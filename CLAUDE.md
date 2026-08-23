# rocksdb-sharp

.NET bindings for RocksDB, shipped as the `RocksDB` NuGet package together with
the native libraries for every supported platform.

Layout: `csharp/` is the managed library (`src/Native.cs` and
`src/Native.Load.cs` are generated — see below, do not hand edit them),
`build-codegen/` generates those from upstream's `c.h`, `build-native/` builds
the native libraries, `.azure-devops/` runs both in CI, and `rocksdbversion`
pins the upstream release the whole repository tracks.

To move onto a new upstream release, use the `update-rocksdb-release` skill in
`.claude/skills/` — it covers repinning, regenerating, building and validating,
and the failure modes each of those has hit before.

## The jemalloc library only loads under LD_PRELOAD

`librocksdb-jemalloc.so` is built for **linux-x64 / glibc only** and links
jemalloc dynamically. It loads *only* in a process that already has jemalloc
mapped:

```
LD_PRELOAD=libjemalloc.so.2 dotnet YourApp.dll
```

Anywhere else `dlopen` refuses it with `cannot allocate memory in static TLS
block`, because distribution builds of jemalloc use the initial-exec TLS model
and that cannot be satisfied by a library loaded after startup.

That failure is load-bearing, not a bug. `AutoNativeImport` probes the
`-jemalloc` name first on Linux, the failure is what makes it fall through to
the ordinary `librocksdb.so`, and the plain library must therefore never grow a
jemalloc dependency of its own — `verify_dependencies` in
`build-native/common.sh` enforces that. `build-native/README.md` explains why a
private or statically linked jemalloc would not work instead.

Two practical consequences:

* **Testing the jemalloc build means preloading jemalloc.** Drop
  `librocksdb-jemalloc.so` into a test app's `runtimes/linux-x64/native/` and
  run it without the preload and you are silently exercising the ordinary
  library — or, if that is the only library present, getting a
  `TypeInitializationException` that lists the TLS error among the paths it
  tried. Neither looks like the jemalloc build failing.
* **Do not "fix" the load failure.** Embedding jemalloc, linking it statically,
  or dropping the `-jemalloc` probe would each break the arrangement rather
  than repair it.

## Do not commit built binaries

The `.so`/`.dll`/`.dylib` files under `csharp/runtimes/**/native/` are 52-byte
placeholders that the Azure pipelines replace at pack time, and they are tracked
in git. Overwriting one with a real library adds tens of megabytes to the
repository permanently. Point tests at the build output in
`build-native/runtimes/` instead.
