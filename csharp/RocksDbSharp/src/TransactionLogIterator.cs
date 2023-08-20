using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Transitional;

namespace RocksDbSharp
{
    public class TransactionLogIterator : IDisposable
    {
        private IntPtr handle;
        public IntPtr Handle { get { return handle; } }
        public nint CurrentSequenceNumber {get; private set; }
        public WriteBatch CurrentWriteBatch { get; private set; }
        public RocksDb RocksDb { get; private set; }

        internal TransactionLogIterator(RocksDb rocksDb, IntPtr handle, ulong initSequenceNumber)
        {
            this.handle = handle;
            this.RocksDb = rocksDb;
            this.CurrentSequenceNumber = (nint)initSequenceNumber;
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
#if !NODESTROY
                Native.Instance.rocksdb_wal_iter_destroy(handle);
#endif
                handle = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Detach the iterator from its handle but don't dispose the handle
        /// </summary>
        /// <returns></returns>
        public IntPtr Detach()
        {
            var r = handle;
            handle = IntPtr.Zero;
            return r;
        }

        public TransactionLogIterator Next()
        {
            if (!Valid() || Status() != IntPtr.Zero)
            {
                return this;
            }

            Native.Instance.rocksdb_wal_iter_next(handle);
            return this;
        }

        public bool Valid()
        {
            if(RocksDb.GetLastSequenceNumber() <= (ulong)this.CurrentSequenceNumber)
            {
                return false;
            }

            return Native.Instance.rocksdb_wal_iter_valid(handle);
        }

        public IntPtr Status()
        {
            Native.Instance.rocksdb_wal_iter_status(handle, out var status);
            return status;
        }

        public WriteBatch GetBatchData()
        {
            if (!Valid() || Status() != IntPtr.Zero)
            {
                return null;
            }

            nint seqNum = 0;
            var batchHandle = Native.Instance.rocksdb_wal_iter_get_batch(handle, out seqNum);

            var wb = new WriteBatch(batchHandle);
            CurrentSequenceNumber = seqNum;
            CurrentWriteBatch = wb;

            return wb;
        }
    }
}
