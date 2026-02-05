using System;
using System.Collections.Generic;
using System.IO;

namespace RocksDbSharp
{
    public class ReplicationFile : IDisposable
    {
        public string FileName { get; set; }
        public ulong FileSize { get; set; }
        public Stream FileStream { get; set; }

        public void Dispose()
        {
            FileStream?.Dispose();
        }
    }

    public class ReplicationBatch
    {
        public ulong SequenceNumber { get; set; }
        public byte[] Data { get; set; }
    }

    public class ReplicationSession : IDisposable
    {
        private readonly string _tempPath;

        public ReplicationSession(string tempPath)
        {
            _tempPath = tempPath;
        }

        public IEnumerable<ReplicationFile> Files
        {
            get
            {
                foreach (var filePath in Directory.GetFiles(_tempPath))
                {
                    var fileName = Path.GetFileName(filePath);
                    var fileInfo = new FileInfo(filePath);
                    var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    yield return new ReplicationFile
                    {
                        FileName = fileName,
                        FileSize = (ulong)fileInfo.Length,
                        FileStream = stream
                    };
                }
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempPath))
            {
                Directory.Delete(_tempPath, true);
            }
        }
    }
}
