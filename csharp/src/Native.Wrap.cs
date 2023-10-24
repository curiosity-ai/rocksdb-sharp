using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using size_t = System.UIntPtr;

#pragma warning disable IDE1006 // Intentionally violating naming conventions because this is meant to match the C API
namespace RocksDbSharp
{
    /*
    These wrappers provide translation from the error output of the C API into exceptions
    */
    public abstract partial class Native
    {
        public void rocksdb_put(
            /*rocksdb_t**/ IntPtr db,
            /*const rocksdb_writeoptions_t**/ IntPtr writeOptions,
            string key,
            string val,
            ColumnFamilyHandle cf = null,
            System.Text.Encoding encoding = null)
        {
            rocksdb_put(db, writeOptions, key, val, out IntPtr errptr, cf, encoding);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        public void rocksdb_put(
            IntPtr db,
            IntPtr writeOptions,
            byte[] key,
            long keyLength,
            byte[] value,
            long valueLength,
            ColumnFamilyHandle cf)
        {
            IntPtr errptr;
            UIntPtr sklength = (UIntPtr)keyLength;
            UIntPtr svlength = (UIntPtr)valueLength;
            if (cf is null)
            {
                rocksdb_put(db, writeOptions, key, sklength, value, svlength, out errptr);
            }
            else
            {
                rocksdb_put_cf(db, writeOptions, cf.Handle, key, sklength, value, svlength, out errptr);
            }

            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        public void rocksdb_transaction_put(
            /*rocksdb_t**/ IntPtr db,
            string key,
            string val,
            ColumnFamilyHandle cf = null,
            System.Text.Encoding encoding = null)
        {
            rocksdb_transaction_put(db, key, val, out IntPtr errptr, cf, encoding);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        public void rocksdb_transaction_put(
            IntPtr db,
            byte[] key,
            long keyLength,
            byte[] value,
            long valueLength,
            ColumnFamilyHandle cf)
        {
            IntPtr errptr;
            UIntPtr sklength = (UIntPtr)keyLength;
            UIntPtr svlength = (UIntPtr)valueLength;
            if (cf is null)
            {
                rocksdb_transaction_put(db, key, sklength, value, svlength, out errptr);
            }
            else
            {
                rocksdb_transaction_put_cf(db, cf.Handle, key, sklength, value, svlength, out errptr);
            }

            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

#if !NETSTANDARD2_0
        public unsafe void rocksdb_put(
            IntPtr db,
            IntPtr writeOptions,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> value,
            ColumnFamilyHandle cf)
        {
            IntPtr errptr;
            UIntPtr sklength = (UIntPtr)key.Length;
            UIntPtr svlength = (UIntPtr)value.Length;

            fixed (byte* keyPtr = &MemoryMarshal.GetReference(key))
            fixed (byte* valuePtr = &MemoryMarshal.GetReference(value))
            {
                if (cf is null)
                {
                    rocksdb_put(db, writeOptions, keyPtr, sklength, valuePtr, svlength, out errptr);
                }
                else
                {
                    rocksdb_put_cf(db, writeOptions, cf.Handle, keyPtr, sklength, valuePtr, svlength, out errptr);
                }

                if (errptr != IntPtr.Zero)
                {
                    throw new RocksDbException(errptr);
                }
            }
        }

        public unsafe void rocksdb_transaction_put(
            IntPtr db,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> value,
            ColumnFamilyHandle cf)
        {
            IntPtr errptr;
            UIntPtr sklength = (UIntPtr)key.Length;
            UIntPtr svlength = (UIntPtr)value.Length;

            fixed (byte* keyPtr = &MemoryMarshal.GetReference(key))
            fixed (byte* valuePtr = &MemoryMarshal.GetReference(value))
            {
                if (cf is null)
                {
                    rocksdb_transaction_put(db, keyPtr, sklength, valuePtr, svlength, out errptr);
                }
                else
                {
                    rocksdb_transaction_put_cf(db, cf.Handle, keyPtr, sklength, valuePtr, svlength, out errptr);
                }

                if (errptr != IntPtr.Zero)
                {
                    throw new RocksDbException(errptr);
                }
            }
        }
#endif

        public void rocksdb_merge(
            IntPtr db,
            IntPtr writeOptions,
            byte[] key,
            long keyLength,
            byte[] value,
            long valueLength,
            ColumnFamilyHandle cf)
        {
            IntPtr errptr;
            UIntPtr sklength = (UIntPtr)keyLength;
            UIntPtr svlength = (UIntPtr)valueLength;
            if (cf is null)
            {
                rocksdb_merge(db, writeOptions, key, sklength, value, svlength, out errptr);
            }
            else
            {
                rocksdb_merge_cf(db, writeOptions, cf.Handle, key, sklength, value, svlength, out errptr);
            }

            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

#if !NETSTANDARD2_0
        public unsafe void rocksdb_merge(
            IntPtr db,
            IntPtr writeOptions,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> value,
            ColumnFamilyHandle cf)
        {
            IntPtr errptr;
            UIntPtr sklength = (UIntPtr)key.Length;
            UIntPtr svlength = (UIntPtr)value.Length;

            fixed (byte* keyPtr = &MemoryMarshal.GetReference(key))
            fixed (byte* valuePtr = &MemoryMarshal.GetReference(value))
            {
                if (cf is null)
                {
                    rocksdb_merge(db, writeOptions, keyPtr, sklength, valuePtr, svlength, out errptr);
                }
                else
                {
                    rocksdb_merge_cf(db, writeOptions, cf.Handle, keyPtr, sklength, valuePtr, svlength, out errptr);
                }

                if (errptr != IntPtr.Zero)
                {
                    throw new RocksDbException(errptr);
                }
            }
        }
#endif

        public string rocksdb_get(
            /*rocksdb_t**/ IntPtr db,
            /*const rocksdb_readoptions_t**/ IntPtr read_options,
            string key,
            ColumnFamilyHandle cf,
            System.Text.Encoding encoding = null)
        {
            var result = rocksdb_get(db, read_options, key, out IntPtr errptr, cf, encoding);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public IntPtr rocksdb_get(
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            long keyLength,
            out long vallen,
            ColumnFamilyHandle cf)
        {
            UIntPtr sklength = (UIntPtr)keyLength;
            var result = cf is null
                ? rocksdb_get(db, read_options, key, sklength, out UIntPtr valLength, out IntPtr errptr)
                : rocksdb_get_cf(db, read_options, cf.Handle, key, sklength, out valLength, out errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            vallen = (long)valLength;
            return result;
        }

        public byte[] rocksdb_get(
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            long keyLength = 0,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_get(db, read_options, key, keyLength == 0 ? key.Length : keyLength, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public string rocksdb_transaction_get(
            /*rocksdb_t**/ IntPtr db,
            /*const rocksdb_readoptions_t**/ IntPtr read_options,
            string key,
            ColumnFamilyHandle cf,
            System.Text.Encoding encoding = null)
        {
            var result = rocksdb_transaction_get(db, read_options, key, out IntPtr errptr, cf, encoding);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public IntPtr rocksdb_transaction_get(
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            long keyLength,
            out long vallen,
            ColumnFamilyHandle cf)
        {
            UIntPtr sklength = (UIntPtr)keyLength;
            var result = cf is null
                ? rocksdb_transaction_get(db, read_options, key, sklength, out UIntPtr valLength, out IntPtr errptr)
                : rocksdb_transaction_get_cf(db, read_options, cf.Handle, key, sklength, out valLength, out errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            vallen = (long)valLength;
            return result;
        }

        public byte[] rocksdb_transaction_get(
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            long keyLength = 0,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_transaction_get(db, read_options, key, keyLength == 0 ? key.Length : keyLength, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }


        public bool rocksdb_has_key(
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            long keyLength,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_has_key(db, read_options, key, keyLength == 0 ? key.Length : keyLength, out IntPtr errptr, cf);
            
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        internal bool rocksdb_has_key(IntPtr db, IntPtr read_options, string key, ColumnFamilyHandle cf, Encoding encoding)
        {
            if (encoding is null)
            {
                encoding = Encoding.UTF8;
            }
            
            IntPtr errptr;

            unsafe
            {
                fixed (char* k = key)
                {
                    int klength = key.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    var buffer = Marshal.AllocHGlobal(bklength);
                    byte* bk = (byte*)buffer.ToPointer();
                    encoding.GetBytes(k, klength, bk, bklength);
                    UIntPtr sklength = (UIntPtr)bklength;

                    var resultPtr = cf is null
                        ? rocksdb_get(db, read_options, bk, sklength, out UIntPtr bvlength, out errptr)
                        : rocksdb_get_cf(db, read_options, cf.Handle, bk, sklength, out bvlength, out errptr);
      
                    Marshal.FreeHGlobal(buffer);

                    if (errptr != IntPtr.Zero)
                    {
                        throw new RocksDbException(errptr);
                    }

                    if (resultPtr == IntPtr.Zero)
                    {
                        return false;
                    }

                    rocksdb_free(resultPtr);

                    return true;
                }
            }
        }

        public bool rocksdb_transaction_has_key(
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            long keyLength,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_transaction_has_key(db, read_options, key, keyLength == 0 ? key.Length : keyLength, out IntPtr errptr, cf);

            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        internal bool rocksdb_transaction_has_key(IntPtr db, IntPtr read_options, string key, ColumnFamilyHandle cf, Encoding encoding)
        {
            if (encoding is null)
            {
                encoding = Encoding.UTF8;
            }

            IntPtr errptr;

            unsafe
            {
                fixed (char* k = key)
                {
                    int klength = key.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    var buffer = Marshal.AllocHGlobal(bklength);
                    byte* bk = (byte*)buffer.ToPointer();
                    encoding.GetBytes(k, klength, bk, bklength);
                    UIntPtr sklength = (UIntPtr)bklength;

                    var resultPtr = cf is null
                        ? rocksdb_transaction_get(db, read_options, bk, sklength, out UIntPtr bvlength, out errptr)
                        : rocksdb_transaction_get_cf(db, read_options, cf.Handle, bk, sklength, out bvlength, out errptr);

                    Marshal.FreeHGlobal(buffer);

                    if (errptr != IntPtr.Zero)
                    {
                        throw new RocksDbException(errptr);
                    }

                    if (resultPtr == IntPtr.Zero)
                    {
                        return false;
                    }

                    rocksdb_free(resultPtr);

                    return true;
                }
            }
        }

#if !NETSTANDARD2_0
        public byte[] rocksdb_get(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_get(db, read_options, key, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public bool rocksdb_has_key(
                        IntPtr db,
                        IntPtr read_options,
                        ReadOnlySpan<byte> key,
                        ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_has_key(db, read_options, key, out IntPtr errptr, cf);

            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }
        public T rocksdb_get<T>(
                    IntPtr db,
                    IntPtr read_options,
                    ReadOnlySpan<byte> key,
                    ISpanDeserializer<T> deserializer,
                    ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_get(db, read_options, key, deserializer, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public T rocksdb_get<T>(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            Func<Stream, T> deserializer,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_get(db, read_options, key, deserializer, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public byte[] rocksdb_transaction_get(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_transaction_get(db, read_options, key, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public bool rocksdb_transaction_has_key(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_transaction_has_key(db, read_options, key, out IntPtr errptr, cf);

            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }
        public T rocksdb_transaction_get<T>(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            ISpanDeserializer<T> deserializer,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_transaction_get(db, read_options, key, deserializer, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public T rocksdb_transaction_get<T>(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            Func<Stream, T> deserializer,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_transaction_get(db, read_options, key, deserializer, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }
#endif

        public System.Collections.Generic.KeyValuePair<string, string>[] rocksdb_multi_get(
            IntPtr db,
            IntPtr read_options,
            string[] keys,
            ColumnFamilyHandle[] cf = null,
            System.Text.Encoding encoding = null)
        {
            if (encoding == null)
            {
                encoding = System.Text.Encoding.UTF8;
            }

            IntPtr[] errptrs = new IntPtr[keys.Length];
            var result = rocksdb_multi_get(db, read_options, keys, cf: cf, errptrs: errptrs, encoding: encoding);
            foreach (var errptr in errptrs)
            {
                if (errptr != IntPtr.Zero)
                {
                    throw new RocksDbException(errptr);
                }
            }

            return result;
        }


        public System.Collections.Generic.KeyValuePair<byte[], byte[]>[] rocksdb_multi_get(
            IntPtr db,
            IntPtr read_options,
            byte[][] keys,
            ulong[] keyLengths = null,
            ColumnFamilyHandle[] cf = null)
        {
            IntPtr[] errptrs = new IntPtr[keys.Length];
            var result = rocksdb_multi_get(db, read_options, keys, keyLengths: keyLengths, cf: cf, errptrs: errptrs);
            foreach (var errptr in errptrs)
            {
                if (errptr != IntPtr.Zero)
                {
                    throw new RocksDbException(errptr);
                }
            }

            return result;
        }

        public System.Collections.Generic.KeyValuePair<string, string>[] rocksdb_transaction_multi_get(
            IntPtr db,
            IntPtr read_options,
            string[] keys,
            ColumnFamilyHandle[] cf = null,
            System.Text.Encoding encoding = null)
        {
            if (encoding == null)
            {
                encoding = System.Text.Encoding.UTF8;
            }

            IntPtr[] errptrs = new IntPtr[keys.Length];
            var result = rocksdb_transaction_multi_get(db, read_options, keys, cf: cf, errptrs: errptrs, encoding: encoding);
            foreach (var errptr in errptrs)
            {
                if (errptr != IntPtr.Zero)
                {
                    throw new RocksDbException(errptr);
                }
            }

            return result;
        }

        public System.Collections.Generic.KeyValuePair<byte[], byte[]>[] rocksdb_transaction_multi_get(
            IntPtr db,
            IntPtr read_options,
            byte[][] keys,
            ulong[] keyLengths = null,
            ColumnFamilyHandle[] cf = null)
        {
            IntPtr[] errptrs = new IntPtr[keys.Length];
            var result = rocksdb_transaction_multi_get(db, read_options, keys, keyLengths: keyLengths, cf: cf, errptrs: errptrs);
            foreach (var errptr in errptrs)
            {
                if (errptr != IntPtr.Zero)
                {
                    throw new RocksDbException(errptr);
                }
            }

            return result;
        }

        public void rocksdb_delete(
            /*rocksdb_t**/ IntPtr db,
            /*const rocksdb_writeoptions_t**/ IntPtr writeOptions,
            /*const*/ string key,
            ColumnFamilyHandle cf)
        {
            rocksdb_delete(db, writeOptions, key, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        public void rocksdb_transaction_delete(
            /*rocksdb_t**/ IntPtr db,
            /*const*/ string key,
            ColumnFamilyHandle cf)
        {
            rocksdb_transaction_delete(db, key, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        [Obsolete("Use UIntPtr version instead")]
        public void rocksdb_delete(
            /*rocksdb_t**/ IntPtr db,
            /*const rocksdb_writeoptions_t**/ IntPtr writeOptions,
            /*const*/ byte[] key,
            long keylen)
        {
            UIntPtr sklength = (UIntPtr)keylen;
            rocksdb_delete(db, writeOptions, key, sklength, out IntPtr errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        [Obsolete("Use UIntPtr version instead")]
        public void rocksdb_delete_cf(
            /*rocksdb_t**/ IntPtr db,
            /*const rocksdb_writeoptions_t**/ IntPtr writeOptions,
            /*const*/ byte[] key,
            long keylen,
            ColumnFamilyHandle cf)
        {
            rocksdb_delete_cf(db, writeOptions, cf.Handle, key, (UIntPtr)keylen, out IntPtr errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        [Obsolete("Use UIntPtr version instead")]
        public void rocksdb_ingest_external_file(IntPtr db, string[] file_list, ulong list_len, IntPtr opt)
        {
            UIntPtr llen = (UIntPtr)list_len;
            rocksdb_ingest_external_file(db, file_list, llen, opt, out IntPtr errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        [Obsolete("Use UIntPtr version instead")]
        public void rocksdb_ingest_external_file_cf(IntPtr db, IntPtr handle, string[] file_list, ulong list_len, IntPtr opt)
        {
            UIntPtr llen = (UIntPtr)list_len;
            rocksdb_ingest_external_file_cf(db, handle, file_list, llen, opt, out IntPtr errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        [Obsolete("Use UIntPtr version instead")]
        public void rocksdb_sstfilewriter_add(
            IntPtr writer,
            byte[] key,
            ulong keylen,
            byte[] val,
            ulong vallen)
        {
            UIntPtr sklength = (UIntPtr)keylen;
            UIntPtr svlength = (UIntPtr)vallen;
            rocksdb_sstfilewriter_add(writer, key, sklength, val, svlength, out IntPtr errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        public unsafe void rocksdb_sstfilewriter_add(
            IntPtr writer,
            string key,
            string val)
        {
            rocksdb_sstfilewriter_add(writer, key, val, out IntPtr errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }
        }

        public string rocksdb_writebatch_wi_get_from_batch(
            IntPtr wb,
            IntPtr options,
            string key,
            ColumnFamilyHandle cf,
            System.Text.Encoding encoding = null)
        {
            var result = rocksdb_writebatch_wi_get_from_batch(wb, options, key, out IntPtr errptr, cf, encoding);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public IntPtr rocksdb_writebatch_wi_get_from_batch(
            IntPtr wb,
            IntPtr options,
            byte[] key,
            ulong keyLength,
            out ulong vallen,
            ColumnFamilyHandle cf)
        {
            UIntPtr sklength = (UIntPtr)keyLength;
            var result = cf is null
                ? rocksdb_writebatch_wi_get_from_batch(wb, options, key, sklength, out UIntPtr valLength, out IntPtr errptr)
                : rocksdb_writebatch_wi_get_from_batch_cf(wb, options, cf.Handle, key, sklength, out valLength, out errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            vallen = (ulong)valLength;
            return result;
        }

        public byte[] rocksdb_writebatch_wi_get_from_batch(
            IntPtr wb,
            IntPtr options,
            byte[] key,
            ulong keyLength = 0,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_writebatch_wi_get_from_batch(wb, options, key, keyLength == 0 ? (ulong)key.Length : keyLength, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public string rocksdb_writebatch_wi_get_from_batch_and_db(
            IntPtr wb,
            IntPtr db,
            IntPtr read_options,
            string key,
            ColumnFamilyHandle cf,
            System.Text.Encoding encoding = null)
        {
            var result = rocksdb_writebatch_wi_get_from_batch_and_db(wb, db, read_options, key, out IntPtr errptr, cf, encoding);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

        public IntPtr rocksdb_writebatch_wi_get_from_batch_and_db(
            IntPtr wb,
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            ulong keyLength,
            out ulong vallen,
            ColumnFamilyHandle cf)
        {
            UIntPtr sklength = (UIntPtr)keyLength;
            var result = cf is null
                ? rocksdb_writebatch_wi_get_from_batch_and_db(wb, db, read_options, key, sklength, out UIntPtr valLength, out IntPtr errptr)
                : rocksdb_writebatch_wi_get_from_batch_and_db_cf(wb, db, read_options, cf.Handle, key, sklength, out valLength, out errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            vallen = (ulong)valLength;
            return result;
        }

        public byte[] rocksdb_writebatch_wi_get_from_batch_and_db(
            IntPtr wb,
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            ulong keyLength = 0,
            ColumnFamilyHandle cf = null)
        {
            var result = rocksdb_writebatch_wi_get_from_batch_and_db(wb, db, read_options, key, keyLength == 0 ? (ulong)key.Length : keyLength, out IntPtr errptr, cf);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            return result;
        }

    }
}
