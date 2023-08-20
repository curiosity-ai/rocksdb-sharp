using System;

namespace RocksDbSharp
{
    public class TransactionDbOptions
    {
        public TransactionDbOptions()
        {
            Handle = Native.Instance.rocksdb_transactiondb_options_create();
        }

        public IntPtr Handle { get; protected set; }

        ~TransactionDbOptions()
        {
            if (Handle != IntPtr.Zero)
            {
#if !NODESTROY
                Native.Instance.rocksdb_transactiondb_options_destroy(Handle);
#endif
                Handle = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Specifies the maximum number of keys that can be locked at the same time per column family.
        /// </summary>
        /// <param name="maxNumLocks">The maximum number of keys that can be locked; If this value is not positive, no limit will be enforced.</param>
        /// <returns>This TransactionDbOptions instance</returns>
        public TransactionDbOptions SetMaxNumLocks(long maxNumLocks)
        {
            Native.Instance.rocksdb_transactiondb_options_set_max_num_locks(Handle, maxNumLocks);
            return this;
        }

        /// <summary>
        /// Increasing this value will increase the concurrency by dividing the lock table (per column family) into more sub-tables, each with their own separate mutex. Default: 16
        /// </summary>
        /// <param name="numStripes">The number of sub-tables</param>
        /// <returns>This TransactionDbOptions instance</returns>
        public TransactionDbOptions SetNumStripes(uint numStripes)
        {
            var numStripesPtr = (UIntPtr)numStripes;
            Native.Instance.rocksdb_transactiondb_options_set_num_stripes(Handle, numStripesPtr);
            return this;
        }

        /// <summary>
        /// If positive, specifies the default wait timeout in milliseconds when a transaction attempts to lock a key if not specified by TransactionOptions.SetLockTimeout(long) 
        /// If 0, no waiting is done if a lock cannot instantly be acquired. If negative, there is no timeout. Not using a timeout is not recommended as it can lead to deadlocks. 
        /// Currently, there is no deadlock-detection to recover from a deadlock. Default: 1000
        /// </summary>
        /// <param name="transactionLockTimeout">The default wait timeout in milliseconds</param>
        /// <returns>This TransactionDbOptions instance</returns>
        public TransactionDbOptions SetTransactionLockTimeout(long transactionLockTimeout)
        {
            Native.Instance.rocksdb_transactiondb_options_set_transaction_lock_timeout(Handle, transactionLockTimeout);
            return this;
        }

        /// <summary>
        /// If positive, specifies the wait timeout in milliseconds when writing a key OUTSIDE of a transaction
        /// </summary>
        /// <param name="defaultLockTimeout">The timeout in milliseconds when writing a key OUTSIDE of a transaction</param>
        /// <returns>This TransactionDbOptions instance</returns>
        public TransactionDbOptions SetDefaultLockTimeout(long defaultLockTimeout)
        {
            Native.Instance.rocksdb_transactiondb_options_set_default_lock_timeout(Handle, defaultLockTimeout);
            return this;
        }
    }
}
