using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RocksDbSharp
{
    /// <summary>
    /// Asynchronous read methods, the counterparts of RocksDB 11.8.0's DB::GetAsync() and
    /// DB::MultiGetAsync().
    /// </summary>
    /// <remarks>
    /// <para>
    /// RocksDB's own asynchronous reads are a C++ API only: 11.8.0 added DB::GetAsync() and
    /// DB::MultiGetAsync() to <c>include/rocksdb/db.h</c>, but nothing in <c>include/rocksdb/c.h</c>
    /// reaches them, and this binding is a wrapper over the C API. The methods below therefore run
    /// the ordinary synchronous read on the thread pool: the calling thread is released while the
    /// read is in flight, which is what an <c>await</c>ing caller wants, but a pool thread is
    /// occupied for the duration rather than the read suspending on a coroutine.
    /// </para>
    /// <para>
    /// That is also the behaviour upstream falls back to unless RocksDB is built with coroutine
    /// support (<c>USE_COROUTINES</c>, which pulls in folly), the DB has a read executor
    /// configured, and the filesystem implements <c>FSRandomAccessFile::SubmitReadAsync()</c>. The
    /// libraries shipped with this package are not built with folly, so even a C API for GetAsync
    /// would run the synchronous path and invoke its callback inline.
    /// </para>
    /// <para>
    /// What does carry real asynchronous IO through the C API is
    /// <see cref="ReadOptions.SetAsyncIO(bool)"/>, which lets RocksDB issue the file reads of a
    /// single MultiGet in parallel. Prefer one <see cref="MultiGetAsync(byte[][], ColumnFamilyHandle[], ReadOptions, CancellationToken)"/>
    /// with that option set over many concurrent <c>GetAsync</c> calls: it is one read request into
    /// RocksDB and one pool thread, instead of one of each per key.
    /// </para>
    /// </remarks>
    public sealed partial class RocksDb
    {
        /// <summary>
        /// Reads the value associated with <paramref name="key"/> without blocking the calling
        /// thread, returning null if the key is not present.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels the read while it is still queued. A read that has already reached RocksDB runs
        /// to completion -- the C API has no way to abandon one -- so the returned task can still
        /// complete successfully after cancellation was requested.
        /// </param>
        public Task<byte[]> GetAsync(byte[] key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null, CancellationToken cancellationToken = default)
        {
            if (key is null)
                throw new ArgumentNullException(nameof(key));

            var options = readOptions ?? DefaultReadOptions;
            return Task.Run(() => Get(key, cf, options), cancellationToken);
        }

        /// <summary>
        /// Reads the value associated with <paramref name="key"/> without blocking the calling
        /// thread, returning null if the key is not present.
        /// </summary>
        public Task<string> GetAsync(string key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null, Encoding encoding = null, CancellationToken cancellationToken = default)
        {
            if (key is null)
                throw new ArgumentNullException(nameof(key));

            var options = readOptions ?? DefaultReadOptions;
            var keyEncoding = encoding ?? DefaultEncoding;
            return Task.Run(() => Get(key, cf, options, keyEncoding), cancellationToken);
        }

        /// <summary>
        /// Reads all of <paramref name="keys"/> in one request without blocking the calling thread.
        /// Keys that are not present come back with a null value.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels the read while it is still queued; see
        /// <see cref="GetAsync(byte[], ColumnFamilyHandle, ReadOptions, CancellationToken)"/>.
        /// </param>
        public Task<KeyValuePair<byte[], byte[]>[]> MultiGetAsync(byte[][] keys, ColumnFamilyHandle[] cf = null, ReadOptions readOptions = null, CancellationToken cancellationToken = default)
        {
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));

            var options = readOptions ?? DefaultReadOptions;
            return Task.Run(() => MultiGet(keys, cf, options), cancellationToken);
        }

        /// <summary>
        /// Reads all of <paramref name="keys"/> in one request without blocking the calling thread.
        /// Keys that are not present come back with a null value.
        /// </summary>
        public Task<KeyValuePair<string, string>[]> MultiGetAsync(string[] keys, ColumnFamilyHandle[] cf = null, ReadOptions readOptions = null, CancellationToken cancellationToken = default)
        {
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));

            var options = readOptions ?? DefaultReadOptions;
            return Task.Run(() => MultiGet(keys, cf, options), cancellationToken);
        }

        /// <summary>
        /// Reads all of <paramref name="keys"/> in one request without blocking the calling thread,
        /// reporting each key's status instead of throwing on the first key that has one. See
        /// <see cref="MultiGetWithStatus(byte[][], ColumnFamilyHandle[], ReadOptions)"/>.
        /// </summary>
        public Task<MultiGetResult<byte[], byte[]>[]> MultiGetWithStatusAsync(byte[][] keys, ColumnFamilyHandle[] cf = null, ReadOptions readOptions = null, CancellationToken cancellationToken = default)
        {
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));

            var options = readOptions ?? DefaultReadOptions;
            return Task.Run(() => MultiGetWithStatus(keys, cf, options), cancellationToken);
        }

        /// <summary>
        /// Reads all of <paramref name="keys"/> in one request without blocking the calling thread,
        /// reporting each key's status instead of throwing on the first key that has one. See
        /// <see cref="MultiGetWithStatus(string[], ColumnFamilyHandle[], ReadOptions, Encoding)"/>.
        /// </summary>
        public Task<MultiGetResult<string, string>[]> MultiGetWithStatusAsync(string[] keys, ColumnFamilyHandle[] cf = null, ReadOptions readOptions = null, Encoding encoding = null, CancellationToken cancellationToken = default)
        {
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));

            var options = readOptions ?? DefaultReadOptions;
            var keyEncoding = encoding ?? DefaultEncoding;
            return Task.Run(() => MultiGetWithStatus(keys, cf, options, keyEncoding), cancellationToken);
        }

        /// <summary>
        /// Determines whether <paramref name="key"/> is present without blocking the calling thread
        /// and without transferring the value.
        /// </summary>
        public Task<bool> HasKeyAsync(byte[] key, ColumnFamilyHandle cf = null, ReadOptions readOptions = null, CancellationToken cancellationToken = default)
        {
            if (key is null)
                throw new ArgumentNullException(nameof(key));

            var options = readOptions ?? DefaultReadOptions;
            return Task.Run(() => HasKey(key, key.GetLongLength(0), cf, options), cancellationToken);
        }
    }
}
