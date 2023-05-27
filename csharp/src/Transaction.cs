using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;

namespace RocksDbSharp
{
    /// <summary>
    /// A Snapshot is an immutable object and can therefore be safely
    /// accessed from multiple threads without any external synchronization.
    /// </summary>
    public class Transaction : IDisposable
    {
        private TransactionDb db;
        public IntPtr Handle { get; private set; }

        public Snapshot Snapshot { get; private set; } = null;

        public WriteOptions WriteOptions { get; private set; }

        internal Transaction(TransactionDb transactionDb, WriteOptions writeOptions, TransactionOptions transactionOptions)
        {
            this.db = transactionDb;
            Handle = Native.Instance.rocksdb_transaction_begin(
                db.Handle,
                writeOptions.Handle,
                transactionOptions.Handle, IntPtr.Zero);
        }

        /// <summary>
        /// Write all batched keys to the db atomically. 
        /// </summary>
        /// <returns>The status code of the commit. IntPtr.Zero is returned on success. Otherwise, the error code is returned</returns>
        public void Commit()
        {
            Native.Instance.rocksdb_transaction_commit(Handle);
        }

        /// <summary>
        /// Prepares the transaction
        /// </summary>
        public void Prepare()
        {
            Native.Instance.rocksdb_transaction_prepare(Handle);
        }

        /// <summary>
        /// Discard all batched writes in this transaction.
        /// </summary>
        public void Rollback()
        {
            Native.Instance.rocksdb_transaction_rollback(Handle);
        }

        /// <summary>
        /// Records the state of the transaction for future calls to RollbackToSavePoint(). May be called multiple times to set multiple save points.
        /// </summary>
        public void SetSavePoint()
        {
            Native.Instance.rocksdb_transaction_set_savepoint(Handle);
        }

        /// <summary>
        /// Undo all operations in this transaction (put, merge, delete, putLogData) since the most recent call to SetSavePoint() 
        /// and removes the most recent SetSavePoint(). If there is no previous call to SetSavePoint(), returns Status::NotFound()
        /// </summary>
        public void RollbackToSavePoint()
        {
            Native.Instance.rocksdb_transaction_rollback_to_savepoint(Handle);
        }


#if !NETSTANDARD2_0
        /// <summary>
        /// Set the name of the transaction.
        /// </summary>
        /// <param name="name">Transaction name</param>
        public void SetName(string name)
        {
            Native.Instance.rocksdb_transaction_set_name(Handle, name, (nuint)name.Length);
        }

        /// <summary>
        /// Set the name of the transaction.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="nameLength"></param>
        public unsafe void SetName(byte[] name, UIntPtr nameLength)
        {
            fixed (byte* keyPtr = name)
            {
                Native.Instance.rocksdb_transaction_set_name(Handle, (IntPtr)keyPtr, nameLength);
            }    
        }

        /// <summary>
        /// Get the name of the transaction
        /// </summary>
        /// <returns>Transaction name</returns>
        public string? GetName()
        {
            var ptr = Native.Instance.rocksdb_transaction_get_name(Handle, out var nameLength);
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringAnsi(ptr);
        }
#endif

        public Iterator NewIterator(ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            IntPtr iteratorHandle = cf is null
                ? Native.Instance.rocksdb_transactiondb_create_iterator(Handle, (readOptions ?? RocksDb.DefaultReadOptions).Handle)
                : Native.Instance.rocksdb_transactiondb_create_iterator_cf(Handle, (readOptions ?? RocksDb.DefaultReadOptions).Handle, cf.Handle);

            return new Iterator(iteratorHandle, readOptions);
        }

        public string GetForUpdate(string key, bool exclusive, ColumnFamilyHandle cf = null, ReadOptions readOptions = null, Encoding encoding = null)
        {
            var enc = (encoding ?? RocksDb.DefaultEncoding);

            var keyBytes = enc.GetBytes(key);
            var response = GetForUpdate(keyBytes, exclusive, cf, readOptions);
            return enc.GetString(response);
        }

        public byte[] GetForUpdate(byte[] key, bool exclusive, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            var ptr = GetForUpdate(key, key.Length, exclusive, out var valLength, cf, readOptions);
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            var response = new byte[(int)valLength];
            Marshal.Copy(ptr, response, 0, (int)valLength);
            return response;
        }

        public long GetForUpdate(byte[] key, long keyLength, byte[] buffer, long offset, long length, bool exclusive, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            var ptr = GetForUpdate(key, keyLength, exclusive, out var valLength, cf, readOptions);
            if (ptr == IntPtr.Zero)
            {
                return -1;
            }

            var copyLength = Math.Min(length, (int)valLength);
            Marshal.Copy(ptr, buffer, (int)offset, (int)copyLength);
            Native.Instance.rocksdb_free(ptr);
            return (long)valLength;
        }

        public string Get(string key, bool exclusive, ColumnFamilyHandle cf = null, ReadOptions readOptions = null, Encoding encoding = null)
        {
            var enc = (encoding ?? RocksDb.DefaultEncoding);

            var keyBytes = enc.GetBytes(key);
            var response = Get(keyBytes, cf, readOptions);
            return enc.GetString(response);
        }

        public byte[] Get(byte[] key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            var ptr = Get(key, key.Length, out var valLength, cf, readOptions);
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            var response = new byte[(int)valLength];
            Marshal.Copy(ptr, response, 0, (int)valLength);
            return response;
        }

        public long Get(byte[] key, long keyLength, byte[] buffer, long offset, long length, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            var ptr = Get(key, keyLength, out var valLength, cf, readOptions);
            if (ptr == IntPtr.Zero)
            {
                return -1;
            }

            var copyLength = Math.Min(length, (int)valLength);
            Marshal.Copy(ptr, buffer, (int)offset, (int)copyLength);
            Native.Instance.rocksdb_free(ptr);
            return (long)valLength;
        }

        public void Delete(string key, ColumnFamilyHandle cf = null, Encoding encoding = null)
        {
            var enc = (encoding ?? RocksDb.DefaultEncoding);
            var keyBytes = enc.GetBytes(key);

            Delete(keyBytes, keyBytes.Length, cf);
        }

        public void Delete(byte[] key, ColumnFamilyHandle cf = null) =>
            Delete(key, key.Length, cf);

        public void Delete(byte[] key, long keyLength, ColumnFamilyHandle cf = null)
        {
            if(cf is null)
            {
                Native.Instance.rocksdb_transaction_delete(Handle, key, (UIntPtr)keyLength);
            }
            else
            {
                Native.Instance.rocksdb_transaction_delete_cf(Handle, cf.Handle, key, (UIntPtr)keyLength);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IntPtr Get(byte[] key, long keyLength, out UIntPtr valLength, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            var ptr = cf is null
                ? Native.Instance.rocksdb_transaction_get(Handle, (readOptions ?? RocksDb.DefaultReadOptions).Handle, key, (UIntPtr)keyLength, out valLength)
                : Native.Instance.rocksdb_transaction_get_cf(Handle, (readOptions ?? RocksDb.DefaultReadOptions).Handle, cf.Handle, key, (UIntPtr)keyLength, out valLength);

            return ptr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IntPtr GetForUpdate(byte[] key, long keyLength, bool exclusive, out UIntPtr valLength, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            var ptr = cf is null
                ? Native.Instance.rocksdb_transaction_get_for_update(Handle, (readOptions ?? RocksDb.DefaultReadOptions).Handle, key, (UIntPtr)key.Length, out valLength, exclusive)
                : Native.Instance.rocksdb_transaction_get_for_update_cf(Handle, (readOptions ?? RocksDb.DefaultReadOptions).Handle, cf.Handle, key, (UIntPtr)key.Length, out valLength, exclusive);

            return ptr;
        }

        public void Put(string key, string value, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null, Encoding encoding = null)
        {
            var enc = (encoding ?? RocksDb.DefaultEncoding);
            var keyBytes = enc.GetBytes(key);
            var valueBytes = enc.GetBytes(value);

            Put(keyBytes, valueBytes, cf, writeOptions);
        }

        public void Put(byte[] key, byte[] value, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
            => Put(key, (nuint)key.Length, value, (nuint)value.Length, cf, writeOptions);

        public void Put(byte[] key, nuint keyLength, byte[] value, nuint valueLength, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            if(cf is null)
            {
                Native.Instance.rocksdb_transaction_put(Handle, key, keyLength, value, valueLength);
            }
            else
            {
                Native.Instance.rocksdb_transaction_put_cf(Handle, cf.Handle, key, keyLength, value, valueLength);
            }
        }


        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
#if !NODESTROY
                Native.Instance.rocksdb_transaction_destroy(Handle);
#endif
                Handle = IntPtr.Zero;
            }
        }
    }
}
