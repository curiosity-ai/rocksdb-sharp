﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Transitional;

namespace RocksDbSharp
{
    public class Iterator : IDisposable
    {
        private IntPtr handle;
        #pragma warning disable CS0414
        private ReadOptions readOptions;
        #pragma warning restore CS0414

        internal Iterator(IntPtr handle)
        {
            this.handle = handle;
        }

        internal Iterator(IntPtr handle, ReadOptions readOptions) : this(handle)
        {
            // Note: passing readOptions in here has no actual effect except to keep readOptions
            // from being garbage collected whilst the Iterator is still alive because the
            // the iterator on the native side will actually read things from some of the readOptions
            // directly
            this.readOptions = readOptions;
        }

        public IntPtr Handle { get { return handle; } }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
#if !NODESTROY
                Native.Instance.rocksdb_iter_destroy(handle);
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

        public bool Valid()
        {
            return Native.Instance.rocksdb_iter_valid(handle);
        }

        public Iterator SeekToFirst()
        {
            Native.Instance.rocksdb_iter_seek_to_first(handle);
            return this;
        }

        public Iterator SeekToLast()
        {
            Native.Instance.rocksdb_iter_seek_to_last(handle);
            return this;
        }

        public unsafe Iterator Seek(byte *key, ulong klen)
        {
            Native.Instance.rocksdb_iter_seek(handle, key, (UIntPtr)klen);
            return this;
        }

        public Iterator Seek(byte[] key)
        {
            return Seek(key, (ulong)key.GetLongLength(0));
        }

        public Iterator Seek(byte[] key, ulong klen)
        {
            Native.Instance.rocksdb_iter_seek(handle, key, (UIntPtr)klen);
            return this;
        }

        public Iterator Seek(string key)
        {
            Native.Instance.rocksdb_iter_seek(handle, key);
            return this;
        }

#if !NETSTANDARD2_0
        public unsafe Iterator Seek(ReadOnlySpan<byte> key)
        {
            fixed (byte* keyPtr = key)
            {
                Native.Instance.rocksdb_iter_seek(handle, keyPtr, (UIntPtr)key.Length);
                return this;
            }
        }
#endif 

        public unsafe Iterator SeekForPrev(byte* key, ulong klen)
        {
            Native.Instance.rocksdb_iter_seek_for_prev(handle, key, (UIntPtr)klen);
            return this;
        }

        public Iterator SeekForPrev(byte[] key)
        {
            SeekForPrev(key, (ulong)key.Length);
            return this;
        }

        public Iterator SeekForPrev(byte[] key, ulong klen)
        {
            Native.Instance.rocksdb_iter_seek_for_prev(handle, key, (UIntPtr)klen);
            return this;
        }

        public Iterator SeekForPrev(string key)
        {
            Native.Instance.rocksdb_iter_seek_for_prev(handle, key);
            return this;
        }

#if !NETSTANDARD2_0
        public unsafe Iterator SeekForPrev(ReadOnlySpan<byte> key)
        {
            fixed (byte* keyPtr = key)
            {
                Native.Instance.rocksdb_iter_seek_for_prev(handle, keyPtr, (UIntPtr)key.Length);
                return this;
            }
        }
#endif

        public Iterator Next()
        {
            Native.Instance.rocksdb_iter_next(handle);
            return this;
        }

        public Iterator Prev()
        {
            Native.Instance.rocksdb_iter_prev(handle);
            return this;
        }

        public byte[] Key()
        {
            return Native.Instance.rocksdb_iter_key(handle);
        }

        public byte[] Value()
        {
            return Native.Instance.rocksdb_iter_value(handle);
        }

#if !NETSTANDARD2_0
        public T Key<T>(ISpanDeserializer<T> deserializer)
        {
            return Native.Instance.rocksdb_iter_key(handle, deserializer);
        }

        public T Value<T>(ISpanDeserializer<T> deserializer)
        {
            return Native.Instance.rocksdb_iter_value(handle, deserializer);
        }

        public unsafe ReadOnlySpan<byte> GetKeySpan()
        {
            IntPtr keyPtr = Native.Instance.rocksdb_iter_key(handle, out UIntPtr keyLength);
            return new ReadOnlySpan<byte>((byte*)keyPtr, (int)keyLength);
        }

        public unsafe ReadOnlySpan<byte> GetValueSpan()
        {
            IntPtr valuePtr = Native.Instance.rocksdb_iter_value(handle, out UIntPtr valueLength);
            return new ReadOnlySpan<byte>((byte*)valuePtr, (int)valueLength);
        }
#endif

        public T Key<T>(Func<Stream,T> deserializer)
        {
            return Native.Instance.rocksdb_iter_key(handle, deserializer);
        }

        public T Value<T>(Func<Stream, T> deserializer)
        {
            return Native.Instance.rocksdb_iter_value(handle, deserializer);
        }

        public string StringKey()
        {
            return Native.Instance.rocksdb_iter_key_string(handle);
        }

        public string StringValue()
        {
            return Native.Instance.rocksdb_iter_value_string(handle);
        }

        // TODO: figure out how to best implement rocksdb_iter_get_error
    }
}
