using System;
using System.IO;

namespace RocksDbSharp
{
    public class ReplicationConsumer
    {
        private readonly RocksDb _db;

        public ReplicationConsumer(RocksDb db)
        {
            _db = db;
        }

        public static void IngestFile(ReplicationFile file, string destinationDbPath)
        {
            Directory.CreateDirectory(destinationDbPath);

            string destPath = Path.Combine(destinationDbPath, file.FileName);

            using (var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write))
            {
                file.FileStream.CopyTo(fileStream);
            }
        }

        public void IngestBatch(ReplicationBatch batch)
        {
            if (_db == null) throw new InvalidOperationException("DB is not initialized.");

            using (var writeBatch = new WriteBatch(batch.Data))
            {
                _db.Write(writeBatch);
            }
        }

#if !NETSTANDARD2_0
        /// <summary>
        /// Applies a chunk produced by <see cref="WalTailer"/> - a run of the primary's write batches
        /// already merged into one - as a single write, which is what keeps the replica's per-record
        /// cost off the database's write path.
        /// </summary>
        public void IngestChunk(ReadOnlySpan<byte> chunk)
        {
            if (_db == null) throw new InvalidOperationException("DB is not initialized.");

            using (var writeBatch = WriteBatch.FromSpan(chunk))
            {
                _db.Write(writeBatch);
            }
        }

        public void IngestBatch(ulong sequenceNo, ReadOnlySpan<byte> batchData)
        {
            if (_db == null) throw new InvalidOperationException("DB is not initialized.");

            using (var writeBatch = WriteBatch.FromSpan(batchData))
            {
                _db.Write(writeBatch);
            }
        }
#endif

    }

}
