using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Master
{
    public class SessionRequest
    {
        public DateTime ExpirationTime { get; set; } = DateTime.UtcNow.AddSeconds(30);
        public ulong StartSequenceNumber { get; set; }
        public string SessionKey { get; set; }

        public SessionRequest(ulong startSequenceNumber, string sessionKey)
        {
            StartSequenceNumber = startSequenceNumber;
            SessionKey = sessionKey;
        }
    }
}
