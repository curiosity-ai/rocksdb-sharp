using System;
using RocksDbSharp;

namespace RocksDbSharp.Lite
{
    /// <summary>
    /// Configuration for opening a <see cref="LiteDatabase"/>.
    /// </summary>
    public sealed class LiteDatabaseOptions
    {
        /// <summary>
        /// When true (default), the database directory is created if missing.
        /// </summary>
        public bool CreateIfMissing { get; set; } = true;

        /// <summary>
        /// When true, opens the database read-only.
        /// </summary>
        public bool ReadOnly { get; set; }

        /// <summary>
        /// Pluggable document serializer. Defaults to <see cref="JsonLiteSerializer.Default"/>.
        /// </summary>
        public ILiteSerializer Serializer { get; set; } = JsonLiteSerializer.Default;

        /// <summary>
        /// Optional hook to further customize the underlying RocksDB <see cref="DbOptions"/>.
        /// </summary>
        public Action<DbOptions>? ConfigureDb { get; set; }

        /// <summary>
        /// Optional hook to further customize per-column-family options before they are passed to RocksDB.
        /// </summary>
        public Action<ColumnFamilyOptions>? ConfigureColumnFamily { get; set; }
    }
}
