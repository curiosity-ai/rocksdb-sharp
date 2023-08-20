using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Shared
{
    public class SyncDatabaseDumpInfo
    {
        public List<SyncFileInfo> Files { get; set; } = new List<SyncFileInfo>();
    }
}
