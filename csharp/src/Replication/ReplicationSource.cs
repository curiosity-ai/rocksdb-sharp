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

#if !NETSTANDARD2_0
        /// <summary>
        /// Opens a long-lived tail on the write-ahead log, starting at <paramref name="sequenceNumber"/>.
        /// This is what a replication stream should use: unlike <see cref="GetWalUpdates"/> /
        /// <see cref="GetPooledWalUpdates"/>, which open a fresh log iterator per call and so pay a scan of
        /// the current WAL file every time they are asked for updates, the tailer keeps its iterator and
        /// hands back whole runs of batches merged into one write batch.
        /// </summary>
        public WalTailer TailWal(ulong sequenceNumber, int maxChunkBytes = WalTailer.DEFAULT_MAX_CHUNK_BYTES)
        {
            return new WalTailer(_db, sequenceNumber, maxChunkBytes);
        }
#endif

        /// <summary>
        /// Reads every batch the log holds from <paramref name="sequenceNumber"/> on, opening a log
        /// iterator for the call. Prefer <see cref="TailWal"/> for a continuous replication stream.
        /// </summary>
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
