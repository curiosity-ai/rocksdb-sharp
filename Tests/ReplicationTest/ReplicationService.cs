using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MagicOnion;
using MagicOnion.Server;
using RocksDbSharp;

namespace ReplicationTest
{
    public class ReplicationService : ServiceBase<IReplicationService>, IReplicationService
    {
        private readonly RocksDb _db;
        private readonly AdaptiveCommitDelayController _commitDelayController;
        //Static, because MagicOnion builds a service instance per call. As instance state these were reset
        //on every request: the running minimum below never saw a previous value, and the initial-state flag
        //was set on one instance and read on another, so the guard it exists for never fired.
        private static readonly ConcurrentDictionary<int, ulong> _lastSyncedPerReplica = new();
        private static ulong _lastSyncedSequenceNumber      = ulong.MaxValue;
        private static int   _replicatingInitialStateCount  = 0;

        public ReplicationService(RocksDb db, AdaptiveCommitDelayController commitDelayController)
        {
            _db = db;
            _db.DisableFileDeletions(); //Must be disabled for this to work
            _commitDelayController = commitDelayController;
        }

        public UnaryResult<bool> ReportLastSyncSequenceNumber(int replicaIndex, ulong seqNumber)
        {
            //The slowest replica decides what may be retired, so this is a minimum across replicas, not
            //across time - one replica reporting 100 after another reported 200 must not move it back.
            _lastSyncedPerReplica[replicaIndex] = seqNumber;

            ulong slowest = seqNumber;

            foreach (var reported in _lastSyncedPerReplica.Values)
            {
                if (reported < slowest) slowest = reported;
            }

            _lastSyncedSequenceNumber = slowest;

            if (Volatile.Read(ref _replicatingInitialStateCount) > 0) return new UnaryResult<bool>(false);

            if (_lastSyncedSequenceNumber != ulong.MaxValue && _lastSyncedSequenceNumber != 0)
            {
                MaybeDeleteOldWalFiles();
                _commitDelayController.ReportLag(new ReplicaLagSample(replicaIndex, (long)(_db.GetLatestSequenceNumber() - seqNumber)));
            }

            return new UnaryResult<bool>(true);
        }

        private void MaybeDeleteOldWalFiles()
        {
            var seqNoPerWalFile = RocksDbWalInspector.GetFirstSequenceNumbers(_db.WalPath);

            var seqNoPerWalFileId = seqNoPerWalFile.Select(kv => (id: int.Parse(kv.Key.AsSpan(0, kv.Key.Length - ".log".Length)), seqNo: (ulong)kv.Value, fileName: kv.Key))
                                                   .OrderBy(kv => kv.id)
                                                   .ToArray();

            foreach (var (walID, startSeqNo, fileName) in seqNoPerWalFileId)
            {
                var nextWalByID = seqNoPerWalFileId.Where(d => d.id > walID).FirstOrDefault();

                if (nextWalByID.id != default)
                {
                    var endSeqNumber = nextWalByID.seqNo - 1;
                    if (endSeqNumber < _lastSyncedSequenceNumber)
                    {
                        Console.WriteLine($"[Primary] Deleting WAL file: {fileName} with {startSeqNo:n0}..{endSeqNumber:n0} < last sync'd {_lastSyncedSequenceNumber:n0}");
                        File.Delete(Path.Combine(_db.WalPath, fileName));
                    }
                }
            }
        }

        public async Task<ServerStreamingResult<ReplicationFileData>> SyncInitialStateAsync()
        {
            //While a checkpoint is being shipped, no WAL file may be retired - the replica will ask for
            //updates from the sequence number that checkpoint ended at. Counted rather than a flag so two
            //replicas syncing at once cannot have one clear it for the other, and released in a finally so
            //a failed transfer does not leave retention switched off for good.
            Interlocked.Increment(ref _replicatingInitialStateCount);

            try
            {
                var stream = GetServerStreamingContext<ReplicationFileData>();

                var replicator = new ReplicationSource(_db);

                var tempPath = Path.Combine(Path.GetTempPath(), "rocksdb_replication_" + Guid.NewGuid().ToString());

                using (var session = replicator.GetInitialState(tempPath))
                {
                    foreach (var file in session.Files)
                    {
                        using (var memoryStream = new MemoryStream())
                        {
                            await file.FileStream.CopyToAsync(memoryStream);
                            var data = new ReplicationFileData
                            {
                                FileName = file.FileName,
                                FileSize = file.FileSize,
                                Content = memoryStream.ToArray()
                            };
                            await stream.WriteAsync(data);
                        }
                        file.Dispose();
                    }
                }

                return stream.Result();
            }
            finally
            {
                Interlocked.Decrement(ref _replicatingInitialStateCount);
            }
        }

        public async Task<ServerStreamingResult<ReplicationBatchData>> SyncUpdatesAsync(ulong startSeq)
        {
            var stream = GetServerStreamingContext<ReplicationBatchData>();
            var token  = Context.CallContext.CancellationToken;

            var replicator = new ReplicationSource(_db);

            long chunkCount = 0, entryCount = 0, byteCount = 0, waitCount = 0;
            long readTicks  = 0, writeTicks = 0;
            var  report     = Stopwatch.StartNew();

            //One long-lived tailer for the whole stream: a chunk is a run of the primary's write batches
            //merged into one, so the replica applies it with a single write and stays on the primary's
            //sequence numbers.
            using (var tailer = replicator.TailWal(startSeq))
            {
                while (!token.IsCancellationRequested)
                {
                    var  beforeRead = Stopwatch.GetTimestamp();
                    bool hasChunk   = tailer.TryReadChunk(out var chunk);
                    var  afterRead  = Stopwatch.GetTimestamp();

                    readTicks += afterRead - beforeRead;

                    if (hasChunk)
                    {
                        await stream.WriteAsync(new ReplicationBatchData
                        {
                            SequenceNumber = chunk.FirstSequenceNumber,
                            PooledData     = chunk.Buffer,
                            Length         = chunk.Length,
                        });

                        writeTicks += Stopwatch.GetTimestamp() - afterRead;

                        chunkCount++;
                        entryCount += chunk.EntryCount;
                        byteCount  += chunk.Length;
                    }
                    else
                    {
                        waitCount++;

                        try
                        {
                            await tailer.WaitForUpdatesAsync(token);
                        }
                        catch (OperationCanceledException)
                        {
                            break; //The replica disconnected, or the host is shutting down.
                        }
                    }

                    if (report.ElapsedMilliseconds >= 2000)
                    {
                        double ticksPerMs = Stopwatch.Frequency / 1000.0;

                        Console.WriteLine($"[Src] chunks={chunkCount:n0} entries={entryCount:n0} bytes={byteCount:n0} waits={waitCount:n0} | read={readTicks / ticksPerMs:n0}ms write={writeTicks / ticksPerMs:n0}ms | entriesPerChunk={(chunkCount == 0 ? 0 : (double)entryCount / chunkCount):n0} reopens={tailer.Reopens:n0} lagSeq={tailer.LagInSequenceNumbers:n0}");

                        chunkCount = entryCount = byteCount = waitCount = readTicks = writeTicks = 0;
                        report.Restart();
                    }
                }
            }

            return stream.Result();
        }
    }
}
