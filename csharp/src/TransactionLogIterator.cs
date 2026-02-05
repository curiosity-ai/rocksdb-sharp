using System;
using System.Runtime.InteropServices;

namespace RocksDbSharp
{
    public class TransactionLogIterator : IDisposable
    {
        public IntPtr Handle { get; private set; }

        internal TransactionLogIterator(IntPtr handle)
        {
            Handle = handle;
        }

        public bool Valid()
        {
            return Native.Instance.rocksdb_wal_iter_valid(Handle);
        }

        public void Next()
        {
            Native.Instance.rocksdb_wal_iter_next(Handle);
        }

        public void Status()
        {
            Native.Instance.rocksdb_wal_iter_status(Handle);
        }

        public unsafe WriteBatch GetBatch(out ulong sequenceNumber)
        {
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
