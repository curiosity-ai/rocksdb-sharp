# RocksDB.Lite

A LiteDB-style embedded document database built on top of RocksDB. The goal is a small,
focused API for typed CRUD plus sorted, iterator-friendly indexes — not a full ORM.

## Layout

```
lite/
  RocksDbSharp.Lite.csproj
  src/
    Attributes.cs           [LiteId], [LiteIgnore]
    ILiteCollection.cs      Public collection interface
    LiteCollection.cs       Implementation (CRUD + indexes)
    LiteDatabase.cs         DB open/close, collection/CF registry
    LiteDatabaseOptions.cs  Configuration
    LiteException.cs        Library error type
    LiteKey.cs              Order-preserving key encoding
    LiteSerializer.cs       ILiteSerializer + default Json impl
```

Tests live in `../Tests/LiteTest`.

## Storage model

Every collection and every index is its own RocksDB **column family**:

| CF name pattern              | Contents                                                            |
| ---------------------------- | ------------------------------------------------------------------- |
| `default`                    | Unused (RocksDB requires it to exist).                              |
| `_meta`                      | Per-collection auto-increment counters (`cnt:<collection>`).        |
| `d:<collection>`             | Document data. Key = `LiteKey.Encode(id)`. Value = serialized doc.  |
| `i:<collection><idx>`  | Index entries. Key = `encoded(value) || encoded(id)`. Value = "".   |

The `` (ASCII Unit Separator) splits the collection and index portions of an
index column family name. User-facing names that contain `` or start with `_`
are rejected.

### Why a CF per index

A column family in RocksDB is an independent keyspace with its own sorted iterator.
Putting each index in its own CF means a `FindByIndex` or `FindByIndexRange` is just
a `NewIterator` + `Seek` on that CF, walking only entries for the relevant index
rather than scanning a shared keyspace and filtering.

## Key encoding (`LiteKey.cs`)

All ids and index values pass through `LiteKey.Encode(object)`. The encoding rules
are tuned so that RocksDB's default lexicographic comparator yields the natural order
of the value:

* `null`, `false`, `true` → 1-byte tag.
* Integer types → 9 bytes: tag + big-endian 64-bit two's-complement with the sign bit flipped (negatives sort before positives lexicographically).
* `float`/`double` → 9 bytes: IEEE-754 bits with the standard "flip sign, or all-bits if negative" trick.
* `DateTime`/`DateTimeOffset` → 9 bytes: ticks, encoded like a long.
* `Guid` → 17 bytes: tag + raw 16-byte representation.
* `string` → tag + UTF-8 bytes + `0x00` terminator. **Strings cannot contain a literal NUL** (validated on encode).
* `byte[]` → tag + 4-byte big-endian length + raw bytes.
* anything else → falls back to `IFormattable.ToString(InvariantCulture)` and is encoded as a string.

Each encoded scalar is **self-delimiting** (the tag plus payload determine its length without external metadata). That is what lets index keys be a plain concatenation `encoded(value) || encoded(id)` and still be split back apart by `LiteCollection.ScalarLength`. Don't add a new tag without also extending `ScalarLength`.

Type tags are ordered (`TagNull` < `TagFalse` < `TagLong` < ...) so mixed-type indexes also produce a deterministic ordering — but in practice indexes should be over a single type.

## Document identity

The id property is found via, in order:
1. A `[LiteId]`-annotated public property.
2. A public property literally named `Id`.

Supported types: `long`, `int`, `Guid`, `string`. Auto-assignment on `Insert` happens
when the id is the type's default value and `[LiteId(AutoId = true)]` (the default).
`long`/`int` use a counter in `_meta` (`LiteDatabase.NextAutoId`); `Guid` uses
`Guid.NewGuid()`; `string` ids must be supplied.

## Serialization

Documents flow through `ILiteSerializer`. The default `JsonLiteSerializer` uses
`System.Text.Json`. If users want BSON or MessagePack they can implement the interface
and pass it via `LiteDatabaseOptions.Serializer`.

The id is stored both *inside* the serialized value (so it survives round-tripping)
and *as* the key. After an auto-id assignment the property is set on the original
object before serialization.

## Atomic writes

Every mutation that touches both the data CF and one or more index CFs is wrapped in
a single `WriteBatch`. There is no transactional API beyond that — overlapping
writers on the same id race, last-writer-wins.

## What is intentionally not here

* **No expression-based queries.** `Find(predicate)` is a delegate over an in-memory
  scan of `FindAll()`. Anything performance-sensitive should go through an index.
* **No automatic re-indexing on reopen.** Indexes survive across opens because their
  CFs survive, but the `Func<T,object?> selector` is per-process. Callers must
  re-register selectors with `EnsureIndex` after reopen if they want future writes to
  maintain those indexes (existing index entries remain queryable in the meantime).
  This is an explicit trade-off — selectors are .NET delegates and can't be persisted.
* **No unique indexes / no compound indexes** in the first cut. Both fit the current
  CF-per-index layout but aren't implemented yet.
* **No LINQ provider.** Possible future work; not in scope now.

## When extending

* Adding a new scalar type to `LiteKey.Encode`: also extend `LiteCollection.ScalarLength` so index keys can still be split.
* Adding new column families: any CF that gets created at runtime must also be opened on startup. `LiteDatabase` does this by listing all existing CFs via `RocksDb.TryListColumnFamilies` before opening.
* Adding a new public API: prefer methods on `ILiteCollection<T>` over methods on `LiteDatabase`; the database object should mostly be a registry.

## Running the tests

```
dotnet test Tests/LiteTest/LiteTests.csproj
```

Both `lite/RocksDbSharp.Lite.csproj` and `Tests/LiteTest/LiteTests.csproj` consume
RocksDB exclusively through the `RocksDB` NuGet package — no project reference to
`csharp/RocksDbSharp.csproj`. The test project references only the Lite project and
picks up the managed assembly and the native runtime files transitively from the
package. (The older `Tests/MergeTest` and `Tests/ConsoleTest` use a mixed
`ProjectReference + ExcludeAssets` pattern that pre-dates this convention.)
