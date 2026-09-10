using System;
using System.Runtime.InteropServices;

namespace RocksDbSharp
{
    public class TransactionLogIterator : IDisposable
    {
        private bool m_batchTaken;

        public IntPtr Handle { get; private set; }

        internal TransactionLogIterator(IntPtr handle)
        {
            Handle = handle;
        }

        public bool Valid()
        {
            return Native.Instance.rocksdb_wal_iter_valid(Handle) != 0;
        }

        public void Next()
        {
            m_batchTaken = false;
            Native.Instance.rocksdb_wal_iter_next(Handle);
        }

        public void Status()
        {
            Native.Instance.rocksdb_wal_iter_status(Handle);
        }

        /// <summary>
        /// Takes the batch at the current position. The batch is moved out of the iterator rather than
        /// lent, so it can only be taken once per position - the caller owns what it gets back and has
        /// to <see cref="Next"/> before asking again.
        /// </summary>
        public unsafe WriteBatch GetBatch(out ulong sequenceNumber)
        {
            //Without this the second call dereferences the null the iterator was left holding, which
            //crashes the process instead of raising anything a caller could act on.
            if (m_batchTaken) throw new InvalidOperationException("The batch at this position has already been taken; call Next() before taking another.");

            m_batchTaken = true;

            ulong seq;
            IntPtr writeBatchHandle = Native.Instance.rocksdb_wal_iter_get_batch(Handle, (IntPtr)(&seq));
            sequenceNumber = seq;
            return new WriteBatch(writeBatchHandle);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                Native.Instance.rocksdb_wal_iter_destroy(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }
}
