---
name: update-rocksdb-release
description: Move rocksdb-sharp onto a new upstream RocksDB release - check facebook/rocksdb for the newest release, repin rocksdbversion, regenerate the C API bindings, build and validate the native library on Linux, and commit the result. Use this whenever the request touches the pinned RocksDB version at all: "update to the latest release", "is there a new rocksdb", "bump rocksdb to 11.9", "regenerate the bindings", "rebuild the native library", "why is the version behind". Also use it when a native build or the codegen breaks after a version bump, since the failure modes and their fixes are documented here.
---

# Updating rocksdb-sharp to a new RocksDB release

This package tracks upstream RocksDB releases one for one: the NuGet version is
the upstream version plus a build revision, and the bindings in
`csharp/src/Native.cs` are generated from that release's `include/rocksdb/c.h`.
Moving to a new release means repinning one file, regenerating from the new
header, and proving the result actually loads.

The order below matters. Each step gives the next one something to check
against, and the cheap checks come first so a bad release is found before an
hour of compiling.

## 1. Find out whether there is anything to do

```bash
.claude/skills/update-rocksdb-release/scripts/check-upstream-release.sh
```

It prints the newest upstream release, what this repository pins, and a verdict.

Two things it protects you from. Tags must be sorted with `sort -V` and never
lexically, because `11.10.0` sorts *below* `11.9.0` as text and a new release
would look like an old one. And it reads tags over `git ls-remote` rather than
`api.github.com`, which egress policy refuses in many sandboxed sessions.

**If the verdict is "up to date", say so and stop.** That is a complete,
correct answer to "update to the latest release". Do not manufacture a commit
to have something to show — an empty or invented change costs the user a review
cycle and teaches them not to trust the next report.

## 2. Repin the version

`rocksdbversion` holds `<major>.<minor>.<patch>.<our build revision>`. The first
three name the upstream tag that `build-native/` checks out and that the header
is read from; the fourth is this repository's own revision of that release, and
resets to `1` for a new upstream version.

```bash
echo "11.9.0.1" > rocksdbversion   # for upstream v11.9.0
```

Nothing else needs editing by hand. `csharp/src/Native.Load.cs` also carries the
version, but the generator rewrites that file wholesale in the next step, so
editing it now only creates a conflict with itself.

## 3. Regenerate the bindings

```bash
cd build-codegen && dotnet run
```

The generator reads `../rocksdbversion`, downloads
`https://raw.githubusercontent.com/facebook/rocksdb/v<version>/include/rocksdb/c.h`,
and rewrites `../csharp/src/Native.cs` and `../csharp/src/Native.Load.cs`. Its
paths are relative, so it only works from inside `build-codegen/`.

Expect to have to fix the generator. It parses C by regular expression, and
each release that adds a construct it has not seen before breaks it — the 11.8.1
bump needed four fixes (duplicate region headings, hex and shifted enum values,
enum names with no usable common prefix, newly typedef'd callbacks). Typical
symptoms and where to look in `Generate.cs`:

| Symptom | Cause |
| --- | --- |
| throws on a duplicate key | two regions in `c.h` share a `/* Title */` heading |
| an enum named `Log`, `Type`, or similar | the common-prefix guess did not land on a word boundary; add a `ManualEnumName` entry |
| enum values all wrong, not just one | a value written as hex or a shift that the fold did not reduce |
| a callback marshalled as `IntPtr` that a caller has to supply | `c.h` gave it a typedef name; it may need a real delegate in `ManualCallbackDelegates` |

Rerun the generator after each fix. It is idempotent: on an unchanged
`rocksdbversion` it reproduces the committed files byte for byte, so
`git diff --stat` after a no-op run is the fastest way to tell whether a
generator change altered anything you did not intend.

## 4. Read the generated diff before trusting it

The diff is tens of thousands of lines and nobody reads it line by line. Read it
for three things instead:

```bash
# functions gained and lost
git diff csharp/src/Native.cs | grep -E '^\+.*public abstract' | wc -l
git diff csharp/src/Native.cs | grep -E '^-.*public abstract' | wc -l
```

* **Removals** are the dangerous ones. For each, check whether upstream dropped
  it from the library too or only moved it, then grep the hand-written layers —
  `Native.Marshaled.cs`, `Native.Wrap.cs`, `RocksDb.cs`, `Options/`, `Cluster/`,
  `Replication/` — for the name. Those files are not generated and will not be
  fixed for you.
* **Changed signatures on existing functions** mean a silent ABI change; the
  managed side may compile and then corrupt memory. Look for a function that is
  both added and removed in the diff.
* **Additions** are usually fine, but scan the new enum types for names the
  guesser invented badly, since those become public API.

## 5. Build the managed library

```bash
dotnet build csharp/RocksDbSharp.csproj -c Release
```

This catches generated code that does not compile and hand-written code that
referred to something now gone. It cannot catch a binding the native library
does not export — that is step 7.

## 6. Build the native library on Linux

A release bump is not validated until a native library has actually been built
from the new tag. Upstream's own build system changes between releases, and it
breaks in platform-specific ways.

What CI runs, and what produces a shippable artifact:

```bash
build-native/build-rocksdb-linux-docker.sh --arch x64 --libc glibc
```

The container matters. `build-rocksdb-linux.sh` run directly on a modern
machine builds a library that references your host's glibc symbols, and
`verify_glibc_floor` then fails it against the 2.34 floor the package promises
— correctly, because that artifact would not load on RHEL 9. The wrapper pins
`ubuntu:22.04`, which is the oldest image that can compile RocksDB's C++20 and
still land under the floor.

Without Docker, run the script directly to validate the *bindings* even though
the artifact is not shippable:

```bash
build-native/build-rocksdb-linux.sh --arch x64 --libc glibc --no-jemalloc
```

It builds, verifies the compression codecs and the dependency list, and only
then fails on the glibc floor. The library is still there, at
`build-native/rocksdb/librocksdb.so`, and it is good enough for step 7. Say
plainly in your report that the floor check did not pass and why.

`--no-jemalloc` skips the flavour that needs `libjemalloc-dev`. The jemalloc
library is deliberately *not* self-contained; `build-native/README.md` explains
why, and it is not worth reproducing locally.

Two things that go wrong here:

* **Dependency downloads are refused (403).** The build fetches zlib, bzip2,
  snappy, lz4 and zstd tarballs, and sandboxed sessions are commonly allowed
  `zlib.net` and `sourceware.org` but not GitHub archive URLs. That is egress
  policy, not a broken script. Report the blocked host and do not route around
  it — a hand-fetched tarball would fail the SHA-256 check anyway, which is the
  point of the check.
* **"Build parameters changed since the last build".** See below.

## 7. Prove the library and the bindings agree

```bash
.claude/skills/update-rocksdb-release/scripts/verify-exports.sh \
    build-native/rocksdb/librocksdb.so
```

RocksDbSharp binds late: `AutoNativeImport` resolves every abstract method on
`Native` by name at type-initialisation time. A binding the library does not
export is not a compile error anywhere — it is a `TypeInitializationException`
on a user's machine the first time they open a database. The generator will
emit a declaration for anything left in `c.h`, including a function upstream
deleted from the library, so this check is the only thing standing between that
and a shipped package.

Then run something real against it. Exports only prove the symbols exist; they
say nothing about whether the ABI behind them still matches.

Mind where the tests get their native library from. `Tests/MergeTest` and
`Tests/ConsoleTest` deliberately do *not* use the libraries in
`csharp/runtimes/` — they reference the published `RocksDB` NuGet package at a
pinned version purely to copy its runtime files into their output. So a test run
straight after a bump exercises the **old** native library and passes without
telling you anything about the new one. Build first, then overwrite what landed
in the output directory:

```bash
dotnet build Tests/MergeTest -c Release
cp build-native/rocksdb/librocksdb.so \
   Tests/MergeTest/bin/Release/net10.0/runtimes/linux-x64/native/librocksdb.so
# the loader prefers -jemalloc, then plain, then -musl: remove the stale
# siblings so the one under test is the one that loads
rm -f Tests/MergeTest/bin/Release/net10.0/runtimes/linux-x64/native/librocksdb-{jemalloc,musl}.so
dotnet test Tests/MergeTest -c Release --no-build
```

Exercise more than open/put/get — column families, write batches, iterators,
snapshots, checkpoints, merge operators, transactions, and every compression
codec, since the codecs are what the static linking in step 6 is for. Write a
throwaway console program for whatever the test projects do not already cover
and say in the commit message what you ran.

Once the new package is published, those pinned `RocksDB` package versions in
the two test `.csproj` files want updating — both files carry a comment saying
so. That is a follow-up, not part of this commit: the version does not exist on
nuget.org until the pipeline has built it.

Do not copy a built library over `csharp/runtimes/<rid>/native/*.so`. Those are
52-byte placeholders that the Azure pipelines replace at pack time, and they are
tracked in git — committing a real binary over one adds tens of megabytes to
the repository permanently.

## 8. The rest of the matrix

Linux is the one you can build and the one that catches binding problems, but
the package ships five RIDs, and the macOS and Windows pipelines can fail on
their own. Before calling the update done, either run the other pipelines or say
explicitly which platforms were not built.

### "Build parameters changed since the last build"

RocksDB 11.8 added a check that hashes `CC/CXX/CFLAGS/CXXFLAGS/LDFLAGS` into
`$OBJ_DIR/.build_signature` on every goal it considers a build, and refuses to
start the next one if the hash moved. It reports itself as:

```
Makefile:NNNN: *** Build parameters changed since the last build (OBJ_DIR=.).
Existing object files are stale and must be removed.
```

`build_static_compression_libs` passes `ALLOW_BUILD_PARAMETER_CHANGE=1` so the
five dependency goals do not record a signature — they compile no RocksDB
object, and the signature they would leave never matches the `shared_lib` build
that follows it. If this reappears, look for a *new* `make` invocation in
`build-native/` between a `clean-rocks` and a `shared_lib`, not for a way to
disable the check globally: `shared_lib` still writes and compares its own
signature, which is what catches a genuine flag change.

The general shape is worth remembering. Upstream's build system is a dependency
like any other, and the platform scripts differ in the order they clean, build
dependencies and link — so a change in it can break exactly one platform while
the others stay green. When one pipeline fails after a bump and the others pass,
compare the order of `make` calls between the scripts before suspecting the
agent.

## 9. Commit

Everything above belongs in one commit on the working branch: `rocksdbversion`,
the two generated files, any `Generate.cs` fixes, and any build script fix the
new release forced.

Write the message for someone deciding whether to trust the upgrade. The 11.8.1
commit is the model to follow — state how many functions were added and removed
and name the removals, list each generator fix and why it was needed, and end
with what was actually validated and on which platform. "Update to 11.9.0" tells
a reviewer nothing they could not read from the diff.

Do not commit built binaries.
