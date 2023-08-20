using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RocksDbSharp.Backup;
using RocksDbSharp.Replication.Shared;
using RocksDbSharp.Replication.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Master.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class DumpController : ControllerBase
    {
        private ReplicatedDbMaster _replicationMaster;
        public DumpController(ReplicatedDbMaster replicationMaster)
        {
            _replicationMaster = replicationMaster;
        }

        [Route("~/dump/download")]
        [HttpGet]
        public async Task DownloadDumpAsync()
        {
            var backupName = $"backup-{Guid.NewGuid()}";
            try
            {
                Response.Headers.ContentType = "application/zip";
                backupName = CreateBackup(backupName);
                ZipUtil.ZipDirectoryToStreamAsync(backupName, Response.Body);
            }
            finally
            {
                try
                {
                    Directory.Delete(backupName, true);
                }
                catch(Exception e) { 
                    // TODO log
                }
            }
        }

        private string CreateBackup(string backupName)
        {
            var backupOptions = new BackupEngineOptions(backupName);
            var be = BackupEngine.Open(backupOptions, Env.CreateDefaultEnv());
            be.CreateBackup(_replicationMaster.DB);

            return backupName;
        }
    }
}
