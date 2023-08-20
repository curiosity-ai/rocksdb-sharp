using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Backup
{
    public class BackupEngineOptions : IDisposable
    {
        public IntPtr Handle { get; set; }
        public string Path { get; set; }

        /// <summary>
        /// Where to keep the DB files. Has to be different than the DB name.
        /// </summary>
        /// <param name="path"></param>
        public BackupEngineOptions(string path)
        {
            Path = path;
            Handle = Native.Instance.rocksdb_backup_engine_options_create(path);
        }

        ~BackupEngineOptions()
        {
            Native.Instance.rocksdb_backup_engine_options_destroy(Handle);
        }

        public void Dispose()
        {
        }
    }
}
