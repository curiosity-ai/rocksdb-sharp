using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using RocksDbSharp.Replication.Slave;

namespace RocksDbSharp.Replication.Master
{
    public class RocksDBReplicationServiceServer
    {
        public ReplicatedDbMaster Master { get; set; }
        public int Port { get; set; }
        public IPAddress IPAddress { get; set; }

        public Dictionary<ulong, RocksDBReplicationMasterSession> Sessions { get; set; } = new Dictionary<ulong, RocksDBReplicationMasterSession>();



        public RocksDBReplicationServiceServer(ReplicatedDbMaster master, IPAddress ipAddress, int port)
        {
            IPAddress = ipAddress;
            Master = master;
            Port = port;
        }

        /// <summary>
        /// Starts the server in a new thread
        /// </summary>
        public void RunAsync()
        {
            var serviceThread = new Thread(RunServiceThread);
            serviceThread.IsBackground = true;
            serviceThread.Start();
        }

        private void RunServiceThread()
        {
            var listener = new TcpListener(IPAddress, Port);
            listener.Start();

            ulong sessionId = 0;

            while (true)
            {
                var client = listener.AcceptTcpClient();
                sessionId++;

                var session = new RocksDBReplicationMasterSession(sessionId, this, client);
                Sessions.Add(sessionId, session);
                session.StartAsync();
            }
        }
    }
}
