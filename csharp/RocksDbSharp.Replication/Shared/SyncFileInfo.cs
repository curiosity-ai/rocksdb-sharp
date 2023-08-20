using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Shared
{
    public class SyncFileInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int Length { get; set; }
        public string MD5 { get; set; }
    }
}
