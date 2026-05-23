using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RocksDbSharp;

namespace RocksDbSharp.Lite
{
    internal sealed class LiteCollection<T> : ILiteCollection<T>
    {
        private readonly LiteDatabase _db;
        private readonly ColumnFamilyHandle _dataCf;
        private readonly PropertyInfo _idProperty;
        private readonly bool _autoId;
        private readonly object _idLock = new object();
        private readonly Dictionary<string, IndexEntry> _indexes = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);

        private sealed class IndexEntry
        {
            public required string Name;
            public required ColumnFamilyHandle Cf;
            public required Func<T, object?> Selector;
        }

        public string Name { get; }

        public LiteCollection(LiteDatabase db, string name, ColumnFamilyHandle dataCf, IEnumerable<(string Name, ColumnFamilyHandle Cf)> existingIndexes)
        {
            _db = db;
            Name = name;
            _dataCf = dataCf;
            (_idProperty, _autoId) = ResolveIdProperty();

            foreach (var (idxName, cf) in existingIndexes)
            {
                _indexes[idxName] = new IndexEntry { Name = idxName, Cf = cf, Selector = _ => null };
            }
        }

        // ---------------- id handling ----------------

        private static (PropertyInfo prop, bool autoId) ResolveIdProperty()
        {
            var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? attributed = null;
            bool autoId = true;
            foreach (var p in props)
            {
                var attr = p.GetCustomAttribute<LiteIdAttribute>();
                if (attr is not null)
                {
                    if (attributed is not null)
                        throw new LiteException($"Type {typeof(T).Name} has more than one [LiteId] property.");
                    attributed = p;
                    autoId = attr.AutoId;
                }
            }
            if (attributed is not null) return (attributed, autoId);

            var byName = props.FirstOrDefault(p => p.Name == "Id");
            if (byName is null)
                throw new LiteException($"Type {typeof(T).Name} has no [LiteId] property and no public 'Id' property.");
            if (!byName.CanWrite)
                throw new LiteException($"Id property on {typeof(T).Name} must have a setter.");
            return (byName, true);
        }

        private object GetId(T doc)
        {
            var v = _idProperty.GetValue(doc);
            if (v is null) throw new LiteException("Document id cannot be null.");
            return v;
        }

        private void SetId(T doc, object id) => _idProperty.SetValue(doc, id);

        private bool IsDefaultId(object id) => id switch
        {
            long l => l == 0,
            int i => i == 0,
            Guid g => g == Guid.Empty,
            string s => string.IsNullOrEmpty(s),
            _ => false,
        };

        private object AssignAutoId(T doc)
        {
            object id;
            var t = _idProperty.PropertyType;
            if (t == typeof(long)) id = _db.NextAutoId(Name);
            else if (t == typeof(int)) id = checked((int)_db.NextAutoId(Name));
            else if (t == typeof(Guid)) id = Guid.NewGuid();
            else throw new LiteException($"Cannot auto-generate id of type {t.Name}; supply one explicitly.");
            SetId(doc, id);
            return id;
        }

        private static byte[] EncodeIdKey(object id) => LiteKey.Encode(id);

        // ---------------- crud ----------------

        public long Count()
        {
            long n = 0;
            using var it = _db.Db.NewIterator(_dataCf);
            for (it.SeekToFirst(); it.Valid(); it.Next()) n++;
            return n;
        }

        public bool Contains(object id) => _db.Db.HasKey(EncodeIdKey(id), _dataCf);

        public T? FindById(object id)
        {
            var bytes = _db.Db.Get(EncodeIdKey(id), _dataCf);
            return bytes is null ? default : _db.Serializer.Deserialize<T>(bytes);
        }

        public IEnumerable<T> FindAll()
        {
            using var it = _db.Db.NewIterator(_dataCf);
            for (it.SeekToFirst(); it.Valid(); it.Next())
            {
                yield return _db.Serializer.Deserialize<T>(it.Value());
            }
        }

        public IEnumerable<T> Find(Func<T, bool> predicate)
        {
            foreach (var doc in FindAll())
                if (predicate(doc)) yield return doc;
        }

        public object Insert(T document)
        {
            var id = GetId(document);
            if (_autoId && IsDefaultId(id))
                id = AssignAutoId(document);

            var keyBytes = EncodeIdKey(id);
            if (_db.Db.HasKey(keyBytes, _dataCf))
                throw new LiteException($"Document with id {id} already exists in collection '{Name}'.");

            WriteDocument(keyBytes, document, previousForIndexes: default);
            return id;
        }

        public bool Update(T document)
        {
            var id = GetId(document);
            var keyBytes = EncodeIdKey(id);
            var existing = _db.Db.Get(keyBytes, _dataCf);
            if (existing is null) return false;

            T? previous = _db.Serializer.Deserialize<T>(existing);
            WriteDocument(keyBytes, document, previous);
            return true;
        }

        public void Upsert(T document)
        {
            var id = GetId(document);
            if (_autoId && IsDefaultId(id))
                id = AssignAutoId(document);
            Upsert(id, document);
        }

        public void Upsert(object id, T document)
        {
            SetId(document, id);
            var keyBytes = EncodeIdKey(id);
            var existingBytes = _db.Db.Get(keyBytes, _dataCf);
            T? previous = existingBytes is null ? default : _db.Serializer.Deserialize<T>(existingBytes);
            WriteDocument(keyBytes, document, previous);
        }

        public bool Delete(object id)
        {
            var keyBytes = EncodeIdKey(id);
            var existing = _db.Db.Get(keyBytes, _dataCf);
            if (existing is null) return false;
            T previous = _db.Serializer.Deserialize<T>(existing);

            using var batch = new WriteBatch();
            batch.Delete(keyBytes, _dataCf);
            foreach (var idx in _indexes.Values)
            {
                var oldKey = BuildIndexKey(idx, previous, keyBytes);
                if (oldKey is not null) batch.Delete(oldKey, idx.Cf);
            }
            _db.Db.Write(batch);
            return true;
        }

        public long DeleteAll()
        {
            long n = 0;
            using var batch = new WriteBatch();
            using (var it = _db.Db.NewIterator(_dataCf))
            {
                for (it.SeekToFirst(); it.Valid(); it.Next())
                {
                    batch.Delete(it.Key(), _dataCf);
                    n++;
                }
            }
            foreach (var idx in _indexes.Values)
            {
                using var it = _db.Db.NewIterator(idx.Cf);
                for (it.SeekToFirst(); it.Valid(); it.Next())
                    batch.Delete(it.Key(), idx.Cf);
            }
            _db.Db.Write(batch);
            return n;
        }

        private void WriteDocument(byte[] keyBytes, T document, T? previousForIndexes)
        {
            var value = _db.Serializer.Serialize(document);
            using var batch = new WriteBatch();
            batch.Put(keyBytes, value, _dataCf);

            foreach (var idx in _indexes.Values)
            {
                if (previousForIndexes is not null)
                {
                    var oldKey = BuildIndexKey(idx, previousForIndexes, keyBytes);
                    if (oldKey is not null) batch.Delete(oldKey, idx.Cf);
                }
                var newKey = BuildIndexKey(idx, document, keyBytes);
                if (newKey is not null) batch.Put(newKey, Array.Empty<byte>(), idx.Cf);
            }
            _db.Db.Write(batch);
        }

        // ---------------- indexes ----------------

        public void EnsureIndex(string indexName, Func<T, object?> selector)
        {
            if (string.IsNullOrWhiteSpace(indexName))
                throw new ArgumentException("Index name is required.", nameof(indexName));

            if (_indexes.TryGetValue(indexName, out var existing))
            {
                existing.Selector = selector;
                return;
            }

            var cf = _db.CreateIndexCf(Name, indexName);
            var entry = new IndexEntry { Name = indexName, Cf = cf, Selector = selector };
            _indexes[indexName] = entry;

            // backfill
            using var it = _db.Db.NewIterator(_dataCf);
            using var batch = new WriteBatch();
            for (it.SeekToFirst(); it.Valid(); it.Next())
            {
                var docKey = it.Key();
                var doc = _db.Serializer.Deserialize<T>(it.Value());
                var idxKey = BuildIndexKey(entry, doc, docKey);
                if (idxKey is not null) batch.Put(idxKey, Array.Empty<byte>(), cf);
            }
            _db.Db.Write(batch);
        }

        public void DropIndex(string indexName)
        {
            if (!_indexes.TryGetValue(indexName, out var entry)) return;
            _db.DropIndexCf(Name, indexName);
            _indexes.Remove(indexName);
        }

        public IReadOnlyCollection<string> GetIndexNames() => _indexes.Keys.ToArray();

        public IEnumerable<T> FindByIndex(string indexName, object? value)
        {
            var idx = GetIndex(indexName);
            var prefix = LiteKey.Encode(value);
            return ScanIndex(idx, prefix, prefix, exactPrefix: true);
        }

        public IEnumerable<T> FindByIndexRange(string indexName, object? fromInclusive, object? toInclusive)
        {
            var idx = GetIndex(indexName);
            var from = fromInclusive is null ? null : LiteKey.Encode(fromInclusive);
            var to   = toInclusive   is null ? null : LiteKey.Encode(toInclusive);
            return ScanIndex(idx, from, to, exactPrefix: false);
        }

        private IndexEntry GetIndex(string name)
        {
            if (!_indexes.TryGetValue(name, out var e))
                throw new LiteException($"Index '{name}' is not registered on collection '{Name}'. Call EnsureIndex first.");
            return e;
        }

        private IEnumerable<T> ScanIndex(IndexEntry idx, byte[]? lower, byte[]? upperInclusive, bool exactPrefix)
        {
            using var it = _db.Db.NewIterator(idx.Cf);
            if (lower is null) it.SeekToFirst();
            else it.Seek(lower);

            while (it.Valid())
            {
                var key = it.Key();

                if (exactPrefix)
                {
                    if (!StartsWith(key, lower!)) yield break;
                }
                else if (upperInclusive is not null)
                {
                    // accept keys whose value-component is <= upperInclusive
                    if (CompareValueComponent(key, upperInclusive) > 0) yield break;
                }

                var docKey = ExtractDocKey(key, idx);
                var bytes = _db.Db.Get(docKey, _dataCf);
                if (bytes is not null)
                    yield return _db.Serializer.Deserialize<T>(bytes);

                it.Next();
            }
        }

        // The index key is [encodedIndexValue][encodedDocumentId]. Both components are self-delimiting
        // (the type tag + payload tells us how much to consume).
        private static int IndexValueLength(byte[] key)
        {
            if (key.Length == 0) throw new LiteException("Index key is empty.");
            return ScalarLength(key, 0);
        }

        private static int ScalarLength(byte[] buf, int offset)
        {
            byte tag = buf[offset];
            switch (tag)
            {
                case LiteKey.TagNull:
                case LiteKey.TagFalse:
                case LiteKey.TagTrue:
                    return 1;
                case LiteKey.TagLong:
                case LiteKey.TagDouble:
                case LiteKey.TagDateTime:
                    return 9;
                case LiteKey.TagGuid:
                    return 17;
                case LiteKey.TagString:
                {
                    int i = offset + 1;
                    while (i < buf.Length && buf[i] != 0) i++;
                    if (i >= buf.Length) throw new LiteException("Malformed string component in index key.");
                    return (i - offset) + 1; // include NUL terminator
                }
                case LiteKey.TagBytes:
                {
                    if (offset + 5 > buf.Length) throw new LiteException("Malformed bytes component in index key.");
                    int len = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(offset + 1, 4));
                    return 5 + len;
                }
                default:
                    throw new LiteException($"Unknown key tag 0x{tag:X2}.");
            }
        }

        private static byte[] ExtractDocKey(byte[] indexKey, IndexEntry _idx)
        {
            int valLen = IndexValueLength(indexKey);
            var docKey = new byte[indexKey.Length - valLen];
            Buffer.BlockCopy(indexKey, valLen, docKey, 0, docKey.Length);
            return docKey;
        }

        private static int CompareValueComponent(byte[] indexKey, byte[] upperEncoded)
        {
            int valLen = IndexValueLength(indexKey);
            int min = Math.Min(valLen, upperEncoded.Length);
            for (int i = 0; i < min; i++)
            {
                int c = indexKey[i] - upperEncoded[i];
                if (c != 0) return c;
            }
            return valLen - upperEncoded.Length;
        }

        private static bool StartsWith(byte[] key, byte[] prefix)
        {
            if (key.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (key[i] != prefix[i]) return false;
            return true;
        }

        private byte[]? BuildIndexKey(IndexEntry idx, T doc, byte[] docKey)
        {
            object? v;
            try { v = idx.Selector(doc); }
            catch { return null; }
            if (v is null) return null;
            var encVal = LiteKey.Encode(v);
            return LiteKey.Concat(encVal, docKey);
        }
    }
}
