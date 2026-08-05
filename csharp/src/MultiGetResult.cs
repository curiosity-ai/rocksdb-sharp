using System;

namespace RocksDbSharp
{
    /// <summary>
    /// One key's outcome from <c>MultiGetWithStatus</c>: the value, or the status RocksDB
    /// returned instead of a value.
    /// </summary>
    /// <remarks>
    /// A key that simply is not in the database succeeds with a null <see cref="Value"/>. A key
    /// RocksDB declined to read has <see cref="Error"/> set -- most usefully when
    /// <see cref="ReadOptions.SetValueSizeSoftLimit(ulong)"/> is in play, which from RocksDB
    /// 11.8.0 on aborts the keys after the point where the values read pass the limit so that the
    /// caller can retry exactly those.
    /// </remarks>
    public readonly struct MultiGetResult<TKey, TValue>
    {
        // The rendering of Status::Aborted. The C API reports a key's status only as this
        // message, so the message is what there is to match on.
        private const string AbortedStatus = "Operation aborted";

        public MultiGetResult(TKey key, TValue value, string error)
        {
            Key = key;
            Value = value;
            Error = error;
        }

        public TKey Key { get; }

        /// <summary>
        /// The value read, null if the key is not in the database or was not read at all.
        /// </summary>
        public TValue Value { get; }

        /// <summary>
        /// The status RocksDB returned for this key, or null when the key was read -- whether or
        /// not it turned out to be present.
        /// </summary>
        public string Error { get; }

        public bool Succeeded => Error is null;

        /// <summary>
        /// True when RocksDB aborted this key rather than failing on it, which is what
        /// <see cref="ReadOptions.SetValueSizeSoftLimit(ulong)"/> does to the keys past its
        /// limit. Reading these keys again, on their own or with a larger limit, is expected to
        /// succeed.
        /// </summary>
        public bool WasAborted => Error != null && Error.StartsWith(AbortedStatus, StringComparison.Ordinal);
    }
}
