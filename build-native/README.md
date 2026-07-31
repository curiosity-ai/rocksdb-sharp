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

The dependency versions are read out of RocksDB's own `Makefile`, so they always
match what upstream links into its own release artifacts.

## Linux

```
./build-rocksdb-linux.sh [--arch x64|arm64] [--libc glibc|musl] [--no-jemalloc]
```

Defaults to the architecture and libc of the machine it runs on. Requires
`make`, a C++ toolchain, `git`, `curl` and — for the jemalloc flavour —
`libjemalloc-dev`.

For `linux-x64`/glibc it produces two libraries:

* `librocksdb.so`
* `librocksdb-jemalloc.so`, the same build with jemalloc statically linked in.
  RocksDbSharp probes for this one first on Linux, so it is what most
  applications end up loading.

Cross compiling to `arm64` needs `g++-aarch64-linux-gnu` on `PATH`.

CI does not call the script directly, it goes through the container wrapper, so
that the glibc and musl versions the artifacts are built against are pinned here
instead of being inherited from the agent image:

```
./build-rocksdb-linux-docker.sh --arch x64   --libc glibc
./build-rocksdb-linux-docker.sh --arch x64   --libc musl
./build-rocksdb-linux-docker.sh --arch arm64 --libc glibc
./build-rocksdb-linux-docker.sh --arch arm64 --libc musl
```

The last of those runs emulated under qemu, because no musl cross toolchain is
packaged for Alpine, and takes considerably longer than the others.

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
