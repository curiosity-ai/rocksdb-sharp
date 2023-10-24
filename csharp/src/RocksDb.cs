using System;
using System.Collections.Generic;
using System.Linq;

namespace RocksDbSharp
{
    public sealed class RocksDb : RocksDbBase
    {
        private RocksDb(IntPtr handle, dynamic optionsReferences, dynamic cfOptionsRefs, Dictionary<string, ColumnFamilyHandleInternal> columnFamilies = null)
            : base(handle, (object)optionsReferences, (object)cfOptionsRefs, columnFamilies)
        {
        }

        protected override void ReleaseUnmanagedResources()
        {
            base.ReleaseUnmanagedResources();

            if (Handle == IntPtr.Zero)
                return;

            var handle = Handle;
            Handle = IntPtr.Zero;
            Native.Instance.rocksdb_close(handle);
        }

        public static RocksDb Open(OptionsHandle options, string path)
        {
            using (var pathSafe = new RocksSafePath(path))
            {
                IntPtr db = Native.Instance.rocksdb_open(options.Handle, pathSafe.Handle);
                return new RocksDb(db, optionsReferences: null, cfOptionsRefs: null);
            }
        }

        public static RocksDb OpenReadOnly(OptionsHandle options, string path, bool errorIfLogFileExists)
        {
            using (var pathSafe = new RocksSafePath(path))
            {
                IntPtr db = Native.Instance.rocksdb_open_for_read_only(options.Handle, pathSafe.Handle, errorIfLogFileExists);
                return new RocksDb(db, optionsReferences: null, cfOptionsRefs: null);
            }
        }

        public static RocksDb OpenAsSecondary(OptionsHandle options, string path, string secondaryPath)
        {
            using (var pathSafe = new RocksSafePath(path))
            using (var secondaryPathSafe = new RocksSafePath(secondaryPath))
            {
                IntPtr db = Native.Instance.rocksdb_open_as_secondary(options.Handle, pathSafe.Handle, secondaryPathSafe.Handle);
                return new RocksDb(db, optionsReferences: null, cfOptionsRefs: null);
            }
        }

        public static RocksDb OpenWithTtl(OptionsHandle options, string path, int ttlSeconds)
        {
            using (var pathSafe = new RocksSafePath(path))
            {
                IntPtr db = Native.Instance.rocksdb_open_with_ttl(options.Handle, pathSafe.Handle, ttlSeconds);
                return new RocksDb(db, optionsReferences: null, cfOptionsRefs: null);
            }
        }

        public static RocksDb Open(DbOptions options, string path, ColumnFamilies columnFamilies)
        {
            using (var pathSafe = new RocksSafePath(path))
            {
                string[] cfnames = columnFamilies.Names.ToArray();
                IntPtr[] cfoptions = columnFamilies.OptionHandles.ToArray();
                IntPtr[] cfhandles = new IntPtr[cfnames.Length];
                IntPtr db = Native.Instance.rocksdb_open_column_families(options.Handle, pathSafe.Handle, cfnames.Length, cfnames, cfoptions, cfhandles);
                var cfHandleMap = new Dictionary<string, ColumnFamilyHandleInternal>();
                foreach (var pair in cfnames.Zip(cfhandles.Select(cfh => new ColumnFamilyHandleInternal(cfh)), (name, cfh) => new { Name = name, Handle = cfh }))
                {
                    cfHandleMap.Add(pair.Name, pair.Handle);
                }

                return new RocksDb(db,
                    optionsReferences: options.References,
                    cfOptionsRefs: columnFamilies.Select(cfd => cfd.Options.References).ToArray(),
                    columnFamilies: cfHandleMap);
            }
        }

        public static RocksDb OpenReadOnly(DbOptions options, string path, ColumnFamilies columnFamilies, bool errIfLogFileExists)
        {
            using (var pathSafe = new RocksSafePath(path))
            {
                string[] cfnames = columnFamilies.Names.ToArray();
                IntPtr[] cfoptions = columnFamilies.OptionHandles.ToArray();
                IntPtr[] cfhandles = new IntPtr[cfnames.Length];
                IntPtr db = Native.Instance.rocksdb_open_for_read_only_column_families(options.Handle, pathSafe.Handle, cfnames.Length, cfnames, cfoptions, cfhandles, errIfLogFileExists);
                var cfHandleMap = new Dictionary<string, ColumnFamilyHandleInternal>();
                foreach (var pair in cfnames.Zip(cfhandles.Select(cfh => new ColumnFamilyHandleInternal(cfh)), (name, cfh) => new { Name = name, Handle = cfh }))
                {
                    cfHandleMap.Add(pair.Name, pair.Handle);
                }

                return new RocksDb(db,
                    optionsReferences: options.References,
                    cfOptionsRefs: columnFamilies.Select(cfd => cfd.Options.References).ToArray(),
                    columnFamilies: cfHandleMap);
            }
        }

        public static RocksDb OpenAsSecondary(DbOptions options, string path, string secondaryPath, ColumnFamilies columnFamilies)
        {
            using (var pathSafe = new RocksSafePath(path))
            using (var secondaryPathSafe = new RocksSafePath(secondaryPath))
            {
                string[] cfnames = columnFamilies.Names.ToArray();
                IntPtr[] cfoptions = columnFamilies.OptionHandles.ToArray();
                IntPtr[] cfhandles = new IntPtr[cfnames.Length];
                var db = Native.Instance.rocksdb_open_as_secondary_column_families(options.Handle, pathSafe.Handle, secondaryPathSafe.Handle, cfnames.Length, cfnames, cfoptions, cfhandles);
                var cfHandleMap = new Dictionary<string, ColumnFamilyHandleInternal>();
                foreach (var pair in cfnames.Zip(cfhandles.Select(cfh => new ColumnFamilyHandleInternal(cfh)), (name, cfh) => new { Name = name, Handle = cfh }))
                {
                    cfHandleMap.Add(pair.Name, pair.Handle);
                }
                return new RocksDb(db,
                    optionsReferences: options.References,
                    cfOptionsRefs: columnFamilies.Select(cfd => cfd.Options.References).ToArray(),
                    columnFamilies: cfHandleMap);
            }
        }
    }
}
