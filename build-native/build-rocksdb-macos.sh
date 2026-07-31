#!/usr/bin/env bash
#
# Builds the rocksdb shared library for macOS.
#
# Usage: ./build-rocksdb-macos.sh [--arch arm64|x64|all]
#
# Outputs:
#   build-native/runtimes/osx-arm64/native/librocksdb.dylib
#   build-native/runtimes/osx-x64/native/librocksdb.dylib
#
# Both slices are cross compiled from a single machine: Xcode's toolchain can
# target either architecture with -arch, and zlib, bzip2, snappy, lz4 and zstd
# are compiled from source for the architecture being built and linked in
# statically. The published dylib therefore has no Homebrew dependencies and
# needs no install_name_tool fixups afterwards.
#
# arm64 is built first so it can be validated before the x64 slice is started.

set -u

. "$(cd "$(dirname "$0")" && pwd)/common.sh"

TARGET_ARCH="all"

while [ $# -gt 0 ]; do
    case "$1" in
        --arch) TARGET_ARCH="${2:-}"; shift 2 ;;
        -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
        *) fail "unknown argument: $1" ;;
    esac
done

test "$(uname)" = "Darwin" || fail "this script must be run on macOS"

case "$TARGET_ARCH" in
    arm64) ARCHES="arm64" ;;
    x64)   ARCHES="x86_64" ;;
    # arm64 first: it is the architecture every current Mac runs natively, so a
    # failure there should stop the build before the x64 slice is attempted.
    all)   ARCHES="arm64 x86_64" ;;
    *) fail "unsupported architecture ${TARGET_ARCH}, expected arm64, x64 or all" ;;
esac

CONCURRENCY="$(detect_concurrency)"
HOST_ARCH="$(uname -m)"

command -v clang++ > /dev/null 2>&1 || fail "Build requires the Xcode command line tools"
command -v lipo > /dev/null 2>&1 || fail "Build requires lipo (Xcode command line tools)"

info "building rocksdb ${ROCKSDBVERSION} for [${ARCHES}] on ${HOST_ARCH} with ${CONCURRENCY} jobs"

checkout_rocksdb

# PORTABLE=1 keeps -march=native (which would pin the artifact to the build
# machine's CPU, and is meaningless when cross compiling) out of the build.
export PORTABLE=1

# rocksdb only ships a jemalloc flavour of the Linux libraries, and linking
# against the build machine's Homebrew jemalloc would both break cross
# compilation and add a runtime dependency users do not have.
export ROCKSDB_DISABLE_JEMALLOC=1

# rocksdb builds with -Werror by default. Which warnings clang emits depends on
# the Xcode version on the agent, so a new Xcode should not be able to break the
# build over a warning in third party code.
export DISABLE_WARNING_AS_ERROR=1

build_arch() {
    local arch="$1"
    local rid archflag

    case "$arch" in
        arm64)  rid="osx-arm64" ;;
        x86_64) rid="osx-x64" ;;
        *) fail "unsupported architecture ${arch}" ;;
    esac

    archflag="-arch ${arch}"

    info "=========== ${rid} (${archflag}) ==========="

    if [ "$arch" != "$HOST_ARCH" ]; then
        info "cross compiling ${arch} from ${HOST_ARCH}"
    fi

    # Start from a clean tree: object files and the compression archives from a
    # previous architecture are not reusable. clean-rocks deletes every *.a in
    # the tree, which is what forces the compression libraries to be rebuilt for
    # this architecture; the downloaded tarballs are kept.
    (cd "${ROCKSDB_SRC_DIR}" && make clean-rocks > /dev/null 2>&1) || true

    # build_detect_platform folds $CFLAGS into the platform flags used for both
    # C and C++, which is how -arch reaches its own compile probes as well as
    # the rocksdb sources.
    export CFLAGS="${archflag}"
    export ARCHFLAG="${archflag}"

    build_static_compression_libs "$CONCURRENCY"

    local deps_dir="${BUILD_NATIVE_DIR}/deps/${rid}"
    rm -rf "$deps_dir" && mkdir -p "$deps_dir" || fail "unable to create ${deps_dir}"

    local static_deps="" archive
    for archive in $COMPRESSION_LDFLAGS; do
        cp -f "$archive" "$deps_dir/" || fail "unable to stage $archive"
        static_deps="${static_deps} ${deps_dir}/$(basename "$archive")"
    done

    info "building librocksdb.dylib for ${arch}"

    (cd "${ROCKSDB_SRC_DIR}" && {
        rm -f librocksdb*.dylib

        make -j"${CONCURRENCY}" shared_lib \
            ARCHFLAG="${archflag}" \
            EXTRA_CXXFLAGS="${archflag} ${COMPRESSION_CPPFLAGS}" \
            EXTRA_CFLAGS="${archflag} ${COMPRESSION_CPPFLAGS}" \
            EXTRA_LDFLAGS="${archflag} ${static_deps}" \
            || fail "${rid} build failed"

        # make leaves librocksdb.dylib as a symlink to the versioned file.
        # Replace it with the real thing before rewriting it below, so strip,
        # install_name_tool and codesign all act on a plain file.
        cp -L librocksdb.dylib librocksdb.dylib.tmp \
            && mv -f librocksdb.dylib.tmp librocksdb.dylib \
            || fail "unable to materialise librocksdb.dylib"

        # -S -x drops debug and local symbols but keeps the exported C API.
        strip -S -x librocksdb.dylib || warn "unable to strip librocksdb.dylib"

        # rocksdb names the library after its soname, which makes the dylib
        # resolvable only under librocksdb.<major>.<minor>.dylib. RocksDbSharp
        # loads it by path as librocksdb.dylib.
        install_name_tool -id "@rpath/librocksdb.dylib" librocksdb.dylib \
            || warn "unable to set install name"

        # strip and install_name_tool invalidate the ad-hoc signature the linker
        # applied, and an arm64 dylib with a broken signature will not load.
        # Recent toolchains re-sign automatically; do it explicitly so the build
        # does not depend on that.
        codesign --force --sign - librocksdb.dylib || warn "unable to re-sign librocksdb.dylib"
    }) || fail "${rid} build failed"

    # --- checks -----------------------------------------------------------

    local lib="${ROCKSDB_SRC_DIR}/librocksdb.dylib"

    grep -q "${arch}\$" <<< "$(lipo -info "$lib")" \
        || fail "librocksdb.dylib is not a ${arch} binary: $(lipo -info "$lib")"

    verify_library "$lib" $COMPRESSION_SYMBOLS

    # Everything but the system libraries has to be linked in statically; a
    # /opt/homebrew or /usr/local entry here means the artifact would only load
    # on a machine with the same Homebrew packages installed.
    local foreign
    foreign="$(otool -L "$lib" | tail -n +2 | awk '{print $1}' \
        | grep -v -e '^/usr/lib/' -e '^/System/Library/' -e '@rpath/librocksdb.dylib' || true)"
    test -z "$foreign" || fail "librocksdb.dylib links against non-system libraries:
${foreign}"

    publish_library "$rid" "$lib" "librocksdb.dylib"

    info "${rid} done:"
    otool -L "${RUNTIMES_DIR}/${rid}/native/librocksdb.dylib" | sed 's/^/    /'
}

for arch in $ARCHES; do
    build_arch "$arch"
done

info "done"
