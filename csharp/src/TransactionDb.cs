using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Transitional;

namespace RocksDbSharp
{
    /// <summary>
    /// RocksDb Database with Transaction support
    /// </summary>
    public class TransactionDb : RocksDb
    {
        public static TransactionDbOptions TransactionDbOptions { get; set; } = new TransactionDbOptions();
        internal static TransactionOptions DefaultTransactionOptions { get; set; } = new TransactionOptions();
        internal static TransactionOptions DefaultTransactionOptionsSetSnapshot { get; set; } = new TransactionOptions(true);

        private TransactionDb(IntPtr handle, dynamic optionsReferences, dynamic cfOptionsRefs, Dictionary<string, ColumnFamilyHandleInternal> columnFamilies = null) : base(handle, (object)optionsReferences, (object)cfOptionsRefs, columnFamilies: columnFamilies)
        {
        }

        public new static TransactionDb Open(OptionsHandle options, string path)
        {
            using (var pathSafe = new RocksSafePath(path))
            {
                IntPtr db = Native.Instance.rocksdb_transactiondb_open(options.Handle, TransactionDbOptions.Handle, pathSafe.Handle);
                return new TransactionDb(db, optionsReferences: null, cfOptionsRefs: null);
            }
        }

        /// <summary>
        /// Starts a new Transaction. Caller is responsible for calling Dispose() on the returned transaction when it is no longer needed.
        /// </summary>
        /// <param name="writeOptions"></param>
        /// <param name="transactionOptions"></param>
        /// <returns></returns>
        public Transaction BeginTransaction(bool setSnapshot = false, WriteOptions writeOptions = null, TransactionOptions transactionOptions = null)
        {
            var transaction = new Transaction(this,
                writeOptions ?? DefaultWriteOptions,
                transactionOptions ?? (setSnapshot ? DefaultTransactionOptionsSetSnapshot : DefaultTransactionOptions));

            return transaction;
        }
    }
}
