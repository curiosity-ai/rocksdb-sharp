#!/usr/bin/env bash
#
# Builds rocksdb.dll for Windows (x64).
#
# Run this from a Visual Studio developer environment (for example
# "Developer Command Prompt for VS 2022") under git bash / MSYS, so that
# cl.exe, msbuild and the Windows SDK are on PATH:
#
#     ./build-rocksdb-windows.sh
#
# Output: build-native/runtimes/win-x64/native/rocksdb.dll
#
# zlib, snappy, lz4 and zstd come from vcpkg's x64-windows-static triplet and
# the C runtime is linked statically, so the DLL is self contained.

set -u

. "$(cd "$(dirname "$0")" && pwd)/common.sh"

CONCURRENCY="$(detect_concurrency)"

command -v cmake > /dev/null 2>&1 || fail "CMake is not installed (https://cmake.org/download/)"
command -v git > /dev/null 2>&1 || fail "Build requires git"
test -n "${WindowsSdkDir:-}" \
    || fail "This must be run from a build environment such as the Developer Command Prompt"

# The hosted Windows images ship vcpkg pre-installed and point
# VCPKG_INSTALLATION_ROOT at it, spelled with backslashes. Everything below
# builds paths out of it that end up in CMake variables and in log lines, both
# of which are happier with forward slashes; Windows itself accepts either.
VCPKG_ROOT="${VCPKG_INSTALLATION_ROOT:-C:/vcpkg}"
VCPKG_ROOT="${VCPKG_ROOT//\\//}"

# Where vcpkg puts the finished libraries. Not "packages/<port>_<triplet>",
# which is scratch space vcpkg is free to clean out once it has installed from
# it -- and does, which is why this build stopped finding zlib.lib there.
VCPKG_INSTALLED="${VCPKG_ROOT}/installed/x64-windows-static"

info "building rocksdb ${ROCKSDBVERSION} for win-x64 with ${CONCURRENCY} jobs"
info "using vcpkg at ${VCPKG_ROOT}"

# ---------------------------------------------------------------------------
# Dependencies
# ---------------------------------------------------------------------------

# The static triplet gives us .lib files built against the static CRT, matching
# the /MT build of rocksdb below.
vcpkg.exe install \
    zlib:x64-windows-static \
    snappy:x64-windows-static \
    lz4:x64-windows-static \
    zstd:x64-windows-static \
    || fail "unable to install libraries with vcpkg.exe"

# The release library a port installed, given the names it is known to use.
#
# These are not stable. vcpkg names the library whatever the upstream project's
# own build names it, and zlib 1.3.2 renamed its static Windows library from
# zlib.lib to zs.lib -- which is why this build started failing to find a file
# it had been finding for years. Trying a list beats hardcoding today's answer,
# and an unknown name fails here with the directory listed rather than as a
# missing symbol at link time.
vcpkg_lib() {
    local name candidate

    for name in "$@"; do
        candidate="${VCPKG_INSTALLED}/lib/${name}"
        test -f "$candidate" && { echo "$candidate"; return 0; }
    done

    fail "vcpkg installed none of [$*], only:
$(ls "${VCPKG_INSTALLED}/lib" 2>/dev/null | sed 's/^/    /')"
}

# The debug counterpart of a release library. Only Release is ever built here,
# but rocksdb's thirdparty.inc wants both paths set; vcpkg's debug libraries
# either carry a "d" suffix or keep the release name, so try both and fall back
# to the release library where the port ships no debug build at all.
debug_counterpart() {
    local base name

    base="$(basename "$1")"

    for name in "${base%.lib}d.lib" "$base"; do
        test -f "${VCPKG_INSTALLED}/debug/lib/${name}" \
            && { echo "${VCPKG_INSTALLED}/debug/lib/${name}"; return 0; }
    done

    echo "$1"
}

# rocksdb's thirdparty.inc reads these out of the environment; without them it
# falls back to the long dead THIRDPARTY_HOME/*.Library layout.
#
# Assigned before being exported, because the exit status of `export VAR=$(...)`
# is export's own and would swallow a failure inside the substitution.
ZLIB_LIB_RELEASE="$(vcpkg_lib zs.lib zlib.lib zlibstatic.lib)" || exit 1
LZ4_LIB_RELEASE="$(vcpkg_lib lz4.lib liblz4.lib)" || exit 1
SNAPPY_LIB_RELEASE="$(vcpkg_lib snappy.lib libsnappy.lib)" || exit 1
ZSTD_LIB_RELEASE="$(vcpkg_lib zstd.lib libzstd.lib zstd_static.lib)" || exit 1

export ZLIB_INCLUDE="${VCPKG_INSTALLED}/include"
export ZLIB_LIB_RELEASE
export ZLIB_LIB_DEBUG="$(debug_counterpart "$ZLIB_LIB_RELEASE")"

export LZ4_INCLUDE="${VCPKG_INSTALLED}/include"
export LZ4_LIB_RELEASE
export LZ4_LIB_DEBUG="$(debug_counterpart "$LZ4_LIB_RELEASE")"

export SNAPPY_INCLUDE="${VCPKG_INSTALLED}/include"
export SNAPPY_LIB_RELEASE
export SNAPPY_LIB_DEBUG="$(debug_counterpart "$SNAPPY_LIB_RELEASE")"

export ZSTD_INCLUDE="${VCPKG_INSTALLED}/include"
export ZSTD_LIB_RELEASE
export ZSTD_LIB_DEBUG="$(debug_counterpart "$ZSTD_LIB_RELEASE")"

info "linking against $(basename "$ZLIB_LIB_RELEASE"), $(basename "$LZ4_LIB_RELEASE"), $(basename "$SNAPPY_LIB_RELEASE") and $(basename "$ZSTD_LIB_RELEASE")"

# ---------------------------------------------------------------------------
# Sources
# ---------------------------------------------------------------------------

checkout_rocksdb

# rocksdb still declares cmake_minimum_required(VERSION 3.12), so CMP0091 is
# OLD and CMAKE_MSVC_RUNTIME_LIBRARY is ignored: CMake emits the default
# /MD into the generated projects even though WITH_MD_LIBRARY=0 asks for /MT.
# Rewrite the runtime library in the generated projects so rocksdb and the
# static vcpkg libraries agree on the CRT.
update_vcxproj() {
    info "patching vcxproj files for the static VC runtime"
    /bin/find . -type f -name '*.vcxproj' -exec \
        sed -i 's/MultiThreadedDLL/MultiThreaded/g; s/MultiThreadedDebugDLL/MultiThreadedDebug/g' '{}' ';'
}

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

(cd "${ROCKSDB_SRC_DIR}" && {
    rm -rf build && mkdir -p build

    (cd build && {
        # Notes on the options below:
        #  WITH_WINDOWS_UTF8_FILENAMES  open files by UTF-8 name regardless of
        #                               the system code page
        #  WITH_MD_LIBRARY=0            static CRT, see update_vcxproj above
        #  PORTABLE=1                   no -march=native equivalent, the DLL has
        #                               to run on any x64 machine
        #  USE_RTTI=1                   RTTI is off in MSVC release builds by
        #                               default; rocksdb's options machinery
        #                               uses dynamic_cast
        # Tools, tests and benchmarks are all switched off: only rocksdb-shared
        # is built below.
        cmake -G "Visual Studio 17 2022" -A x64 \
            -DCMAKE_BUILD_TYPE=Release \
            -DCMAKE_CXX_STANDARD=20 \
            -DCMAKE_CXX_FLAGS="/wd4267" \
            -DWITH_WINDOWS_UTF8_FILENAMES=1 \
            -DWITH_MD_LIBRARY=0 \
            -DPORTABLE=1 \
            -DUSE_RTTI=1 \
            -DWITH_GFLAGS=0 \
            -DWITH_SNAPPY=1 \
            -DWITH_ZLIB=1 \
            -DWITH_LZ4=1 \
            -DWITH_ZSTD=1 \
            -DWITH_TOOLS=0 \
            -DWITH_CORE_TOOLS=0 \
            -DWITH_TRACE_TOOLS=0 \
            -DWITH_BENCHMARK_TOOLS=0 \
            -DWITH_TESTS=0 \
            .. || fail "Running cmake failed"

        update_vcxproj || warn "failed to patch vcxproj files for the static VC runtime"
    }) || fail "cmake build generation failed"

    info "starting rocksdb build"

    # Building the one target we need instead of the whole solution, so that
    # ldb/sst_dump/db_bench and friends are never compiled.
    cmake --build build --config Release --target rocksdb-shared --parallel "${CONCURRENCY}" \
        || fail "Rocksdb release build failed"

    info "finished rocksdb build"

    test -f build/Release/rocksdb-shared.dll || fail "rocksdb-shared.dll was not produced"
}) || fail "rocksdb build failed"

publish_library "win-x64" "${ROCKSDB_SRC_DIR}/build/Release/rocksdb-shared.dll" "rocksdb.dll"

info "done"
