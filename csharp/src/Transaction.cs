using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RocksDbSharp
{
    public class Transaction : IDisposable
    {
        // Managed references to unmanaged resources that need to live at least as long as the db
        private dynamic References { get; } = new ExpandoObject();

        private bool _disposed;

        public IntPtr Handle { get; private set; }

        public Snapshot Snapshot { get; private set; } = null;

        internal Transaction(TransactionDb parent, WriteOptions writeOptions, TransactionOptions transactionOptions)
        {
            References.Parent = parent;
            References.WriteOptions = writeOptions;
            References.TransactionOptions = transactionOptions;

            Handle = Native.Instance.rocksdb_transaction_begin(
                parent.Handle,
                writeOptions.Handle,
                transactionOptions.Handle, IntPtr.Zero);
        }

        ~Transaction()
        {
            ReleaseUnmanagedResources();
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                ReleaseUnmanagedResources();
                GC.SuppressFinalize(this);
            }
            finally
            {
                _disposed = true;
            }
        }

        private void ReleaseUnmanagedResources()
        {
            if (Handle == IntPtr.Zero)
                return;

#if !NODESTROY
            Native.Instance.rocksdb_transaction_destroy(Handle);
#endif
            Handle = IntPtr.Zero;
        }

        /// <summary>
        /// Prepare the current transaction for 2PC.
        /// </summary>
        /// <exception cref="RocksDbException">if an error occurs when preparing the transaction</exception>
        public void Prepare()
        {
            Native.Instance.rocksdb_transaction_prepare(Handle);
        }

        /// <summary>
        /// Write all batched keys to the db atomically.
        /// </summary>
        /// <exception cref="RocksDbException">if an error occurs when committing the transaction</exception>
        public void Commit()
        {
            Native.Instance.rocksdb_transaction_commit(Handle);
        }

        /// <summary>
        /// Discard all batched writes in this transaction.
        /// </summary>
        /// <exception cref="RocksDbException">if an error occurs when rolling back the transaction</exception>
        public void Rollback()
        {
            Native.Instance.rocksdb_transaction_rollback(Handle);
        }

        /// <summary>
        /// Records the state of the transaction for future calls to <see cref="RollbackToSavePoint"/>.
        /// May be called multiple times to set multiple save points.
        /// </summary>
        public void SetSavePoint()
        {
            Native.Instance.rocksdb_transaction_set_savepoint(Handle);
        }

        /// <summary>
        /// Undo all operations in this transaction (put, merge, delete, putLogData) since the most recent call to <see cref="SetSavePoint"/> and removes the most recent <see cref="SetSavePoint"/>.
        /// </summary>
        public void RollbackToSavePoint()
        {
            Native.Instance.rocksdb_transaction_rollback_to_savepoint(Handle);
        }

        public string Get(string key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null, Encoding encoding = null)
        {
            return Native.Instance.rocksdb_transaction_get(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, key, cf, encoding ?? RocksDbBase.DefaultEncoding);
        }

        public byte[] Get(byte[] key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Get(key, key.GetLongLength(0), cf, readOptions);
        }

#if !NETSTANDARD2_0
        public byte[] Get(ReadOnlySpan<byte> key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_transaction_get(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, key, cf);
        }

        public bool HasKey(ReadOnlySpan<byte> key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_transaction_has_key(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, key, cf);
        }

        public T Get<T>(ReadOnlySpan<byte> key, ISpanDeserializer<T> deserializer, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_transaction_get(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, key, deserializer, cf);
        }

        public T Get<T>(ReadOnlySpan<byte> key, Func<Stream, T> deserializer, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_transaction_get(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, key, deserializer, cf);
        }
#endif

        public byte[] Get(byte[] key, long keyLength, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_transaction_get(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, key, keyLength, cf);
        }

        public bool HasKey(byte[] key, long keyLength, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_transaction_has_key(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, key, keyLength, cf);
        }

        public bool HasKey(string key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null, Encoding encoding = null)
        {
            return Native.Instance.rocksdb_transaction_has_key(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, key, cf, encoding ?? RocksDbBase.DefaultEncoding);
        }

        /// <summary>
        /// Reads the contents of the database value associated with <paramref name="key"/>, if present, into the supplied
        /// <paramref name="buffer"/> at <paramref name="offset"/> up to <paramref name="length"/> bytes, returning the
        /// length of the value in the database, or -1 if the key is not present.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        /// <param name="cf"></param>
        /// <param name="readOptions"></param>
        /// <returns>The actual length of the database field if it exists, otherwise -1</returns>
        public long Get(byte[] key, byte[] buffer, long offset, long length, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Get(key, key.GetLongLength(0), buffer, offset, length, cf, readOptions);
        }

        /// <summary>
        /// Reads the contents of the database value associated with <paramref name="key"/>, if present, into the supplied
        /// <paramref name="buffer"/> at <paramref name="offset"/> up to <paramref name="length"/> bytes, returning the
        /// length of the value in the database, or -1 if the key is not present.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="keyLength"></param>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        /// <param name="cf"></param>
        /// <param name="readOptions"></param>
        /// <returns>The actual length of the database field if it exists, otherwise -1</returns>
        public long Get(byte[] key, long keyLength, byte[] buffer, long offset, long length, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            unsafe
            {
                var ptr = Native.Instance.rocksdb_transaction_get(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, key, keyLength, out long valLength, cf);
                if (ptr == IntPtr.Zero)
                {
                    return -1;
                }

                var copyLength = Math.Min(length, valLength);
                Marshal.Copy(ptr, buffer, (int)offset, (int)copyLength);
                Native.Instance.rocksdb_free(ptr);
                return valLength;
            }
        }

        public KeyValuePair<byte[],byte[]>[] MultiGet(byte[][] keys, ColumnFamilyHandle[] cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_transaction_multi_get(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, keys, null, cf);
        }

        public KeyValuePair<string, string>[] MultiGet(string[] keys, ColumnFamilyHandle[] cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_transaction_multi_get(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, keys, cf);
        }

        public void Remove(string key, ColumnFamilyHandle cf = null)
        {
            Native.Instance.rocksdb_transaction_delete(Handle, key, cf);
        }

        public void Remove(byte[] key, ColumnFamilyHandle cf = null)
        {
            Remove(key, key.Length, cf);
        }

#if !NETSTANDARD2_0
        public unsafe void Remove(ReadOnlySpan<byte> key, ColumnFamilyHandle cf = null)
        {
            fixed (byte* keyPtr = &MemoryMarshal.GetReference(key))
            {
                if (cf is null)
                {
                    Native.Instance.rocksdb_transaction_delete(Handle, keyPtr, (UIntPtr)key.Length);
                }
                else
                {
                    Native.Instance.rocksdb_transaction_delete_cf(Handle, cf.Handle, keyPtr, (UIntPtr)key.Length);
                }
            }
        }
#endif

        public void Remove(byte[] key, long keyLength, ColumnFamilyHandle cf = null)
        {
            if (cf is null)
            {
                Native.Instance.rocksdb_transaction_delete(Handle, key, (UIntPtr)keyLength);
            }
            else
            {
                Native.Instance.rocksdb_transaction_delete_cf(Handle, cf.Handle, key, (UIntPtr)keyLength);
            }
        }

        public void Put(string key, string value, ColumnFamilyHandle cf = null, Encoding encoding = null)
        {
            Native.Instance.rocksdb_transaction_put(Handle, key, value, cf, encoding ?? RocksDbBase.DefaultEncoding);
        }

        public void Put(byte[] key, byte[] value, ColumnFamilyHandle cf = null)
        {
            Put(key, key.GetLongLength(0), value, value.GetLongLength(0), cf);
        }

#if !NETSTANDARD2_0
        public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ColumnFamilyHandle cf = null)
        {
            Native.Instance.rocksdb_transaction_put(Handle, key, value, cf);
        }
#endif

        public void Put(byte[] key, long keyLength, byte[] value, long valueLength, ColumnFamilyHandle cf = null)
        {
            Native.Instance.rocksdb_transaction_put(Handle, key, keyLength, value, valueLength, cf);
        }

        public Iterator NewIterator(ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            IntPtr iteratorHandle = cf is null
                ? Native.Instance.rocksdb_transaction_create_iterator(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle)
                : Native.Instance.rocksdb_transaction_create_iterator_cf(Handle, (readOptions ?? RocksDbBase.DefaultReadOptions).Handle, cf.Handle);
            // Note: passing in read options here only to ensure that it is not collected before the iterator
            return new Iterator(iteratorHandle, readOptions);
        }
    }
}