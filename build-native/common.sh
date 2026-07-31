# Shared helpers for the rocksdb native build scripts.
#
# This file is meant to be sourced, not executed:
#
#     . "$(dirname "$0")/common.sh"
#
# It provides logging helpers, the rocksdb version/remote to build, a source
# checkout helper and the routine that builds the compression libraries as
# static PIC archives so that the resulting rocksdb library is self contained.

# shellcheck shell=bash

set -o pipefail

ROCKSDBREMOTE="${ROCKSDBREMOTE:-https://github.com/facebook/rocksdb}"

# Directory holding the build scripts (build-native/).
BUILD_NATIVE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Where the rocksdb sources are checked out and built.
ROCKSDB_SRC_DIR="${BUILD_NATIVE_DIR}/rocksdb"

# Where the built libraries are copied to, one directory per .NET RID.
RUNTIMES_DIR="${BUILD_NATIVE_DIR}/runtimes"

# The four leading numbers of ../rocksdbversion are "<rocksdb version>.<our
# build revision>"; only the first three identify the upstream release tag.
ROCKSDBVNUM="$(cut -d. -f1-3 "${BUILD_NATIVE_DIR}/../rocksdbversion")"
ROCKSDBVERSION="v${ROCKSDBVNUM}"

fail() {
    >&2 echo -e "\033[1;31m$1\033[0m"
    exit 1
}

warn() {
    >&2 echo -e "\033[1;33m$1\033[0m"
}

info() {
    echo -e "\033[1;34m==> $1\033[0m"
}

# Number of parallel compile jobs, derived from the machine we are running on.
detect_concurrency() {
    if [ -n "${CONCURRENCY:-}" ]; then
        echo "${CONCURRENCY}"
    elif command -v nproc > /dev/null 2>&1; then
        nproc
    elif command -v sysctl > /dev/null 2>&1; then
        sysctl -n hw.ncpu
    else
        echo 4
    fi
}

# Shallow-fetch a single ref into the current directory.
checkout() {
    local name="$1"
    local remote="$2"
    local fetchref="$3"

    test -d .git || git init || fail "unable to initialize $name repository"
    info "fetching ${fetchref} from ${remote}"
    git fetch --depth 1 "$remote" "$fetchref" || fail "Unable to fetch ${fetchref} from ${remote}"
    git checkout --force FETCH_HEAD || fail "Unable to checkout $name ${fetchref}"
    git clean -xdf -e '*.tar.gz' -e 'zlib-*' -e 'bzip2-*' -e 'snappy-*' -e 'lz4-*' -e 'zstd-*' -e '*.a' > /dev/null
}

# Check out the rocksdb release matching ../rocksdbversion.
checkout_rocksdb() {
    mkdir -p "${ROCKSDB_SRC_DIR}" || fail "unable to create rocksdb directory"
    (cd "${ROCKSDB_SRC_DIR}" && checkout "rocksdb" "$ROCKSDBREMOTE" "$ROCKSDBVERSION") || fail "rocksdb checkout failed"
}

# Read a dependency version straight out of rocksdb's Makefile (for example
# "ZLIB" -> "1.3.1"), so that we always compile the same versions upstream
# links into its own static release artifacts and never drift from them.
dep_version() {
    local name="$1"
    local version
    version="$(sed -n "s/^${name}_VER ?= *//p" "${ROCKSDB_SRC_DIR}/Makefile" | head -1)"
    test -n "$version" || fail "unable to read ${name}_VER from rocksdb's Makefile"
    echo "$version"
}

# Build zlib/bzip2/snappy/lz4/zstd as static PIC archives using rocksdb's own
# makefile targets. These are the exact same targets upstream uses to produce
# the self-contained artifacts published to Maven, so the resulting library
# depends on nothing but the system C/C++ runtime.
#
# Any extra compiler/linker flags (for example "-arch arm64" when cross
# compiling on macOS) must be exported as ARCHFLAG/EXTRA_* before calling this.
build_static_compression_libs() {
    local concurrency="$1"

    ZLIB_VER="$(dep_version ZLIB)"
    BZIP2_VER="$(dep_version BZIP2)"
    SNAPPY_VER="$(dep_version SNAPPY)"
    LZ4_VER="$(dep_version LZ4)"
    ZSTD_VER="$(dep_version ZSTD)"

    info "building static compression libraries (zlib ${ZLIB_VER}, bzip2 ${BZIP2_VER}, snappy ${SNAPPY_VER}, lz4 ${LZ4_VER}, zstd ${ZSTD_VER})"

    # Built one at a time: the individual targets unpack tarballs and shell out
    # to nested makes, which do not compose safely under a parallel outer make.
    (cd "${ROCKSDB_SRC_DIR}" && {
        make -j"${concurrency}" libz.a      || fail "zlib build failed"
        make -j"${concurrency}" libbz2.a    || fail "bzip2 build failed"
        make -j"${concurrency}" libsnappy.a || fail "snappy build failed"
        make -j"${concurrency}" liblz4.a    || fail "lz4 build failed"
        make -j"${concurrency}" libzstd.a   || fail "zstd build failed"
    }) || fail "compression library build failed"

    # Flags describing those archives to the rocksdb build. Mirrors upstream's
    # JAVA_STATIC_FLAGS / JAVA_STATIC_INCLUDES.
    COMPRESSION_CPPFLAGS="-DZLIB -DBZIP2 -DSNAPPY -DLZ4 -DZSTD"
    COMPRESSION_CPPFLAGS="${COMPRESSION_CPPFLAGS} -I${ROCKSDB_SRC_DIR}/zlib-${ZLIB_VER}"
    COMPRESSION_CPPFLAGS="${COMPRESSION_CPPFLAGS} -I${ROCKSDB_SRC_DIR}/bzip2-${BZIP2_VER}"
    COMPRESSION_CPPFLAGS="${COMPRESSION_CPPFLAGS} -I${ROCKSDB_SRC_DIR}/snappy-${SNAPPY_VER}"
    COMPRESSION_CPPFLAGS="${COMPRESSION_CPPFLAGS} -I${ROCKSDB_SRC_DIR}/snappy-${SNAPPY_VER}/build"
    COMPRESSION_CPPFLAGS="${COMPRESSION_CPPFLAGS} -I${ROCKSDB_SRC_DIR}/lz4-${LZ4_VER}/lib"
    COMPRESSION_CPPFLAGS="${COMPRESSION_CPPFLAGS} -I${ROCKSDB_SRC_DIR}/zstd-${ZSTD_VER}/lib"
    COMPRESSION_CPPFLAGS="${COMPRESSION_CPPFLAGS} -I${ROCKSDB_SRC_DIR}/zstd-${ZSTD_VER}/lib/dictBuilder"

    COMPRESSION_LDFLAGS="${ROCKSDB_SRC_DIR}/libsnappy.a ${ROCKSDB_SRC_DIR}/libz.a ${ROCKSDB_SRC_DIR}/libbz2.a ${ROCKSDB_SRC_DIR}/liblz4.a ${ROCKSDB_SRC_DIR}/libzstd.a"

    # The compression libraries are linked in statically above, so stop
    # build_detect_platform from also picking up the system shared ones.
    export ROCKSDB_DISABLE_ZLIB=1
    export ROCKSDB_DISABLE_BZIP=1
    export ROCKSDB_DISABLE_SNAPPY=1
    export ROCKSDB_DISABLE_LZ4=1
    export ROCKSDB_DISABLE_ZSTD=1
}

# Fail unless the given library exports the rocksdb C API and the compression
# entry points we just linked in. Any extra symbol names are checked too.
verify_library() {
    local lib="$1"
    shift

    test -f "$lib" || fail "expected library ${lib} was not produced"

    # The libraries are stripped by the time this runs, so only the dynamic
    # symbol table is left to look at. macOS's nm has no --defined-only.
    local symbols
    symbols="$(nm -D --defined-only "$lib" 2>/dev/null)" \
        || symbols="$(nm -gU "$lib" 2>/dev/null)" \
        || fail "unable to list symbols of ${lib}"

    local symbol
    for symbol in rocksdb_open rocksdb_options_set_compression "$@"; do
        # macOS prefixes exported C symbols with an underscore.
        echo "$symbols" | grep -q -e "[[:space:]]_\{0,1\}${symbol}\$" \
            || fail "${lib} does not export ${symbol}"
    done

    info "verified $(basename "$lib") ($(du -h "$lib" | cut -f1)): $* ok"
}

# Copy a built library into build-native/runtimes/<rid>/native/<name>.
publish_library() {
    local rid="$1"
    local source="$2"
    local name="$3"

    mkdir -p "${RUNTIMES_DIR}/${rid}/native" || fail "unable to create ${RUNTIMES_DIR}/${rid}/native"
    cp -vL "$source" "${RUNTIMES_DIR}/${rid}/native/${name}" || fail "unable to publish ${name} for ${rid}"
}
