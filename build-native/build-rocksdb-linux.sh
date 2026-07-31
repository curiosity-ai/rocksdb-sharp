#!/usr/bin/env bash
#
# Builds the rocksdb shared library for Linux.
#
# Usage: ./build-rocksdb-linux.sh [--arch x64|arm64] [--libc glibc|musl]
#                                 [--no-jemalloc]
#
# Outputs, under build-native/runtimes/linux-<arch>/native/:
#
#   glibc: librocksdb.so            plain build, self contained
#          librocksdb-jemalloc.so   same, linked against jemalloc
#   musl:  librocksdb-musl.so
#
# --no-jemalloc skips the jemalloc flavour, for targets where no jemalloc for
# the target architecture is available to link against.
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
    if grep -qi musl <<< "$(ldd --version 2>&1)"; then
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

# cmake and curl are for the compression libraries: they are downloaded with
# curl and snappy is a cmake project.
for tool in make git curl cmake "${CXX:-g++}"; do
    command -v "$tool" > /dev/null 2>&1 || fail "Build requires ${tool}"
done

# ---------------------------------------------------------------------------
# Sources and dependencies
# ---------------------------------------------------------------------------

checkout_rocksdb

# PORTABLE=1 keeps -march=native out of the build so the artifact runs on any
# CPU of the target architecture.
export PORTABLE=1

# build_detect_platform links jemalloc whenever it finds it on the build
# machine, which would leave every library here needing libjemalloc.so.2 at
# runtime -- including the plain one, whose whole job is to be the fallback for
# when jemalloc is unavailable. The jemalloc flavour below opts back in
# explicitly.
export ROCKSDB_DISABLE_JEMALLOC=1

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

# The C runtime, and nothing else. A compression library or libstdc++ turning
# up here would have to be installed on the machine running the package for the
# library to load at all.
BASE_DEPENDENCIES="libc.so.6 libm.so.6 libdl.so.2 librt.so.1 libpthread.so.0 libgcc_s.so.1
                   ld-linux-x86-64.so.2 ld-linux-aarch64.so.1
                   libc.musl-x86_64.so.1 libc.musl-aarch64.so.1"

# Checks the library just built, before it is published under its final name.
check_library() {
    local label="$1"
    shift

    info "checking ${label}"

    verify_library "${ROCKSDB_SRC_DIR}/librocksdb.so" $COMPRESSION_SYMBOLS "$@"
    verify_dependencies "${ROCKSDB_SRC_DIR}/librocksdb.so" "${CROSS_PREFIX}readelf" \
        $BASE_DEPENDENCIES
}

if [ "$TARGET_LIBC" = "musl" ]; then
    # The musl flavour ships as a single library: RocksDbSharp probes for the
    # -jemalloc suffix before the -musl one, so a musl+jemalloc library would
    # never be picked up under its own name.
    build_shared_lib "librocksdb-musl.so" "" ""
    check_library "librocksdb-musl.so"
    publish_library "$RID" "${ROCKSDB_SRC_DIR}/librocksdb.so" "librocksdb-musl.so"
else
    build_shared_lib "librocksdb.so" "" ""
    check_library "librocksdb.so"
    publish_library "$RID" "${ROCKSDB_SRC_DIR}/librocksdb.so" "librocksdb.so"
fi

if [ "$TARGET_LIBC" = "glibc" ] && [ "$WITH_JEMALLOC" = "yes" ]; then
    # --- jemalloc flavour -------------------------------------------------
    #
    # This one is deliberately *not* self contained: it links jemalloc
    # dynamically, and so only loads in a process that already has jemalloc
    # mapped -- one that was started under LD_PRELOAD=libjemalloc.so.2, or whose
    # executable links it.
    #
    # That is not a shortcut, it is the only arrangement that is correct.
    # rocksdb's -DROCKSDB_JEMALLOC support assumes jemalloc *is* the process
    # allocator: it calls malloc_usable_size on ordinary new-allocated objects
    # all over the codebase while the nodump allocator hands it pointers from
    # mallocx, and the two only agree when a single jemalloc serves both.
    # Embedding a private copy in this library instead satisfies neither. It
    # cannot take over malloc for the process -- memory that libc functions such
    # as strdup allocated would then be freed by jemalloc, which segfaults -- and
    # if it does not take over malloc, glibc's malloc_usable_size ends up reading
    # the header of a jemalloc allocation, which is worse than useless.
    #
    # A statically linked jemalloc could not load here anyway: distribution
    # builds use the initial-exec TLS model, which cannot be satisfied by a
    # library dlopened after startup ("cannot allocate memory in static TLS
    # block").
    #
    # When jemalloc is absent, this library simply fails to load and
    # RocksDbSharp falls through to librocksdb.so, which is why the plain
    # library above must never depend on jemalloc itself.
    info "linking jemalloc dynamically"

    # JEMALLOC=1 turns on -DROCKSDB_JEMALLOC/-DJEMALLOC_NO_DEMANGLE in rocksdb's
    # Makefile. ROCKSDB_DISABLE_JEMALLOC stays exported so that the -ljemalloc
    # below is the only place jemalloc enters the link.
    build_shared_lib "librocksdb-jemalloc.so" "-ljemalloc" "JEMALLOC=1"

    verify_library "${ROCKSDB_SRC_DIR}/librocksdb.so" $COMPRESSION_SYMBOLS \
        rocksdb_jemalloc_nodump_allocator_create

    # Unlike the plain library, this one is expected to need libjemalloc.
    verify_dependencies "${ROCKSDB_SRC_DIR}/librocksdb.so" "${CROSS_PREFIX}readelf" \
        libjemalloc.so.2 $BASE_DEPENDENCIES

    "${CROSS_PREFIX}readelf" -d "${ROCKSDB_SRC_DIR}/librocksdb.so" \
        | grep -q "libjemalloc" \
        || fail "the jemalloc library does not actually link against jemalloc"

    publish_library "$RID" "${ROCKSDB_SRC_DIR}/librocksdb.so" "librocksdb-jemalloc.so"
fi

info "published to ${RUNTIMES_DIR}/${RID}/native:"
ls -la "${RUNTIMES_DIR}/${RID}/native/"
