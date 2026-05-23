using System;
using System.Collections.Generic;

namespace RocksDbSharp.Lite
{
    /// <summary>
    /// Typed view over a RocksDB-backed collection. Provides LiteDB-style CRUD plus index-based iteration.
    /// </summary>
    public interface ILiteCollection<T>
    {
        string Name { get; }

        long Count();

        bool Contains(object id);

        T? FindById(object id);

        IEnumerable<T> FindAll();

        IEnumerable<T> Find(Func<T, bool> predicate);

        /// <summary>Returns documents whose indexed value equals the supplied value, in document-id order.</summary>
        IEnumerable<T> FindByIndex(string indexName, object? value);

        /// <summary>
        /// Returns documents whose indexed value falls within [fromInclusive, toInclusive].
        /// Use null on either bound for an open range. Iteration order matches the natural order of the index value type.
        /// </summary>
        IEnumerable<T> FindByIndexRange(string indexName, object? fromInclusive, object? toInclusive);

        /// <summary>
        /// Inserts a new document. If the id property is the default value, an id is auto-assigned (numeric autoincrement / new Guid).
        /// Returns the (possibly assigned) id.
        /// </summary>
        object Insert(T document);

        /// <summary>Updates an existing document. Returns false if no document with the supplied id exists.</summary>
        bool Update(T document);

        /// <summary>Inserts or replaces a document. The id must already be set.</summary>
        void Upsert(T document);

        /// <summary>Inserts or replaces a document under the supplied id (overrides whatever is on the object).</summary>
        void Upsert(object id, T document);

        /// <summary>Removes the document with the given id. Returns false if no such document exists.</summary>
        bool Delete(object id);

        /// <summary>Removes every document and every index entry. Returns the number of documents removed.</summary>
        long DeleteAll();

        /// <summary>
        /// Registers an index over the given selector. Idempotent: if the index already exists it is reused.
        /// When a new index is created, the collection is scanned once to backfill index entries for existing documents.
        /// </summary>
        void EnsureIndex(string indexName, Func<T, object?> selector);

        /// <summary>Drops the given index, freeing its storage.</summary>
        void DropIndex(string indexName);

        IReadOnlyCollection<string> GetIndexNames();
    }
}
