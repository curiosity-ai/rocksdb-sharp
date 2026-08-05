
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

### Asynchronous reads

RocksDB 11.8.0 added `DB::GetAsync()` and `DB::MultiGetAsync()`, but only to
`include/rocksdb/db.h` — nothing in `include/rocksdb/c.h` reaches them, and this library is a
wrapper over the C API. There is therefore no `GetAsync` here yet; it is waiting on a C API for
those methods upstream.

What RocksDB does expose through the C API is `ReadOptions.SetAsyncIO(true)`, which lets it issue
the file reads of a single `MultiGet` in parallel:

```csharp
var values = db.MultiGet(keys, readOptions: new ReadOptions().SetAsyncIO(true));
```

### Usage

#### Using NuGet:

[![Nuget](https://img.shields.io/nuget/v/rocksdb.svg?maxAge=0&colorB=brightgreen)](https://www.nuget.org/packages/rocksdb/) 

```
install-package RocksDb
```

The version of the NuGet package is set to follow the official RocksDB version, with the last 4 numbers representing the build number on Azure - i.e. [NuGet version 6.7.3.6120](https://www.nuget.org/packages/rocksdb/6.7.3.6120) corresponds to release [v6.7.3](https://github.com/facebook/rocksdb/releases/tag/v6.7.3)

This will install the managed library and the correct version of the unmanaged library depending on your operating system. The native64-bit library is automatically built for each official RocksDB release, for Windows, Linux and MacOS, and is included in the package by default.


