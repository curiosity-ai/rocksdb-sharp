using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Backup
{
    public class BackupEngine
    {
        public IntPtr Handle { get; set; }

        public BackupEngineOptions Options { get; set; }

        public BackupEngine(IntPtr handle, BackupEngineOptions options)
        {
            Options = options;
            Handle = handle;
        }

        public static BackupEngine Open(BackupEngineOptions options, Env env)
        {
            var engineHandle = Native.Instance.rocksdb_backup_engine_open_opts(options.Handle, env.Handle);
            return new BackupEngine(engineHandle, options);
        }

        public void CreateBackup(RocksDb rocksDb)
        {
            Native.Instance.rocksdb_backup_engine_create_new_backup(Handle, rocksDb.Handle, out nint error);
            if (error != 0)
            {
                throw new Exception($"Failed to create backup with code {error}");
            }
        }

        public void RestoreDbFromLatestBackup(string dbDir, string walDir)
        {
            var options = new RestoreOptions();
            Native.Instance.rocksdb_backup_engine_restore_db_from_latest_backup(Handle, dbDir, walDir, options.Handle, out nint error);
            if (error != 0)
            {
                throw new Exception($"Failed to create backup with code {error}");
            }
        }

        public void RestoreDbFromBackup(uint backupId, string dbDir, string walDir, RestoreOptions restoreOptions)
        {
            Native.Instance.rocksdb_backup_engine_restore_db_from_backup(Handle, dbDir, walDir, restoreOptions.Handle, backupId, out nint error);
            if (error != 0)
            {
                throw new Exception($"Failed to create backup with code {error}");
            }
        }
    }
}
