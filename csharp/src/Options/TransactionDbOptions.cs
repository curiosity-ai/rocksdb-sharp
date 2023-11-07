using System;

namespace RocksDbSharp
{
    public class TransactionDbOptions
    {
        public TransactionDbOptions()
        {
            Handle = Native.Instance.rocksdb_transactiondb_options_create();
        }

        public IntPtr Handle { get; private set; }

        ~TransactionDbOptions()
        {
            if (Handle == IntPtr.Zero)
                return;

#if !NODESTROY
            Native.Instance.rocksdb_transactiondb_options_destroy(Handle);
#endif
            Handle = IntPtr.Zero;
        }

        /// <summary>
        /// Specifies the maximum number of keys that can be locked at the same time per column family.
        /// </summary>
        /// <param name="value">The maximum number of keys that can be locked; If this value is not positive, no limit will be enforced.</param>
        /// <returns></returns>
        public TransactionDbOptions SetMaxNumLocks(long value)
        {
            Native.Instance.rocksdb_transactiondb_options_set_max_num_locks(Handle, value);
            return this;
        }

        /// <summary>
        /// Increasing this value will increase the concurrency by dividing the lock table (per column family) into more sub-tables, each with their own separate mutex.
        /// Default: 16
        /// </summary>
        /// <param name="value">The number of sub-tables</param>
        /// <returns></returns>
        public TransactionDbOptions SetNumStripes(ulong value)
        {
            Native.Instance.rocksdb_transactiondb_options_set_num_stripes(Handle, (UIntPtr)value);
            return this;
        }

        /// <summary>
        /// If positive, specifies the default wait timeout in milliseconds when a transaction attempts to lock a key if not specified by TransactionOptionsSetLockTimeout(long).
        /// If 0, no waiting is done if a lock cannot instantly be acquired.
        /// If negative, there is no timeout. Not using a timeout is not recommended as it can lead to deadlocks.
        /// Currently, there is no deadlock-detection to recover from a deadlock.
        /// Default: 1000
        /// </summary>
        /// <param name="value">The default wait timeout in milliseconds</param>
        /// <returns></returns>
        public TransactionDbOptions SetTransactionLockTimeout(long value)
        {
            Native.Instance.rocksdb_transactiondb_options_set_transaction_lock_timeout(Handle, value);
            return this;
        }

        /// <summary>
        /// If positive, specifies the wait timeout in milliseconds when writing a key OUTSIDE of a transaction (ie by calling put, merge, delete or write directly).
        /// If 0, no waiting is done if a lock cannot instantly be acquired.
        /// If negative, there is no timeout and will block indefinitely when acquiring a lock.
        /// Not using a timeout can lead to deadlocks.
        /// Currently, there is no deadlock-detection to recover from a deadlock.
        /// While DB writes cannot deadlock with other DB writes, they can deadlock with a transaction.
        /// A negative timeout should only be used if all transactions have a small expiration set.
        /// Default: 1000
        /// </summary>
        /// <param name="value">The timeout in milliseconds when writing a key OUTSIDE of a transaction.</param>
        /// <returns></returns>
        public TransactionDbOptions SetDefaultLockTimeout(long value)
        {
            Native.Instance.rocksdb_transactiondb_options_set_default_lock_timeout(Handle, value);
            return this;
        }
    }
}