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
# VCPKG_INSTALLATION_ROOT at it.
VCPKG_ROOT="${VCPKG_INSTALLATION_ROOT:-C:/vcpkg}"
VCPKG_PACKAGES="${VCPKG_ROOT}/packages"

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

# rocksdb's thirdparty.inc reads these out of the environment; without them it
# falls back to the long dead THIRDPARTY_HOME/*.Library layout.
export ZLIB_INCLUDE="${VCPKG_PACKAGES}/zlib_x64-windows-static/include"
export ZLIB_LIB_DEBUG="${VCPKG_PACKAGES}/zlib_x64-windows-static/debug/lib/zlib.lib"
export ZLIB_LIB_RELEASE="${VCPKG_PACKAGES}/zlib_x64-windows-static/lib/zlib.lib"

export LZ4_INCLUDE="${VCPKG_PACKAGES}/lz4_x64-windows-static/include"
export LZ4_LIB_DEBUG="${VCPKG_PACKAGES}/lz4_x64-windows-static/debug/lib/lz4.lib"
export LZ4_LIB_RELEASE="${VCPKG_PACKAGES}/lz4_x64-windows-static/lib/lz4.lib"

export SNAPPY_INCLUDE="${VCPKG_PACKAGES}/snappy_x64-windows-static/include"
export SNAPPY_LIB_DEBUG="${VCPKG_PACKAGES}/snappy_x64-windows-static/debug/lib/snappy.lib"
export SNAPPY_LIB_RELEASE="${VCPKG_PACKAGES}/snappy_x64-windows-static/lib/snappy.lib"

export ZSTD_INCLUDE="${VCPKG_PACKAGES}/zstd_x64-windows-static/include"
export ZSTD_LIB_DEBUG="${VCPKG_PACKAGES}/zstd_x64-windows-static/debug/lib/zstd_d.lib"
export ZSTD_LIB_RELEASE="${VCPKG_PACKAGES}/zstd_x64-windows-static/lib/zstd.lib"

for lib in "$ZLIB_LIB_RELEASE" "$LZ4_LIB_RELEASE" "$SNAPPY_LIB_RELEASE" "$ZSTD_LIB_RELEASE"; do
    test -f "$lib" || fail "vcpkg did not produce ${lib}"
done

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
