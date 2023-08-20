using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Shared
{
    public class SyncSessionRequest
    {
        public ulong LastSequenceNumber { get; set; }
    }
}
