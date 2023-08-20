using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RocksDbSharp.Replication.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Master.Controllers
{
    [ApiController]
    public class SessionController : ControllerBase
    {
        private ReplicatedDbMaster _replicationMaster;

        public SessionController(ReplicatedDbMaster replicationMaster)
        {
            _replicationMaster = replicationMaster;
        }

        [Route("~/session/register")]
        [HttpPost]
        public async Task<SyncSessionResponse> RegisterAsync(SyncSessionRequest request)
        {
            ulong lastSequence = 0;
            if(_replicationMaster.DB.GetLastSequenceNumber() > 0)
            {
                ulong num = request.LastSequenceNumber == 0 ? 0 : request.LastSequenceNumber - 1;

                using var iterator = _replicationMaster.DB.GetUpdatesSince(num);
                if (iterator == null || !iterator.Valid() || iterator.Status() != 0)
                {
                    return new SyncSessionResponse()
                    {
                        Success = false,
                        RocksDbInfo = new RocksDbInfo()
                        {
                            Version = "1"
                        },
                        SessionKey = null,
                        Error = "Could not get log iterator."
                    };
                }

                var batch = iterator.GetBatchData();
                if (batch == null || (ulong)iterator.CurrentSequenceNumber != request.LastSequenceNumber - 1)
                {
                    return new SyncSessionResponse()
                    {
                        Success = false,
                        RocksDbInfo = new RocksDbInfo()
                        {
                            Version = "1"
                        },
                        SessionKey = null,
                        Error = "Sequence number invalid or too low. DB is out of sync."
                    };
                }

                lastSequence = request.LastSequenceNumber;
            }

            var sessionKey = $"{Guid.NewGuid()}.{Guid.NewGuid()}";
            _replicationMaster.SessionRequests.Add(sessionKey, new SessionRequest(lastSequence, sessionKey));

            return new SyncSessionResponse()
            {
                Success = true,
                RocksDbInfo = new RocksDbInfo()
                {
                    Version = "1"
                },
                SessionKey = sessionKey,
                Error = null
            };
        }
    }
}
