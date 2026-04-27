using System;
using System.Collections.Generic;
using System.IO;

namespace RocksDbSharp
{
    //Note: DisableFileDeletions() should be set for the database in order to correctly replicate data
    public class ReplicationSource
    {
        private readonly RocksDb _db;

        public ReplicationSource(RocksDb db)
        {
            _db = db;
        }

        public ReplicationSession GetInitialState(string tempPath)
        {
            using (var cp = _db.Checkpoint())
            {
                cp.Save(tempPath);
            }
            return new ReplicationSession(tempPath);
        }

        public IEnumerable<ReplicationBatch> GetWalUpdates(ulong sequenceNumber)
        {
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
        }

        
        public IEnumerable<PooledReplicationBatch> GetPooledWalUpdates(ulong sequenceNumber)
        {
            
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
        }
    }
}
