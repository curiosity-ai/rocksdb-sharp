using System;

namespace RocksDbSharp
{
    public class TransactionOptions
    {
        public TransactionOptions()
        {
            Handle = Native.Instance.rocksdb_transaction_options_create();
        }

        public IntPtr Handle { get; private set; }

        ~TransactionOptions()
        {
            if (Handle == IntPtr.Zero)
                return;

#if !NODESTROY
            Native.Instance.rocksdb_transaction_options_destroy(Handle);
#endif
            Handle = IntPtr.Zero;
        }

        /// <summary>
        /// Setting the setSnapshot to true is the same as calling Transaction.SetSnapshot().
        /// Default: false
        /// </summary>
        /// <param name="value">Whether to set a snapshot</param>
        /// <returns></returns>
        public TransactionOptions SetSetSnapshot(bool value)
        {
            Native.Instance.rocksdb_transaction_options_set_set_snapshot(Handle, value);
            return this;
        }

        /// <summary>
        /// Setting to true means that before acquiring locks, this transaction will check if doing so will cause a deadlock.
        /// If so, it will return with Status.Code.Busy.
        /// The user should retry their transaction.
        /// </summary>
        /// <param name="value">true if we should detect deadlocks.</param>
        /// <returns></returns>
        public TransactionOptions SetDeadlockDetect(bool value)
        {
            Native.Instance.rocksdb_transaction_options_set_deadlock_detect(Handle, value);
            return this;
        }

        /// <summary>
        /// If positive, specifies the wait timeout in milliseconds when a transaction attempts to lock a key.
        /// If 0, no waiting is done if a lock cannot instantly be acquired.
        /// If negative, TransactionDBOptions.GetTransactionLockTimeout(long) will be used
        /// Default: -1
        /// </summary>
        /// <param name="value">The lock timeout in milliseconds</param>
        /// <returns></returns>
        public TransactionOptions SetLockTimeout(long value)
        {
            Native.Instance.rocksdb_transaction_options_set_lock_timeout(Handle, value);
            return this;
        }

        /// <summary>
        /// Expiration duration in milliseconds.
        /// If non-negative, transactions that last longer than this many milliseconds will fail to commit.
        /// If not set, a forgotten transaction that is never committed, rolled back, or deleted will never relinquish any locks it holds.
        /// This could prevent keys from being written by other writers.
        /// Default: -1
        /// </summary>
        /// <param name="value">The expiration duration in milliseconds</param>
        /// <returns></returns>
        public TransactionOptions SetExpiration(long value)
        {
            Native.Instance.rocksdb_transaction_options_set_expiration(Handle, value);
            return this;
        }

        /// <summary>
        /// Sets the number of traversals to make during deadlock detection.
        /// Default: 50
        /// </summary>
        /// <param name="value">The number of traversals to make during deadlock detection</param>
        /// <returns></returns>
        public TransactionOptions SetDeadlockDetectDepth(long value)
        {
            Native.Instance.rocksdb_transaction_options_set_deadlock_detect_depth(Handle, value);
            return this;
        }

        /// <summary>
        /// Set the maximum number of bytes that may be used for the write batch.
        /// </summary>
        /// <param name="value">The maximum number of bytes, 0 means no limit.</param>
        /// <returns></returns>
        public TransactionOptions SetMaxWriteBatchSize(ulong value)
        {
            Native.Instance.rocksdb_transaction_options_set_max_write_batch_size(Handle, (UIntPtr)value);
            return this;
        }
    }
}