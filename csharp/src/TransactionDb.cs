using System;
using System.Collections.Generic;
using System.Linq;

namespace RocksDbSharp
{
    public sealed class TransactionDb : RocksDbBase
    {
        internal static TransactionOptions DefaultTransactionOptions { get; set; } = new TransactionOptions();

        private TransactionDb(IntPtr handle, dynamic optionsReferences, dynamic cfOptionsRefs, TransactionDbOptions transactionDbOptions, Dictionary<string, ColumnFamilyHandleInternal> columnFamilies = null)
            : base(handle, (object)optionsReferences, (object)cfOptionsRefs, columnFamilies)
        {
            References.TransactionDbOptions = transactionDbOptions;
        }

        protected override void ReleaseUnmanagedResources()
        {
            base.ReleaseUnmanagedResources();

            if (Handle == IntPtr.Zero)
                return;

            var handle = Handle;
            Handle = IntPtr.Zero;
            Native.Instance.rocksdb_transactiondb_close(handle);
        }

        public static TransactionDb Open(OptionsHandle options, TransactionDbOptions transactionDbOptions, string path)
        {
            using (var pathSafe = new RocksSafePath(path))
            {
                IntPtr db = Native.Instance.rocksdb_transactiondb_open(options.Handle, transactionDbOptions.Handle, pathSafe.Handle);
                return new TransactionDb(db, optionsReferences: options, cfOptionsRefs: null, transactionDbOptions: transactionDbOptions);
            }
        }

        public static TransactionDb Open(DbOptions options, TransactionDbOptions transactionDbOptions, string path, ColumnFamilies columnFamilies)
        {
            using (var pathSafe = new RocksSafePath(path))
            {
                string[] cfnames = columnFamilies.Names.ToArray();
                IntPtr[] cfoptions = columnFamilies.OptionHandles.ToArray();
                IntPtr[] cfhandles = new IntPtr[cfnames.Length];
                IntPtr db = Native.Instance.rocksdb_transactiondb_open_column_families(options.Handle, transactionDbOptions.Handle, pathSafe.Handle, cfnames.Length, cfnames, cfoptions, cfhandles);
                var cfHandleMap = new Dictionary<string, ColumnFamilyHandleInternal>();
                foreach (var pair in cfnames.Zip(cfhandles.Select(cfh => new ColumnFamilyHandleInternal(cfh)), (name, cfh) => new { Name = name, Handle = cfh }))
                {
                    cfHandleMap.Add(pair.Name, pair.Handle);
                }

                return new TransactionDb(db,
                    optionsReferences: options.References,
                    cfOptionsRefs: columnFamilies.Select(cfd => cfd.Options.References).ToArray(),
                    transactionDbOptions: transactionDbOptions,
                    columnFamilies: cfHandleMap);
            }
        }

        public Transaction BeginTransaction(WriteOptions writeOptions = null, TransactionOptions transactionOptions = null)
        {
            return new Transaction(this,
                writeOptions ?? DefaultWriteOptions,
                transactionOptions ?? DefaultTransactionOptions);
        }
    }
}