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
    }
}
