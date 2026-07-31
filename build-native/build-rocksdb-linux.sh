#!/usr/bin/env bash
#
# Builds the rocksdb shared library for Linux.
#
# Usage: ./build-rocksdb-linux.sh [--arch x64|arm64] [--libc glibc|musl]
#                                 [--no-jemalloc]
#
# Outputs, under build-native/runtimes/linux-<arch>/native/:
#
#   glibc: librocksdb.so            plain build
#          librocksdb-jemalloc.so   same, with jemalloc statically linked in
#   musl:  librocksdb-musl.so
#
# --no-jemalloc skips the jemalloc flavour, for targets where no jemalloc build
# for the target architecture is available.
#
# zlib, bzip2, snappy, lz4 and zstd are compiled from source and linked in
# statically, so the published library only depends on the system C/C++
# runtime. Build the musl flavour by running this script inside an Alpine
# container; build linux-arm64 either on an arm64 machine or on x64 with the
# aarch64-linux-gnu cross toolchain installed.

set -u

. "$(cd "$(dirname "$0")" && pwd)/common.sh"

TARGET_ARCH=""
TARGET_LIBC=""
WITH_JEMALLOC=yes

while [ $# -gt 0 ]; do
    case "$1" in
        --arch) TARGET_ARCH="${2:-}"; shift 2 ;;
        --libc) TARGET_LIBC="${2:-}"; shift 2 ;;
        --no-jemalloc) WITH_JEMALLOC=no; shift ;;
        -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
        *) fail "unknown argument: $1" ;;
    esac
done

# ---------------------------------------------------------------------------
# Target selection
# ---------------------------------------------------------------------------

HOST_ARCH="$(uname -m)"

if [ -z "$TARGET_ARCH" ]; then
    case "$HOST_ARCH" in
        x86_64)        TARGET_ARCH=x64 ;;
        aarch64|arm64) TARGET_ARCH=arm64 ;;
        *) fail "unsupported host architecture ${HOST_ARCH}, pass --arch explicitly" ;;
    esac
fi

case "$TARGET_ARCH" in
    x64)   GNU_ARCH=x86_64;  RID=linux-x64 ;;
    arm64) GNU_ARCH=aarch64; RID=linux-arm64 ;;
    *) fail "unsupported architecture ${TARGET_ARCH}, expected x64 or arm64" ;;
esac

if [ -z "$TARGET_LIBC" ]; then
    # ldd prints its banner on stderr for glibc and musl alike.
    if ldd --version 2>&1 | head -1 | grep -qi musl; then
        TARGET_LIBC=musl
    else
        TARGET_LIBC=glibc
    fi
fi

case "$TARGET_LIBC" in
    glibc) LIBC_SUFFIX="" ;;
    musl)  LIBC_SUFFIX="-musl" ;;
    *) fail "unsupported libc ${TARGET_LIBC}, expected glibc or musl" ;;
esac

CONCURRENCY="$(detect_concurrency)"

info "building rocksdb ${ROCKSDBVERSION} for ${RID} (${TARGET_LIBC}) with ${CONCURRENCY} jobs"

# ---------------------------------------------------------------------------
# Toolchain
# ---------------------------------------------------------------------------

CROSS_PREFIX=""

if [ "$GNU_ARCH" != "$HOST_ARCH" ]; then
    if [ "$TARGET_LIBC" = "musl" ]; then
        CROSS_PREFIX="${GNU_ARCH}-linux-musl-"
    else
        CROSS_PREFIX="${GNU_ARCH}-linux-gnu-"
    fi
    command -v "${CROSS_PREFIX}g++" > /dev/null 2>&1 \
        || fail "cross compiling to ${TARGET_ARCH} requires ${CROSS_PREFIX}g++ on PATH"

    export CC="${CROSS_PREFIX}gcc"
    export CXX="${CROSS_PREFIX}g++"
    export AR="${CROSS_PREFIX}ar"
    export RANLIB="${CROSS_PREFIX}ranlib"

    # build_detect_platform derives this from `uname -m` unless told otherwise.
    # CROSS_COMPILE is deliberately left unset: the cross toolchain compiles and
    # links every one of rocksdb's feature probes (none of them are executed),
    # and setting it would silently drop platform defines such as
    # -DROCKSDB_FALLOCATE_PRESENT from the build.
    export TARGET_ARCHITECTURE="${GNU_ARCH}"

    info "cross compiling with ${CXX}"
fi

STRIP="${CROSS_PREFIX}strip"

command -v make > /dev/null 2>&1 || fail "Build requires make"
command -v "${CXX:-g++}" > /dev/null 2>&1 || fail "Build requires a C++ compiler"

# ---------------------------------------------------------------------------
# Sources and dependencies
# ---------------------------------------------------------------------------

checkout_rocksdb

# PORTABLE=1 keeps -march=native out of the build so the artifact runs on any
# CPU of the target architecture.
export PORTABLE=1

build_static_compression_libs "$CONCURRENCY"

# Keep the archives out of reach of `make clean`, which deletes every *.a in
# the tree, so the two library variants below can share one dependency build.
DEPS_DIR="${BUILD_NATIVE_DIR}/deps/${RID}${LIBC_SUFFIX}"
rm -rf "$DEPS_DIR" && mkdir -p "$DEPS_DIR" || fail "unable to create ${DEPS_DIR}"

STATIC_DEPS=""
for archive in $COMPRESSION_LDFLAGS; do
    cp -f "$archive" "$DEPS_DIR/" || fail "unable to stage $archive"
    STATIC_DEPS="${STATIC_DEPS} ${DEPS_DIR}/$(basename "$archive")"
done

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

# Builds librocksdb.so in the rocksdb tree.
#   $1 label used in log output
#   $2 extra archive to link in, may be empty
#   $3 extra variables to pass to make, may be empty
build_shared_lib() {
    local label="$1"
    local extra_ldflags="$2"
    local extra_make_vars="$3"

    info "building ${label}"

    (cd "${ROCKSDB_SRC_DIR}" && {
        # Rebuild from scratch: the two variants differ in compile-time defines.
        make clean-rocks > /dev/null 2>&1 || true
        rm -f librocksdb.so*

        # -static-libstdc++ binds the C++ runtime into the library, so the
        # target machine's libstdc++ does not have to be as new as the build
        # machine's.
        make -j"${CONCURRENCY}" shared_lib \
            EXTRA_CXXFLAGS="-static-libstdc++ ${COMPRESSION_CPPFLAGS}" \
            EXTRA_CFLAGS="${COMPRESSION_CPPFLAGS}" \
            EXTRA_LDFLAGS="-static-libstdc++ ${STATIC_DEPS} ${extra_ldflags}" \
            ${extra_make_vars} || fail "${label} build failed"

        "$STRIP" librocksdb.so || warn "unable to strip ${label}"
    }) || fail "${label} build failed"
}

if [ "$TARGET_LIBC" = "musl" ]; then
    # The musl flavour ships as a single library: RocksDbSharp probes for the
    # -jemalloc suffix before the -musl one, so a musl+jemalloc library would
    # never be picked up under its own name.
    build_shared_lib "librocksdb-musl.so" "" ""
    verify_library "${ROCKSDB_SRC_DIR}/librocksdb.so" ZSTD_compress LZ4_compress_default
    publish_library "$RID" "${ROCKSDB_SRC_DIR}/librocksdb.so" "librocksdb-musl.so"
else
    build_shared_lib "librocksdb.so" "" ""
    verify_library "${ROCKSDB_SRC_DIR}/librocksdb.so" ZSTD_compress LZ4_compress_default
    publish_library "$RID" "${ROCKSDB_SRC_DIR}/librocksdb.so" "librocksdb.so"
fi

if [ "$TARGET_LIBC" = "glibc" ] && [ "$WITH_JEMALLOC" = "yes" ]; then
    # --- jemalloc flavour -------------------------------------------------
    #
    # RocksDbSharp prefers librocksdb-jemalloc.so over librocksdb.so on Linux,
    # so this is the library most users end up running. jemalloc is linked in
    # from its static PIC archive rather than as -ljemalloc, so the target
    # machine does not need jemalloc installed.
    JEMALLOC_STATIC_LIB="${JEMALLOC_STATIC_LIB:-$("${CC:-gcc}" -print-file-name=libjemalloc_pic.a)}"

    test -f "$JEMALLOC_STATIC_LIB" \
        || fail "libjemalloc_pic.a not found (install libjemalloc-dev, or set JEMALLOC_STATIC_LIB)"

    info "linking jemalloc from ${JEMALLOC_STATIC_LIB}"

    # --whole-archive is required, not an optimisation: rocksdb declares the
    # jemalloc entry points as weak symbols (see port/jemalloc_helper.h) so that
    # it can null-check them at runtime, and weak undefined references do not
    # pull members out of a static archive. Without this the archive would
    # contribute nothing, HasJemalloc() would return false and the jemalloc
    # library would behave exactly like the plain one.
    #
    # RocksDbSharp dlopens with RTLD_LOCAL, so jemalloc's malloc/free stay
    # private to librocksdb-jemalloc.so and do not replace the allocator of the
    # host process.
    JEMALLOC_LINK="-Wl,--whole-archive ${JEMALLOC_STATIC_LIB} -Wl,--no-whole-archive"

    # JEMALLOC=1 turns on -DROCKSDB_JEMALLOC/-DJEMALLOC_NO_DEMANGLE in rocksdb's
    # Makefile. ROCKSDB_DISABLE_JEMALLOC=1 stops build_detect_platform from
    # *also* putting a dynamic -ljemalloc on the link line, which would make the
    # library refuse to load on machines without jemalloc installed.
    export ROCKSDB_DISABLE_JEMALLOC=1
    build_shared_lib "librocksdb-jemalloc.so" "$JEMALLOC_LINK" "JEMALLOC=1"
    unset ROCKSDB_DISABLE_JEMALLOC

    verify_library "${ROCKSDB_SRC_DIR}/librocksdb.so" \
        ZSTD_compress LZ4_compress_default \
        rocksdb_jemalloc_nodump_allocator_create mallocx mallctl

    publish_library "$RID" "${ROCKSDB_SRC_DIR}/librocksdb.so" "librocksdb-jemalloc.so"
fi

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

info "shared library dependencies:"
for lib in "${RUNTIMES_DIR}/${RID}/native/"librocksdb*.so; do
    echo "  $(basename "$lib"):"
    "${CROSS_PREFIX}readelf" -d "$lib" | sed -n 's/.*NEEDED.*\[\(.*\)\]/    \1/p'
done

info "done"
