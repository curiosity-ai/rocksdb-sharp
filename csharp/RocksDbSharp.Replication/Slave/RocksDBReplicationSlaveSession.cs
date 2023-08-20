using RocksDbSharp.Replication.Shared;
using RocksDbSharp.Replication.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Slave
{
    public class RocksDBReplicationSlaveSession
    {
        private TcpClient _client;
        private Stream _stream;

        public ReplicatedDbSlave Slave { get; set; }

        public string SessionKey { get; set; }
        private byte[] _dataBuffer = new byte[1024 * 1024 * 32];

        /// <summary>
        /// Start a new slave session
        /// </summary>
        /// <param name="slave"></param>
        /// <param name="endpoint"></param>
        /// <param name="port"></param>
        /// <param name="sessionKey"></param>
        public RocksDBReplicationSlaveSession(ReplicatedDbSlave slave, string endpoint, int port, string sessionKey)
        {
            _client = new TcpClient(endpoint, port);
            _stream = _client.GetStream();

            this.Slave = slave;
            this.SessionKey = sessionKey;
        }


        /// <summary>
        /// Start the replication sessio
        /// </summary>
        public async Task StartAsync()
        {
            await SendSessionKeyAsync();

            // Start the session thread
            var thread = new Thread(ProcessSessionStream);
            thread.IsBackground = true;
            thread.Start();
        }

        private void ProcessSessionStream()
        {
            while(true)
            {
                _stream.ReadExactly(_dataBuffer, 0, 4);
                var batchSize = ByteUtil.ReadInt32(_dataBuffer, 0);
                _stream.ReadExactly(_dataBuffer, 0, batchSize);

                ProcessBatchData(batchSize);
            }
        }

        private void ProcessBatchData(int batchSize)
        {
            using var writeBatch = new WriteBatch(_dataBuffer, 0, batchSize);
            Slave.Db.Write(writeBatch);
        }

        /// <summary>
        /// Send the session key to the server, this will trigger the session to connect and
        /// start streaming data
        /// </summary>
        private async Task SendSessionKeyAsync()
        {
            var sendBuffer = new byte[Encoding.UTF8.GetByteCount(SessionKey) + 4];
            byte[] intBytes = BitConverter.GetBytes(sendBuffer.Length - 4);

            Buffer.BlockCopy(intBytes, 0, sendBuffer, 0, intBytes.Length);
            Encoding.UTF8.GetBytes(SessionKey, 0, SessionKey.Length, sendBuffer, 4);

            await _stream.WriteAsync(sendBuffer, 0, sendBuffer.Length);
        }
    }
}
