using System;
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
        private ulong _lastSyncedSequenceNumber = ulong.MaxValue;
        private bool _replicatingInitialState = false;

        public ReplicationService(RocksDb db, AdaptiveCommitDelayController commitDelayController)
        {
            _db = db;
            _db.DisableFileDeletions(); //Must be disabled for this to work
            _commitDelayController = commitDelayController;
        }

        public UnaryResult<bool> ReportLastSyncSequenceNumber(int replicaIndex, ulong seqNumber)
        {
            _lastSyncedSequenceNumber = Math.Min(_lastSyncedSequenceNumber, seqNumber);
            
            if (_replicatingInitialState) return new UnaryResult<bool>(false);

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
            _replicatingInitialState = true;

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
            
            _replicatingInitialState = false;

            return stream.Result();
        }

        public async Task<ServerStreamingResult<ReplicationBatchData>> SyncUpdatesAsync(ulong startSeq)
        {
            var stream = GetServerStreamingContext<ReplicationBatchData>();

            var replicator = new ReplicationSource(_db);

            ulong currentSeq = startSeq;

            while (!Context.CallContext.CancellationToken.IsCancellationRequested)
            {
                bool hasUpdates = false;
                ulong lastSequenceNumberSeen = _db.GetLatestSequenceNumber();
                if (currentSeq < lastSequenceNumberSeen)
                {
                    foreach (var batch in replicator.GetPooledWalUpdates(currentSeq))
                    {
                        hasUpdates = true;

                        await stream.WriteAsync(new ReplicationBatchData
                        {
                            SequenceNumber = batch.SequenceNumber,
                            PooledData = batch.PooledData,
                            Length = batch.Length,
                        });

                        lastSequenceNumberSeen = Math.Max(lastSequenceNumberSeen, batch.SequenceNumber);

                        WriteBatch.ReturnPooledBytes(batch.PooledData);
                    }
                }

                if (hasUpdates)
                {
                    currentSeq = lastSequenceNumberSeen; // + 1;
                }
                else
                {
                    await Task.Delay(1, Context.CallContext.CancellationToken);
                }
            }

            return stream.Result();
        }
    }
}
