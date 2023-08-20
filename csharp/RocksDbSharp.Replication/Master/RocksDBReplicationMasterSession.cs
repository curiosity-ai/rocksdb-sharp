using RocksDbSharp.Replication.Master;
using RocksDbSharp.Replication.Shared;
using RocksDbSharp.Replication.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Slave
{
    public class RocksDBReplicationMasterSession
    {
        private TcpClient _client;
        private Stream _stream;

        public RocksDBReplicationServiceServer Server { get; set; }

        private ulong _currentSequenceNumber;
        private byte[] _dataBuffer = new byte[1024 * 1024 * 32];
        private nint _prevSequenceNumber = 0;


        /// <summary>
        /// Start a new slave session
        /// </summary>
        /// <param name="slave"></param>
        /// <param name="endpoint"></param>
        /// <param name="port"></param>
        /// <param name="sessionKey"></param>
        public RocksDBReplicationMasterSession(ulong sessionId, RocksDBReplicationServiceServer server, TcpClient tcpClient)
        {
            _client = tcpClient;
            _stream = _client.GetStream();
            _client.NoDelay = true;

            this.Server = server;
        }


        /// <summary>
        /// Start the replication sessio
        /// </summary>
        public async Task StartAsync()
        {
            var key = await ReadSessionKeyAsync();
            if (key == null || (!Server.Master.SessionRequests.Remove(key, out var sessionRequest)))
            {
                // TODO: Log
                Disconnect();
                return;
            }

            _currentSequenceNumber = sessionRequest.StartSequenceNumber;

            // Start the session thread
            var thread = new Thread(ProcessSessionStream);
            thread.IsBackground = true;
            thread.Start();
        }

        private void ProcessSessionStream()
        {
            try
            {
                var startIterator = _currentSequenceNumber == 0 ? 0 : _currentSequenceNumber - 1;

                while (true)
                {
                    if (Server.Master.DB.GetLastSequenceNumber() > _currentSequenceNumber)
                    {
                        break;
                    }

                    Thread.Sleep(1);
                }


                _prevSequenceNumber = (nint)_currentSequenceNumber;
                var _iterator = Server.Master.DB.GetUpdatesSince(startIterator);
                if(_iterator == null)
                {
                    Disconnect();
                    return;
                }



                if (_currentSequenceNumber > 0)
                {
                    _iterator.Next();
                    _iterator.GetBatchData();
                    _prevSequenceNumber = _iterator.CurrentSequenceNumber;
                }

                while (true)
                {
                    while (true)
                    {
                        if (_iterator.CurrentSequenceNumber != (nint)Server.Master.DB.GetLastSequenceNumber())
                        {
                            break;
                        }

                        Thread.Sleep(1);
                    }

                    _iterator.Next();
                    _iterator.GetBatchData();

                    var wb = _iterator.CurrentWriteBatch;
                    if (_prevSequenceNumber + 1 != _iterator.CurrentSequenceNumber)
                    {
                        // TODO: Log
                        Disconnect();
                        return;
                    }

                    var batchSize = wb.ToBytes(_dataBuffer, 4, _dataBuffer.Length);
                    ByteUtil.WriteInt32(_dataBuffer, batchSize, 0);

                    _prevSequenceNumber = _iterator.CurrentSequenceNumber;

                    _stream.Write(_dataBuffer, 0, batchSize + 4);
                }
            }
            catch
            {
                Disconnect();
            }
        }

        /// <summary>
        /// Read the session key
        /// </summary>
        /// <returns></returns>
        private async Task<string?> ReadSessionKeyAsync()
        {
            var keySizeBuffer = new byte[4];
            await _stream.ReadExactlyAsync(keySizeBuffer, 0, keySizeBuffer.Length);
            int keySize = BitConverter.ToInt32(keySizeBuffer);
            if(keySize > 1024)
            {
                // TODO: log
                Disconnect();
                return null;
            }


            var keyBuffer = new byte[keySize];
            await _stream.ReadExactlyAsync(keyBuffer, 0, keyBuffer.Length);
            var key = Encoding.UTF8.GetString(keyBuffer, 0, keySize);
            return key;
        }

        void Disconnect()
        {
            try
            {
                _stream.Close();
            }
            catch { }

            try
            {
                _stream.Dispose();
            }
            catch { }
        }
    }
}
