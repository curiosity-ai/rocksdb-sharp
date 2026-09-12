using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RocksDbSharp;

namespace RocksDbSharp.Lite
{
    /// <summary>
    /// LiteDB-style embedded document database backed by RocksDB. Each collection lives in its own
    /// RocksDB column family; each index on a collection adds an additional column family that maps
    /// [encoded(value), encoded(id)] -&gt; empty, enabling sorted iteration via the underlying iterator.
    /// </summary>
    public sealed class LiteDatabase : IDisposable
    {
        private const string MetaCfName = "_meta";
        private const string DataCfPrefix = "d:";
        private const string IndexCfPrefix = "i:";
        private const char NamePartSeparator = '\u001F'; // ASCII unit-separator; not allowed in user names

        private readonly RocksDb _db;
        private readonly ColumnFamilyHandle _metaCf;
        private readonly Dictionary<string, ColumnFamilyHandle> _allCfs;
        private readonly Dictionary<string, object> _collections = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly object _autoIdLock = new object();
        private readonly LiteDatabaseOptions _options;
        private bool _disposed;

        public string Path { get; }
        public ILiteSerializer Serializer => _options.Serializer;
        internal RocksDb Db => _db;

        public LiteDatabase(string path) : this(path, new LiteDatabaseOptions()) { }

        public LiteDatabase(string path, LiteDatabaseOptions options)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path required", nameof(path));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            Path = path;

            if (_options.CreateIfMissing && !Directory.Exists(path) && !File.Exists(path))
                Directory.CreateDirectory(path);

            var dbOptions = new DbOptions()
                .SetCreateIfMissing(_options.CreateIfMissing)
                .SetCreateMissingColumnFamilies(true);
            _options.ConfigureDb?.Invoke(dbOptions);

            var existing = new List<string>();
            if (RocksDb.TryListColumnFamilies(dbOptions, path, out var listed))
                existing.AddRange(listed);
            if (existing.Count == 0) existing.Add(ColumnFamilies.DefaultName);
            if (!existing.Contains(MetaCfName)) existing.Add(MetaCfName);

            var families = new ColumnFamilies();
            foreach (var name in existing)
            {
                if (name == ColumnFamilies.DefaultName) continue;
                var cfo = new ColumnFamilyOptions();
                _options.ConfigureColumnFamily?.Invoke(cfo);
                families.Add(name, cfo);
            }

            _db = _options.ReadOnly
                ? RocksDb.OpenReadOnly(dbOptions, path, families, errIfLogFileExists: false)
                : RocksDb.Open(dbOptions, path, families);

            _allCfs = new Dictionary<string, ColumnFamilyHandle>(StringComparer.Ordinal)
            {
                [ColumnFamilies.DefaultName] = _db.GetDefaultColumnFamily(),
            };
            foreach (var name in existing)
            {
                if (name == ColumnFamilies.DefaultName) continue;
                _allCfs[name] = _db.GetColumnFamily(name);
            }
            _metaCf = _allCfs[MetaCfName];
        }

        // ---------------- collection registry ----------------

        public ILiteCollection<T> GetCollection<T>(string name)
        {
            ValidateName(name, "collection");
            if (_collections.TryGetValue(name, out var existing))
                return (ILiteCollection<T>)existing;

            var dataCfName = DataCfPrefix + name;
            if (!_allCfs.TryGetValue(dataCfName, out var dataCf))
            {
                if (_options.ReadOnly)
                    throw new LiteException($"Collection '{name}' does not exist (database opened read-only).");
                var cfo = new ColumnFamilyOptions();
                _options.ConfigureColumnFamily?.Invoke(cfo);
                dataCf = _db.CreateColumnFamily(cfo, dataCfName);
                _allCfs[dataCfName] = dataCf;
            }

            var indexPrefix = IndexCfPrefix + name + NamePartSeparator;
            var indexes = _allCfs
                .Where(kv => kv.Key.StartsWith(indexPrefix, StringComparison.Ordinal))
                .Select(kv => (Name: kv.Key.Substring(indexPrefix.Length), Cf: kv.Value));

            var col = new LiteCollection<T>(this, name, dataCf, indexes);
            _collections[name] = col;
            return col;
        }

        public IReadOnlyCollection<string> GetCollectionNames()
        {
            return _allCfs.Keys
                .Where(k => k.StartsWith(DataCfPrefix, StringComparison.Ordinal))
                .Select(k => k.Substring(DataCfPrefix.Length))
                .ToArray();
        }

        public bool CollectionExists(string name) => _allCfs.ContainsKey(DataCfPrefix + name);

        public void DropCollection(string name)
        {
            if (_options.ReadOnly) throw new LiteException("Database is read-only.");
            ValidateName(name, "collection");

            var indexPrefix = IndexCfPrefix + name + NamePartSeparator;
            var indexCfs = _allCfs.Keys
                .Where(k => k.StartsWith(indexPrefix, StringComparison.Ordinal))
                .ToList();
            foreach (var cfName in indexCfs)
            {
                _db.DropColumnFamily(cfName);
                _allCfs.Remove(cfName);
            }

            var dataCfName = DataCfPrefix + name;
            if (_allCfs.ContainsKey(dataCfName))
            {
                _db.DropColumnFamily(dataCfName);
                _allCfs.Remove(dataCfName);
            }

            _db.Remove(AutoIdKey(name), _metaCf);
            _collections.Remove(name);
        }

        // ---------------- index plumbing (internal hooks used by LiteCollection) ----------------

        internal ColumnFamilyHandle CreateIndexCf(string collectionName, string indexName)
        {
            ValidateName(indexName, "index");
            var cfName = IndexCfPrefix + collectionName + NamePartSeparator + indexName;
            if (_allCfs.TryGetValue(cfName, out var existing)) return existing;
            var cfo = new ColumnFamilyOptions();
            _options.ConfigureColumnFamily?.Invoke(cfo);
            var cf = _db.CreateColumnFamily(cfo, cfName);
            _allCfs[cfName] = cf;
            return cf;
        }

        internal void DropIndexCf(string collectionName, string indexName)
        {
            var cfName = IndexCfPrefix + collectionName + NamePartSeparator + indexName;
            if (!_allCfs.ContainsKey(cfName)) return;
            _db.DropColumnFamily(cfName);
            _allCfs.Remove(cfName);
        }

        // ---------------- auto-id ----------------

        private static byte[] AutoIdKey(string collectionName) => Encoding.UTF8.GetBytes("cnt:" + collectionName);

        internal long NextAutoId(string collectionName)
        {
            lock (_autoIdLock)
            {
                var key = AutoIdKey(collectionName);
                var bytes = _db.Get(key, _metaCf);
                long current = 0;
                if (bytes is not null && bytes.Length == 8)
                    current = BinaryPrimitives.ReadInt64BigEndian(bytes);
                long next = current + 1;
                var outBuf = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(outBuf, next);
                _db.Put(key, outBuf, _metaCf);
                return next;
            }
        }

        // ---------------- misc ----------------

        public void Checkpoint(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("targetPath required", nameof(targetPath));
            using var cp = _db.Checkpoint();
            cp.Save(targetPath);
        }

        private static void ValidateName(string name, string kind)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException($"{kind} name is required", nameof(name));
            if (name.IndexOf(NamePartSeparator) >= 0)
                throw new ArgumentException($"{kind} name cannot contain control character 0x1F", nameof(name));
            if (name.StartsWith("_", StringComparison.Ordinal))
                throw new ArgumentException($"{kind} names starting with '_' are reserved", nameof(name));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _db.Dispose();
        }
    }
}
