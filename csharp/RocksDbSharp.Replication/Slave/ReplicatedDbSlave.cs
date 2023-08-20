using RocksDbSharp.Replication.Shared;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using RocksDbSharp.Backup;

namespace RocksDbSharp.Replication.Slave
{
    public class ReplicatedDbSlave
    {
        private HttpClient _http = new HttpClient();

        public string DbPath { get; set; }
        public RocksDb Db { get; set; }
        public int ControlPort { get; set; }
        public int ServicePort { get; set; }
        public string MasterEndpoint { get; set; }
        private RocksDBReplicationSlaveSession? SlaveSession { get; set; }
        public OptionsHandle DbOptions { get; private set; }

        public ReplicatedDbSlave(OptionsHandle options, string path, string masterEndpoint, int controlPort, int servicePort, string authKey)
        {
            DbOptions = options;
            DbPath = path;
            Db = RocksDb.Open(options, path);

            _http.DefaultRequestHeaders.Add("AuthKey", authKey);
            MasterEndpoint = masterEndpoint;
            ControlPort = controlPort;
            ServicePort = servicePort;
        }

        public async Task StartAsync()
        {
            var lastSequence = Db.GetLastSequenceNumber();
            using var resp = await _http.PostAsJsonAsync<SyncSessionRequest>($"http://{MasterEndpoint}:{ControlPort}/session/register", new SyncSessionRequest()
            {
                LastSequenceNumber= lastSequence,
            });
            resp.EnsureSuccessStatusCode();

            var response = await resp.Content.ReadFromJsonAsync<SyncSessionResponse>();
            if(response == null)
            {
                throw new Exception("Failed to connect to master");
            }
            if (response.Success)
            {
                if (response.SessionKey == null)
                {
                    throw new Exception("Failed to obtain session key");
                }

                await StartSlaveSessionAsync(response.SessionKey);
            }
            else 
            {
                await DownloadAndRestoreSnapshotAsync();

                lastSequence = Db.GetLastSequenceNumber();
                using var resp2 = await _http.PostAsJsonAsync<SyncSessionRequest>($"http://{MasterEndpoint}:{ControlPort}/session/register", new SyncSessionRequest()
                {
                    LastSequenceNumber = lastSequence,
                });
                resp.EnsureSuccessStatusCode();

                response = await resp.Content.ReadFromJsonAsync<SyncSessionResponse>();

                if (response.SessionKey == null)
                {
                    throw new Exception("Failed to obtain session key");
                }

                await StartSlaveSessionAsync(response.SessionKey);
            }
        }

        private void OpenAbc()
        {

        }

        /// <summary>
        ///  Download the full DB snapshot
        /// </summary>
        /// <returns></returns>
        private async Task DownloadAndRestoreSnapshotAsync()
        {
            var restoreName = $"backup-restore-{Guid.NewGuid()}";
            try
            {
                // Download and extract the ZIP of the backup
                using var zipFileStream = await _http.GetStreamAsync($"http://{MasterEndpoint}:{ControlPort}/dump/download");
                using var zipArchive = new ZipArchive(zipFileStream);
                zipArchive.ExtractToDirectory(restoreName);

                // Close and delete the already opened DB
                Db.Dispose();
                Directory.Delete(DbPath, true);

                // Restore the backup
                var backupOptions = new BackupEngineOptions(restoreName);
                var be = BackupEngine.Open(backupOptions, Env.CreateDefaultEnv());
                be.RestoreDbFromLatestBackup(DbPath, DbPath);
                
                // Reopen the database
                Db = RocksDb.Open(DbOptions, DbPath);
            }
            finally
            {
                if(Directory.Exists(restoreName))
                {
                    Directory.Delete(restoreName, true);
                }
            }

        }

        private async Task StartSlaveSessionAsync(string sessionKey)
        {
            SlaveSession = new RocksDBReplicationSlaveSession(this, this.MasterEndpoint, this.ServicePort, sessionKey);
            await SlaveSession.StartAsync();
        }
    }
}
