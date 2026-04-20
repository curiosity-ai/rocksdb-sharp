using System;
using System.Collections.Generic;
using System.IO;

namespace RocksDbSharp
{
    public class ReplicationSource
    {
        private readonly RocksDb _db;
        private readonly string _dbPath;

        public ReplicationSource(RocksDb db, string dbPath)
        {
            _db = db;
            _dbPath = dbPath;
        }

        public ReplicationSession GetInitialState()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "rocksdb_replication_" + Guid.NewGuid().ToString());
            using (var cp = _db.Checkpoint())
            {
                cp.Save(tempPath);
            }
            return new ReplicationSession(tempPath);
        }

        public IEnumerable<ReplicationBatch> GetWalUpdates(ulong sequenceNumber)
        {
            //_db.DisableFileDeletions();
            using (var iterator = _db.GetUpdatesSince(sequenceNumber))
            {
                while (iterator.Valid())
                {
                    iterator.Status(); // Check for errors

                    var batch = iterator.GetBatch(out ulong seq);

                    try
                    {
                        byte[] data = batch.ToBytes();
                        yield return new ReplicationBatch
                        {
                            SequenceNumber = seq,
                            Data = data,
                        };
                    }
                    finally
                    {
                        batch.Dispose();
                    }

                    iterator.Next();
                }
            }
            //TODO: Need to review the usage of EnableFileDeletions and archived log files
            //      See https://github.com/facebook/rocksdb/wiki/Replication-Helpers -> GetSortedWalFiles 
            //_db.EnableFileDeletions();
        }

        public IEnumerable<PooledReplicationBatch> GetPooledWalUpdates(ulong sequenceNumber)
        {
            //_db.DisableFileDeletions();
            using (var iterator = _db.GetUpdatesSince(sequenceNumber))
            {
                while (iterator.Valid())
                {
                    iterator.Status(); // Check for errors

                    var batch = iterator.GetBatch(out ulong seq);

                    try
                    {
                        byte[] data = batch.ToBytesPooled(out var size);
                        yield return new PooledReplicationBatch
                        {
                            SequenceNumber = seq,
                            PooledData = data,
                            Length = size,
                        };
                    }
                    finally
                    {
                        batch.Dispose();
                    }

                    iterator.Next();
                }
            }

            //TODO: Need to review the usage of EnableFileDeletions and archived log files
            //      See https://github.com/facebook/rocksdb/wiki/Replication-Helpers -> GetSortedWalFiles 
            //_db.EnableFileDeletions();
        }
    }
}
