
[![Build Status](https://dev.azure.com/curiosity-ai/mosaik/_apis/build/status/rocksdb-sharp?branchName=master)](https://dev.azure.com/curiosity-ai/mosaik/_build/latest?definitionId=20&branchName=master) [![Nuget](https://img.shields.io/nuget/v/rocksdb.svg?maxAge=0&colorB=brightgreen)](https://www.nuget.org/packages/rocksdb/) 

<img src="https://raw.githubusercontent.com/curiosity-ai/rocksdb-sharp/refs/heads/master/csharp/logo-128.png" width="100" height="100" align="right" />

_**rocksdb-sharp**_ is a C# binding for Facebook's [RocksDB](https://github.com/facebook/rocksdb/), based on the original work from [@warrenfalk](https://github.com/warrenfalk). This fork from the original repository has been modified to keep in sync with the latest release from Facebook, and will automatically re-built on new [RocksDB releases](https://github.com/facebook/rocksdb/releases) using Azure Pipelines.

## RocksDb for C# #
RocksDB is a key-value database with a log-structured-merge design, optimized for flash and RAM storage,
which can be tuned to balance write-, read-, and space-amplification factors.

RocksDB is developed by Facebook and is based on LevelDB.
For more information about RocksDB, visit [RocksDB](http://rocksdb.org/) and on [GitHub](https://github.com/facebook/rocksdb)

This library provides C# bindings for rocksdb, implemented as a wrapper for the native rocksdb DLL (unmanaged C++) via the rocksdb C API.

This is a multi-level binding, 
providing direct access to the C API functions (low level) 
plus some helper wrappers on those to aid in marshaling and exception handling (mid level) 
plus an idiomatic C# class hierarchy for ease of use (high level).

### Example (High Level)

```csharp
var options = new DbOptions()
    .SetCreateIfMissing(true);
using (var db = RocksDb.Open(options, path))
{
    // Using strings below, but can also use byte arrays for both keys and values
    db.Put("key", "value");
    string value = db.Get("key");
    db.Remove("key");
}
```
### Usage

#### Using NuGet:

[![Nuget](https://img.shields.io/nuget/v/rocksdb.svg?maxAge=0&colorB=brightgreen)](https://www.nuget.org/packages/rocksdb/) 

```
install-package RocksDb
```

The version of the NuGet package is set to follow the official RocksDB version, with the last 4 numbers representing the build number on Azure - i.e. [NuGet version 6.7.3.6120](https://www.nuget.org/packages/rocksdb/6.7.3.6120) corresponds to release [v6.7.3](https://github.com/facebook/rocksdb/releases/tag/v6.7.3)

This will install the managed library and the correct version of the unmanaged library depending on your operating system. The native64-bit library is automatically built for each official RocksDB release, for Windows, Linux and MacOS, and is included in the package by default.

#### The jemalloc build (linux-x64 only)

The package ships a second Linux x64 library, `librocksdb-jemalloc.so`, built
with RocksDB's `-DROCKSDB_JEMALLOC` support. It is tried first on Linux and
falls back to the ordinary `librocksdb.so`, so you get it automatically or not
at all — there is no setting to turn it on.

**It only loads in a process that already has jemalloc mapped**, which in
practice means starting your application with:

```
LD_PRELOAD=libjemalloc.so.2 dotnet YourApp.dll
```

Anywhere else it is skipped and you transparently get the ordinary library
instead. Nothing breaks, you simply do not get the jemalloc build — so if you
are running it for the allocator behaviour, the `LD_PRELOAD` is not optional.

This is deliberate rather than a packaging oversight. `-DROCKSDB_JEMALLOC`
assumes jemalloc *is* the process allocator, so the library links jemalloc
dynamically instead of embedding a private copy, and distribution builds of
jemalloc use the initial-exec TLS model, which cannot be satisfied by a library
loaded after startup. Without the preload the loader refuses it with
`cannot allocate memory in static TLS block`, which is exactly what makes the
fallback to `librocksdb.so` kick in. `build-native/README.md` explains the
reasoning in full.

Only `linux-x64` on glibc gets this flavour: linux-arm64 and the musl builds
ship the ordinary library alone.


