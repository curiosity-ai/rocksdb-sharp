using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Transitional;

namespace RocksDbSharp
{

    public abstract class RocksDbBase : IDisposable
    {
        private bool _disposed;
        internal static ReadOptions DefaultReadOptions { get; } = new ReadOptions();
        internal static OptionsHandle DefaultOptions { get; } = new DbOptions();
        internal static WriteOptions DefaultWriteOptions { get; } = new WriteOptions();
        internal static Encoding DefaultEncoding => Encoding.UTF8;
        private Dictionary<string, ColumnFamilyHandleInternal> columnFamilies;

        // Managed references to unmanaged resources that need to live at least as long as the db
        internal dynamic References { get; } = new ExpandoObject();

        public IntPtr Handle { get; internal set; }

        internal RocksDbBase(IntPtr handle, dynamic optionsReferences, dynamic cfOptionsRefs, Dictionary<string, ColumnFamilyHandleInternal> columnFamilies = null)
        {
            this.Handle = handle;
            References.Options = optionsReferences;
            References.CfOptions = cfOptionsRefs;
            this.columnFamilies = columnFamilies;
        }

        ~RocksDbBase()
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

        protected virtual void ReleaseUnmanagedResources()
        {
            if (columnFamilies is object)
            {
                foreach (var cfh in columnFamilies.Values)
                {
                    cfh.Dispose();
                }
                columnFamilies = null;
            }
        }


        /// <summary>
        /// Usage:
        /// <code><![CDATA[
        /// using (var cp = db.Checkpoint())
        /// {
        ///     cp.Save("path/to/checkpoint");
        /// }
        /// ]]></code>
        /// </summary>
        /// <returns></returns>
        public Checkpoint Checkpoint()
        {
            var checkpoint = Native.Instance.rocksdb_checkpoint_object_create(Handle);
            return new Checkpoint(checkpoint);
        }

        public void SetOptions(IEnumerable<KeyValuePair<string, string>> options)
        {
            var keys = options.Select(e => e.Key).ToArray();
            var values = options.Select(e => e.Value).ToArray();
            Native.Instance.rocksdb_set_options(Handle, keys.Length, keys, values);
        }

        public string Get(string key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null, Encoding encoding = null)
        {
            return Native.Instance.rocksdb_get(Handle, (readOptions ?? DefaultReadOptions).Handle, key, cf, encoding ?? DefaultEncoding);
        }

        public byte[] Get(byte[] key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Get(key, key.GetLongLength(0), cf, readOptions);
        }

#if !NETSTANDARD2_0
        public byte[] Get(ReadOnlySpan<byte> key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_get(Handle, (readOptions ?? DefaultReadOptions).Handle, key, cf);
        }

        public bool HasKey(ReadOnlySpan<byte> key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_has_key(Handle, (readOptions ?? DefaultReadOptions).Handle, key, cf);
        }

        public T Get<T>(ReadOnlySpan<byte> key, ISpanDeserializer<T> deserializer, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_get(Handle, (readOptions ?? DefaultReadOptions).Handle, key, deserializer, cf);
        }

        public T Get<T>(ReadOnlySpan<byte> key, Func<Stream, T> deserializer, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_get(Handle, (readOptions ?? DefaultReadOptions).Handle, key, deserializer, cf);
        }
#endif

        public byte[] Get(byte[] key, long keyLength, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_get(Handle, (readOptions ?? DefaultReadOptions).Handle, key, keyLength, cf);
        }

        public bool HasKey(byte[] key, long keyLength, ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_has_key(Handle, (readOptions ?? DefaultReadOptions).Handle, key, keyLength, cf);
        }

        public bool HasKey(string key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null, Encoding encoding = null)
        {
            return Native.Instance.rocksdb_has_key(Handle, (readOptions ?? DefaultReadOptions).Handle, key, cf, encoding ?? DefaultEncoding);
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
                var ptr = Native.Instance.rocksdb_get(Handle, (readOptions ?? DefaultReadOptions).Handle, key, keyLength, out long valLength, cf);
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
            return Native.Instance.rocksdb_multi_get(Handle, (readOptions ?? DefaultReadOptions).Handle, keys, null, cf);
        }

        public KeyValuePair<string, string>[] MultiGet(string[] keys, ColumnFamilyHandle[] cf = null, ReadOptions readOptions = null)
        {
            return Native.Instance.rocksdb_multi_get(Handle, (readOptions ?? DefaultReadOptions).Handle, keys, cf);
        }

        public void Write(WriteBatch writeBatch, WriteOptions writeOptions = null)
        {
            Native.Instance.rocksdb_write(Handle, (writeOptions ?? DefaultWriteOptions).Handle, writeBatch.Handle);
        }

        public void Write(WriteBatchWithIndex writeBatch, WriteOptions writeOptions = null)
        {
            Native.Instance.rocksdb_write_writebatch_wi(Handle, (writeOptions ?? DefaultWriteOptions).Handle, writeBatch.Handle);
        }

        public void Remove(string key, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            Native.Instance.rocksdb_delete(Handle, (writeOptions ?? DefaultWriteOptions).Handle, key, cf);
        }

        public void Remove(byte[] key, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            Remove(key, key.Length, cf, writeOptions);
        }

#if !NETSTANDARD2_0
        public unsafe void Remove(ReadOnlySpan<byte> key, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            fixed (byte* keyPtr = &MemoryMarshal.GetReference(key))
            {
                if (cf is null)
                {
                    Native.Instance.rocksdb_delete(Handle, (writeOptions ?? DefaultWriteOptions).Handle, keyPtr, (UIntPtr)key.Length);
                }
                else
                {
                    Native.Instance.rocksdb_delete_cf(Handle, (writeOptions ?? DefaultWriteOptions).Handle, cf.Handle, keyPtr, (UIntPtr)key.Length);
                }
            }
        }
#endif

        public void Remove(byte[] key, long keyLength, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            if (cf is null)
            {
                Native.Instance.rocksdb_delete(Handle, (writeOptions ?? DefaultWriteOptions).Handle, key, (UIntPtr)keyLength);
            }
            else
            {
                Native.Instance.rocksdb_delete_cf(Handle, (writeOptions ?? DefaultWriteOptions).Handle, cf.Handle, key, (UIntPtr)keyLength);
            }
        }

        public void Put(string key, string value, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null, Encoding encoding = null)
        {
            Native.Instance.rocksdb_put(Handle, (writeOptions ?? DefaultWriteOptions).Handle, key, value, cf, encoding ?? DefaultEncoding);
        }

        public void Put(byte[] key, byte[] value, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            Put(key, key.GetLongLength(0), value, value.GetLongLength(0), cf, writeOptions);
        }

#if !NETSTANDARD2_0
        public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            Native.Instance.rocksdb_put(Handle, (writeOptions ?? DefaultWriteOptions).Handle, key, value, cf);
        }
#endif

        public void Put(byte[] key, long keyLength, byte[] value, long valueLength, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            Native.Instance.rocksdb_put(Handle, (writeOptions ?? DefaultWriteOptions).Handle, key, keyLength, value, valueLength, cf);
        }

        public void Merge(string key, string value, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null, Encoding encoding = null)
        {
            Native.Instance.rocksdb_put(Handle, (writeOptions ?? DefaultWriteOptions).Handle, key, value, cf, encoding ?? DefaultEncoding);
        }

        public void Merge(byte[] key, byte[] value, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            Merge(key, key.GetLongLength(0), value, value.GetLongLength(0), cf, writeOptions);
        }

#if !NETSTANDARD2_0
        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            Native.Instance.rocksdb_merge(Handle, (writeOptions ?? DefaultWriteOptions).Handle, key, value, cf);
        }
#endif

        public void Merge(byte[] key, long keyLength, byte[] value, long valueLength, ColumnFamilyHandle cf = null, WriteOptions writeOptions = null)
        {
            Native.Instance.rocksdb_merge(Handle, (writeOptions ?? DefaultWriteOptions).Handle, key, keyLength, value, valueLength, cf);
        }

        public Iterator NewIterator(ColumnFamilyHandle cf = null, ReadOptions readOptions = null)
        {
            IntPtr iteratorHandle = cf is null
                ? Native.Instance.rocksdb_create_iterator(Handle, (readOptions ?? DefaultReadOptions).Handle)
                : Native.Instance.rocksdb_create_iterator_cf(Handle, (readOptions ?? DefaultReadOptions).Handle, cf.Handle);
            // Note: passing in read options here only to ensure that it is not collected before the iterator
            return new Iterator(iteratorHandle, readOptions);
        }

        public Iterator[] NewIterators(ColumnFamilyHandle[] cfs, ReadOptions[] readOptions)
        {
            throw new NotImplementedException("TODO: Implement NewIterators()");
            // See rocksdb_create_iterators
        }

        public Snapshot CreateSnapshot()
        {
            IntPtr snapshotHandle = Native.Instance.rocksdb_create_snapshot(Handle);
            return new Snapshot(Handle, snapshotHandle);
        }

        public static IEnumerable<string> ListColumnFamilies(DbOptions options, string name)
        {
            return Native.Instance.rocksdb_list_column_families(options.Handle, name);
        }

        public static bool TryListColumnFamilies(DbOptions options, string name, out string[] columnFamilies)
        {
            var result = Native.Instance.rocksdb_list_column_families(options.Handle, name, out UIntPtr lencf, out IntPtr errptr);
            if (errptr != IntPtr.Zero)
            {
                columnFamilies = Array.Empty<string>();
                return false;
            }

            IntPtr[] ptrs = new IntPtr[(ulong)lencf];
            Marshal.Copy(result, ptrs, 0, (int)lencf);
            columnFamilies = new string[(ulong)lencf];
            for (ulong i = 0; i < (ulong)lencf; i++)
            {
                columnFamilies[i] = Marshal.PtrToStringAnsi(ptrs[i]);
            }

            Native.Instance.rocksdb_list_column_families_destroy(result, lencf);
            return true;
        }

        public ColumnFamilyHandle CreateColumnFamily(ColumnFamilyOptions cfOptions, string name)
        {
            var cfh = Native.Instance.rocksdb_create_column_family(Handle, cfOptions.Handle, name);
            var cfhw = new ColumnFamilyHandleInternal(cfh);
            columnFamilies.Add(name, cfhw);
            return cfhw;
        }

        public void DropColumnFamily(string name)
        {
            var cf = GetColumnFamily(name);
            Native.Instance.rocksdb_drop_column_family(Handle, cf.Handle);
            columnFamilies.Remove(name);
        }
        
        public ColumnFamilyHandle GetDefaultColumnFamily()
        {
            return GetColumnFamily(ColumnFamilies.DefaultName);
        }

        public ColumnFamilyHandle GetColumnFamily(string name)
        {
            if (columnFamilies is null)
            {
                throw new RocksDbSharpException("Database not opened for column families");
            }

            return columnFamilies[name];
        }

        public bool TryGetColumnFamily(string name, out ColumnFamilyHandle handle)
        {
            if (columnFamilies is null)
            {
                throw new RocksDbSharpException("Database not opened for column families");
            }

            if (columnFamilies.TryGetValue(name, out var internalHandle))
            {
                handle = internalHandle;
                return true;
            }

            handle = null;
            return false;
        }

        public string GetProperty(string propertyName)
        {
            return Native.Instance.rocksdb_property_value_string(Handle, propertyName);
        }

        public string GetProperty(string propertyName, ColumnFamilyHandle cf)
        {
            return Native.Instance.rocksdb_property_value_cf_string(Handle, cf.Handle, propertyName);
        }

        public void IngestExternalFiles(string[] files, IngestExternalFileOptions ingestOptions, ColumnFamilyHandle cf = null)
        {
            if (cf is null)
            {
                Native.Instance.rocksdb_ingest_external_file(Handle, files, (UIntPtr)files.GetLongLength(0), ingestOptions.Handle);
            }
            else
            {
                Native.Instance.rocksdb_ingest_external_file_cf(Handle, cf.Handle, files, (UIntPtr)files.GetLongLength(0), ingestOptions.Handle);
            }
        }

        public void CompactRange(byte[] start, byte[] limit, ColumnFamilyHandle cf = null)
        {
            if (cf is null)
            {
                Native.Instance.rocksdb_compact_range(Handle, start, (UIntPtr)(start?.GetLongLength(0) ?? 0L), limit, (UIntPtr)(limit?.GetLongLength(0) ?? 0L));
            }
            else
            {
                Native.Instance.rocksdb_compact_range_cf(Handle, cf.Handle, start, (UIntPtr)(start?.GetLongLength(0) ?? 0L), limit, (UIntPtr)(limit?.GetLongLength(0) ?? 0L));
            }
        }

        public void CompactRange(string start, string limit, ColumnFamilyHandle cf = null, Encoding encoding = null)
        {
            if (encoding is null)
            {
                encoding = Encoding.UTF8;
            }

            CompactRange(start is null ? null : encoding.GetBytes(start), limit is null ? null : encoding.GetBytes(limit), cf);
        }
        
        public void TryCatchUpWithPrimary()
        {
            Native.Instance.rocksdb_try_catch_up_with_primary(Handle);
        }
        
        public void Flush(FlushOptions flushOptions)
        {
            Native.Instance.rocksdb_flush(Handle, flushOptions.Handle);
        }


        /// <summary>
        /// Returns metadata about the file and data in the file. 
        /// </summary>
        /// <param name="populateFileMetadataOnly">setting it to true only populates FileName, 
        /// Filesize and filelevel; By default it is false</param>
        /// <returns><c>LiveFilesMetadata</c> or null in case of failure</returns>
        public List<LiveFileMetadata> GetLiveFilesMetadata(bool populateFileMetadataOnly=false)
        {
            IntPtr buffer = Native.Instance.rocksdb_livefiles(Handle);
            if (buffer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                List<LiveFileMetadata> filesMetadata = new List<LiveFileMetadata>();

                int fileCount = Native.Instance.rocksdb_livefiles_count(buffer);
                for (int index = 0; index < fileCount; index++)
                {
                    LiveFileMetadata liveFileMetadata = new LiveFileMetadata();

                    FileMetadata metadata = new FileMetadata();
                    IntPtr fileMetadata = Native.Instance.rocksdb_livefiles_name(buffer, index);
                    string fileName = Marshal.PtrToStringAnsi(fileMetadata);

                    int level = Native.Instance.rocksdb_livefiles_level(buffer, index);

                    UIntPtr fS = Native.Instance.rocksdb_livefiles_size(buffer, index);
                    ulong fileSize = fS.ToUInt64();

                    metadata.FileName = fileName;
                    metadata.FileLevel = level;
                    metadata.FileSize = fileSize;

                    liveFileMetadata.FileMetadata = metadata;

                    if (!populateFileMetadataOnly)
                    {
                        FileDataMetadata fileDataMetadata = new FileDataMetadata();
                        var smallestKeyPtr = Native.Instance.rocksdb_livefiles_smallestkey(buffer, 
                                                                                           index, 
                                                                         out var smallestKeySize);
                        string smallestKey = Marshal.PtrToStringAnsi(smallestKeyPtr);

                        var largestKeyPtr = Native.Instance.rocksdb_livefiles_largestkey(buffer, 
                                                                                          index,
                                                                         out var largestKeySize);
                        string largestKey = Marshal.PtrToStringAnsi(largestKeyPtr);

                        ulong entries = Native.Instance.rocksdb_livefiles_entries(buffer, index);
                        ulong deletions = Native.Instance.rocksdb_livefiles_deletions(buffer, index);

                        fileDataMetadata.SmallestKeyInFile = smallestKey;
                        fileDataMetadata.LargestKeyInFile = largestKey;
                        fileDataMetadata.NumEntriesInFile = entries;
                        fileDataMetadata.NumDeletionsInFile = deletions;

                        liveFileMetadata.FileDataMetadata = fileDataMetadata;
                    }

                    filesMetadata.Add(liveFileMetadata);
                }           
                
                return filesMetadata;
            }
            finally
            {
                Native.Instance.rocksdb_livefiles_destroy(buffer);
                buffer = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Lean API to just get Live file names. 
        /// Refer to GetLiveFilesMetadata() for the complete metadata
        /// </summary>
        /// <returns></returns>
        public List<string> GetLiveFileNames()
        {
            IntPtr buffer = Native.Instance.rocksdb_livefiles(Handle);
            if (buffer == IntPtr.Zero)
            {
                return new List<string>();
            }

            try
            {
                List<string> liveFiles = new List<string>();

                int fileCount = Native.Instance.rocksdb_livefiles_count(buffer);

                for (int index = 0; index < fileCount; index++)
                {
                    IntPtr fileMetadata = Native.Instance.rocksdb_livefiles_name(buffer, index);
                    string fileName = Marshal.PtrToStringAnsi(fileMetadata);
                    liveFiles.Add(fileName);
                }
                
                return liveFiles;
            }
            finally
            {
                Native.Instance.rocksdb_livefiles_destroy(buffer);
                buffer = IntPtr.Zero;
            }
        }        
    }
}
