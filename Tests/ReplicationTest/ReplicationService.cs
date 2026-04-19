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
        private readonly RocksDbSharp.RocksDb _db;
        private readonly string _dbPath;

        public ReplicationService(RocksDbSharp.RocksDb db, string dbPath)
        {
            _db = db;
            _dbPath = dbPath;
        }

        public async Task<ServerStreamingResult<ReplicationFileData>> SyncInitialStateAsync()
        {
            var stream = GetServerStreamingContext<ReplicationFileData>();
            var replicator = new ReplicationSource(_db, _dbPath);

            using (var session = replicator.GetInitialState())
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

        public async Task<ServerStreamingResult<ReplicationBatchData>> SyncUpdatesAsync(ulong startSeq)
        {
            var stream = GetServerStreamingContext<ReplicationBatchData>();
            var replicator = new ReplicationSource(_db, _dbPath);

            ulong currentSeq = startSeq;

            while (!Context.CallContext.CancellationToken.IsCancellationRequested)
            {
                bool hasUpdates = false;

                if (currentSeq < _db.GetLatestSequenceNumber())
                {
                    foreach (var batch in replicator.GetWalUpdates(currentSeq))
                    {
                        hasUpdates = true;
                        await stream.WriteAsync(new ReplicationBatchData
                        {
                            SequenceNumber = batch.SequenceNumber,
                            Data = batch.Data
                        });
                    }
                }

                if (hasUpdates)
                {
                    currentSeq = _db.GetLatestSequenceNumber() + 1;
                }
                else
                {
                    await Task.Delay(100, Context.CallContext.CancellationToken);
                }
            }

            return stream.Result();
        }
    }
}
