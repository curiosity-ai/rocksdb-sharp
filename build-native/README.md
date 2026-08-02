# Scripts for building the RocksDB native libraries used by rocksdb-sharp

There is one script per operating system, plus a small dispatcher that picks the
right one for the machine you are on:

| script | builds |
| --- | --- |
| `build-rocksdb-linux.sh` | `linux-x64` and `linux-arm64`, glibc and musl |
| `build-rocksdb-macos.sh` | `osx-arm64` and `osx-x64` |
| `build-rocksdb-windows.sh` | `win-x64` |
| `build-rocksdb-linux-docker.sh` | runs the Linux script in a container, for the flavours a glibc x64 agent cannot build directly |
| `build-rocksdb.sh` | dispatches to whichever of the three matches `uname` |
| `common.sh` | shared helpers, sourced by the scripts above |

The version of RocksDB that gets built is the first three components of
[`../rocksdbversion`](../rocksdbversion); the fourth is this repository's own
build revision.

Every script writes its result to `runtimes/<rid>/native/`, which is the layout
the NuGet package expects. The pipelines in [`../.azure-devops`](../.azure-devops)
upload those files to blob storage, and `build-nuget.yaml` downloads them again
when packing.

## Self-contained artifacts

zlib, bzip2, snappy, lz4 and zstd are compiled from source as static archives
and linked into the RocksDB library — on Windows they come from vcpkg's
`x64-windows-static` triplet instead. The C++ runtime is linked statically too
(`-static-libstdc++` on Linux, `/MT` on Windows).

The result is a library that depends on nothing but the operating system's own
C runtime, so no companion libraries have to be shipped alongside it and no
`install_name_tool` fixups are needed on macOS.

The dependency versions and checksums are read out of RocksDB's own `Makefile`,
so they always match what upstream links into its own release artifacts.

The tarballs are fetched by `common.sh` rather than by RocksDB's makefile
targets, which find them already in place and skip their own download. RocksDB
hardcodes a single URL per dependency and some of those do not keep old
releases — zlib.net serves only the current release from its root and moves
everything else to `/fossils`, so RocksDB's pinned URL starts returning 404 for
everyone the day zlib publishes a new version. Each dependency therefore has a
list of locations that are tried in turn, and every download is checked against
the SHA-256 RocksDB declares for it.

## Linux

```
./build-rocksdb-linux.sh [--arch x64|arm64] [--libc glibc|musl] [--no-jemalloc]
```

Defaults to the architecture and libc of the machine it runs on. Requires
`make`, `cmake`, a C++ toolchain, `git`, `curl`, and `libjemalloc-dev` for the
jemalloc flavour.

RocksDB has been a C++20 codebase since version 10 and does not build with
anything older than GCC 11 or Clang 13 — `db.h` uses `using enum` and the
block-based table builder includes `<semaphore>`. The script compiles a small
probe with those two features before it starts, so an old compiler is reported
as such instead of burying the build in template errors several hundred files
in.

For `linux-x64`/glibc it produces two libraries:

* `librocksdb.so`
* `librocksdb-jemalloc.so`, the same build linked against jemalloc.
  RocksDbSharp probes for this one first on Linux.

The jemalloc library is the one exception to the self-contained rule: it links
jemalloc dynamically and therefore only loads in a process that already has
jemalloc mapped, such as one started under `LD_PRELOAD=libjemalloc.so.2`.

That is deliberate. RocksDB's `-DROCKSDB_JEMALLOC` support assumes jemalloc *is*
the process allocator — it calls `malloc_usable_size` on ordinary
`new`-allocated objects throughout the codebase, while the nodump allocator
hands that same function pointers from `mallocx`, and the two only agree when a
single jemalloc serves both. Embedding a private copy satisfies neither half:
it cannot take over `malloc` for the process (memory allocated inside libc by
e.g. `strdup` would then be freed by jemalloc, which crashes), and if it does
not take over `malloc`, glibc's `malloc_usable_size` ends up reading the header
of a jemalloc allocation. A statically linked jemalloc could not be loaded here
anyway: distribution builds use the initial-exec thread-local storage model,
which cannot be satisfied by a library `dlopen`ed after startup.

Where jemalloc is absent, the library simply does not load and RocksDbSharp
falls through to `librocksdb.so` — which is why the plain library must never
depend on jemalloc itself.

Cross compiling to `arm64` needs `g++-aarch64-linux-gnu` on `PATH`. Exporting
`CC` is not enough to carry that through to every dependency — bzip2's makefile
assigns `CC=gcc` itself, and a makefile assignment beats the environment — so
the compression libraries are built with `CC` named on `make`'s command line,
where it becomes an override the nested makes inherit. `verify_archives_match_compiler`
then checks each archive against the architecture the compiler actually targets,
because otherwise a dependency that quietly built for the host is only noticed
by the linker at the very end of the build, in a message that names neither the
library nor the architecture.

CI does not call the script directly, it goes through the container wrapper, so
that the toolchain and the glibc and musl versions the artifacts are built
against are pinned here instead of being inherited from the agent image:

```
./build-rocksdb-linux-docker.sh --arch x64   --libc glibc
./build-rocksdb-linux-docker.sh --arch x64   --libc musl
./build-rocksdb-linux-docker.sh --arch arm64 --libc glibc
./build-rocksdb-linux-docker.sh --arch arm64 --libc musl
```

The last of those runs emulated under qemu, because no musl cross toolchain is
packaged for Alpine, and takes considerably longer than the others.

### How old a glibc the libraries load on

The glibc builds happen in `ubuntu:22.04`, and the published libraries load on
**glibc 2.34 and newer** — RHEL/Alma/Rocky 9, Amazon Linux 2023, Ubuntu 22.04,
Debian 12, SLES 15 SP6 and everything released since.

Those two versions being different is the point. What a library requires is the
highest symbol version it references, not the glibc it was compiled against:
glibc only stamps a new version onto a symbol when that symbol's behaviour
changes, so the great majority of any build still resolves against far older
releases. 2.34 is where it stops here, because that is where `libpthread` and
`libdl` were folded into `libc` and everything they exported was reversioned.

Jammy is the oldest image that gets there:

* Bullseye, which these builds used until RocksDB moved to C++20, stops at GCC
  10 and can no longer compile RocksDB at all.
* Bookworm can, but its libstdc++ calls `arc4random` for `std::random_device`.
  That symbol was added in glibc 2.36, and `-static-libstdc++` links that code
  into the library, so building there would quietly cost us RHEL 9 and Amazon
  Linux 2023. Jammy's glibc has no `arc4random` for its libstdc++ to find, so
  the same code reads `/dev/urandom` instead.

`verify_glibc_floor` in `common.sh` checks this after every glibc build and
fails with the offending symbols listed. The floor is therefore a property the
build enforces rather than one inferred from the base image, and moving it is a
deliberate edit of `GLIBC_FLOOR` in `build-rocksdb-linux.sh` rather than a side
effect of bumping a container tag.

musl versions no symbols and ships one library per release, so there is no
equivalent check for the musl flavour.

## macOS

```
./build-rocksdb-macos.sh [--arch arm64|x64|all]
```

Both slices are cross compiled from whichever Mac you run this on: Xcode's
toolchain targets either architecture through `-arch`, and the compression
libraries are rebuilt for each one. `--arch all` (the default) builds `arm64`
first, so a problem shows up before the x64 slice is started.

Requires the Xcode command line tools; Homebrew is no longer used.

## Windows

Prerequisites: Git for Windows (specifically, the git bash environment), CMake,
and Visual Studio 2022.

1. Open "Developer Command Prompt for VS 2022"
2. Run git's `bash.exe`
3. execute `./build-rocksdb-windows.sh`

vcpkg is expected to be present; the hosted build images ship it and point
`VCPKG_INSTALLATION_ROOT` at it. Set that variable yourself if your vcpkg lives
somewhere other than `C:\vcpkg`.

Compiler options are passed to CMake in their `-wd4267` form rather than the
`/wd4267` one MSVC is usually given, because git bash rewrites any argument
that looks like an absolute POSIX path into a Windows one — `/wd4267` arrived
at `cl.exe` as `C:/Program Files/Git/wd4267`, which it tried to compile.
Argument conversion is switched off for the CMake call as well; nothing in it
needs converting.

The libraries are read out of `installed/x64-windows-static`, vcpkg's finished
tree, rather than out of `packages/<port>_<triplet>`, which is scratch space
vcpkg is free to clean out once it has installed from it. Their file names are
not stable either — vcpkg calls a library whatever the upstream project's own
build calls it, and zlib 1.3.2 renamed its static Windows library from
`zlib.lib` to `zs.lib` — so the script tries the names each port is known to
have used and fails with the directory listed if none of them is there.
