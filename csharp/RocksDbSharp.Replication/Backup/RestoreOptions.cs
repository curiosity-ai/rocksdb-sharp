using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Backup
{
    public class RestoreOptions : IDisposable
    {
        public IntPtr Handle { get; set; }

        public RestoreOptions()
        {
            Handle = Native.Instance.rocksdb_restore_options_create();
        }

        ~RestoreOptions()
        {
            Native.Instance.rocksdb_restore_options_destroy(Handle);
        }

        public void Dispose()
        {
        }
    }
}
