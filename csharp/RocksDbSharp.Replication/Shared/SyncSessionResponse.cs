using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Shared
{
    public class SyncSessionResponse
    {
        public string? Error { get; set; }
        public bool Success { get; set; }
        public string? SessionKey { get; set; }
        public RocksDbInfo RocksDbInfo { get; set; }
    }
}
