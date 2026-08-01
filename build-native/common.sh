# Shared helpers for the rocksdb native build scripts.
#
# This file is meant to be sourced, not executed:
#
#     . "$(dirname "$0")/common.sh"
#
# It provides logging helpers, the rocksdb version/remote to build, a source
# checkout helper, the routine that builds the compression libraries as static
# PIC archives so that the resulting rocksdb library is self contained, and the
# checks each build script runs over what it produced.

# shellcheck shell=bash

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

# printf rather than `echo -e`, which would also expand any backslash escape
# that happens to be in the message. Windows paths are full of them: a message
# mentioning C:\vcpkg came out as "C:" followed by a vertical tab.
fail() {
    >&2 printf '\033[1;31m%s\033[0m\n' "$1"
    exit 1
}

warn() {
    >&2 printf '\033[1;33m%s\033[0m\n' "$1"
}

info() {
    printf '\033[1;34m==> %s\033[0m\n' "$1"
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

# Fail unless the given C++ compiler can build the language and library features
# rocksdb relies on.
#
# rocksdb has been a C++20 codebase since version 10: db.h reaches for `using
# enum` and block_based_table_builder.cc includes <semaphore>, so anything older
# than gcc 11 or clang 13 stops a few hundred files into the build with pages of
# errors that say nothing about the actual problem. Upstream states the same
# requirement in INSTALL.md ("GCC >= 11, Clang >= 10").
require_cxx20() {
    local cxx="$1"

    if ! "$cxx" -std=c++20 -x c++ -fsyntax-only - > /dev/null 2>&1 <<'EOF'
#include <semaphore>
enum class Flags { None };
int probe() {
    using enum Flags;
    std::counting_semaphore<1> gate{1};
    gate.acquire();
    return static_cast<int>(None);
}
EOF
    then
        fail "${cxx} cannot compile rocksdb ${ROCKSDBVERSION}, which is C++20 and needs at least gcc 11 or clang 13:
    $("$cxx" --version 2>&1 | head -1)"
    fi

    info "$("$cxx" --version 2>&1 | head -1) builds C++20"
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
    # Keep the downloaded dependency sources, they are expensive to fetch again.
    git clean -xdf -e '*.tar.gz' -e '*.a' \
        -e 'zlib-*' -e 'bzip2-*' -e 'snappy-*' -e 'lz4-*' -e 'zstd-*' > /dev/null
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

# Read a dependency checksum out of rocksdb's Makefile, alongside dep_version.
dep_sha256() {
    local name="$1"
    local sha
    sha="$(sed -n "s/^${name}_SHA256 ?= *//p" "${ROCKSDB_SRC_DIR}/Makefile" | head -1)"
    test -n "$sha" || fail "unable to read ${name}_SHA256 from rocksdb's Makefile"
    echo "$sha"
}

sha256_of() {
    if command -v sha256sum > /dev/null 2>&1; then
        sha256sum "$1" | cut -d' ' -f1
    elif command -v shasum > /dev/null 2>&1; then
        shasum -a 256 "$1" | cut -d' ' -f1
    else
        openssl dgst -sha256 "$1" | sed 's/.*= *//'
    fi
}

# Download $1 (a file name under the rocksdb source directory) from the first of
# the remaining arguments that works, then check it against $2. A file that is
# already present and matches is left alone.
fetch_dependency() {
    local name="$1"
    local expected="$2"
    shift 2

    local target="${ROCKSDB_SRC_DIR}/${name}"

    if [ -f "$target" ]; then
        if [ "$(sha256_of "$target")" = "$expected" ]; then
            return 0
        fi
        warn "${name} does not match its expected checksum, downloading it again"
        rm -f "$target"
    fi

    local url
    for url in "$@"; do
        info "fetching ${name} from ${url}"
        if curl --fail --location --silent --show-error --output "${target}.part" "$url"; then
            local actual
            actual="$(sha256_of "${target}.part")"
            if [ "$actual" = "$expected" ]; then
                mv -f "${target}.part" "$target"
                return 0
            fi
            warn "${url} returned a file with checksum ${actual}, expected ${expected}"
        fi
        rm -f "${target}.part"
    done

    fail "unable to download ${name} from any known location"
}

# Fetch the compression library sources before rocksdb's own targets go looking
# for them; they skip the download when the tarball is already there.
#
# This exists because rocksdb hardcodes one URL per dependency and some of those
# do not keep old releases. zlib.net serves only the current release from its
# root and moves everything else to /fossils, so rocksdb's pinned URL starts
# returning 404 for everyone the day zlib publishes a new version, which is
# exactly what happened to zlib 1.3.1. Trying several locations also means a
# single mirror being down no longer breaks the build.
#
# rocksdb verifies these checksums when it does the downloading itself, so we
# have to verify them here, or pre-placing the files would quietly drop that
# check.
prefetch_dependency_sources() {
    fetch_dependency "zlib-${ZLIB_VER}.tar.gz" "$(dep_sha256 ZLIB)" \
        "https://zlib.net/fossils/zlib-${ZLIB_VER}.tar.gz" \
        "https://github.com/madler/zlib/releases/download/v${ZLIB_VER}/zlib-${ZLIB_VER}.tar.gz" \
        "https://zlib.net/zlib-${ZLIB_VER}.tar.gz"

    fetch_dependency "bzip2-${BZIP2_VER}.tar.gz" "$(dep_sha256 BZIP2)" \
        "https://sourceware.org/pub/bzip2/bzip2-${BZIP2_VER}.tar.gz"

    fetch_dependency "snappy-${SNAPPY_VER}.tar.gz" "$(dep_sha256 SNAPPY)" \
        "https://github.com/google/snappy/archive/${SNAPPY_VER}.tar.gz"

    fetch_dependency "lz4-${LZ4_VER}.tar.gz" "$(dep_sha256 LZ4)" \
        "https://github.com/lz4/lz4/archive/v${LZ4_VER}.tar.gz"

    fetch_dependency "zstd-${ZSTD_VER}.tar.gz" "$(dep_sha256 ZSTD)" \
        "https://github.com/facebook/zstd/archive/v${ZSTD_VER}.tar.gz"
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

    prefetch_dependency_sources

    # bzip2's own makefile assigns CC=gcc, and a makefile assignment beats the
    # environment, so exporting CC is not enough to cross compile it: a build for
    # arm64 quietly filled libbz2.a with host objects and only fell over much
    # later, when the linker refused the archive. Naming CC on make's command
    # line instead makes it an override, which the nested makes inherit through
    # MAKEFLAGS and which does win over their own assignments.
    #
    # Left empty where CC is unset, which is every native build plus macOS, where
    # the architecture travels in ARCHFLAG/CFLAGS instead.
    local toolchain=""
    test -z "${CC:-}" || toolchain="CC=${CC}"

    # Built one at a time: the individual targets unpack tarballs and shell out
    # to nested makes, which do not compose safely under a parallel outer make.
    #
    # DEBUG_LEVEL=0 only quiets rocksdb's "Compiling in debug mode" warning,
    # which it prints for any goal other than shared_lib and which has nothing to
    # do with how these libraries get compiled -- each recipe below passes its
    # own -O2 CFLAGS. Without it the warning shows up in the log of every build
    # and reads like the artifact is a debug build, which it is not.
    (cd "${ROCKSDB_SRC_DIR}" && {
        make -j"${concurrency}" DEBUG_LEVEL=0 $toolchain libz.a      || fail "zlib build failed"
        make -j"${concurrency}" DEBUG_LEVEL=0 $toolchain libbz2.a    || fail "bzip2 build failed"
        make -j"${concurrency}" DEBUG_LEVEL=0 $toolchain libsnappy.a || fail "snappy build failed"
        make -j"${concurrency}" DEBUG_LEVEL=0 $toolchain liblz4.a    || fail "lz4 build failed"
        make -j"${concurrency}" DEBUG_LEVEL=0 $toolchain libzstd.a   || fail "zstd build failed"
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

    # One entry point per codec, to check afterwards that each archive really
    # made it into the library. rocksdb uses snappy through its C++ API, so that
    # one is matched through its mangled name.
    COMPRESSION_SYMBOLS="zlibVersion BZ2_bzCompress snappy11RawCompress LZ4_compress_default ZSTD_compress"

    # The compression libraries are linked in statically above, so stop
    # build_detect_platform from also picking up the system shared ones.
    export ROCKSDB_DISABLE_ZLIB=1
    export ROCKSDB_DISABLE_BZIP=1
    export ROCKSDB_DISABLE_SNAPPY=1
    export ROCKSDB_DISABLE_LZ4=1
    export ROCKSDB_DISABLE_ZSTD=1
}

# Fail unless the given library exports the rocksdb C API plus everything else
# named. Names are matched as regular expressions against the exported symbols,
# so C++ entry points can be named through their mangled form.
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

    local pattern
    for pattern in rocksdb_open rocksdb_options_set_compression "$@"; do
        # Matched from a here-string rather than a pipe: grep -q stops at the
        # first hit, and the resulting SIGPIPE on the writing end of a pipeline
        # is indistinguishable from a real failure.
        grep -qE "$pattern" <<< "$symbols" \
            || fail "${lib} does not export ${pattern}"
    done

    # -L because make leaves the plain name as a symlink to the versioned file.
    info "verified $(basename "$lib") ($(du -hL "$lib" | cut -f1))"
}

# Fail if the library links against anything outside the given list of expected
# runtime dependencies. Everything else is supposed to be linked in statically.
verify_dependencies() {
    local lib="$1"
    local readelf="$2"
    shift 2

    local needed foreign="" entry
    needed="$("$readelf" -d "$lib" | sed -n 's/.*NEEDED.*\[\(.*\)\]/\1/p')"

    for entry in $needed; do
        case " $* " in
            *" $entry "*) ;;
            *) foreign="${foreign} ${entry}" ;;
        esac
    done

    test -z "$foreign" || fail "${lib} depends on${foreign}, which will not be present on every machine"

    info "$(basename "$lib") only needs $(echo $needed)"
}

# The architectures of the ELF objects in a file, one per line. An archive holds
# a header per member, so anything but a single line means a mixed archive.
elf_machines() {
    readelf -h "$1" 2>/dev/null | sed -n 's/^ *Machine: *//p' | sort -u
}

# Fail unless every archive named holds objects for the architecture the given
# compiler builds for. ELF only, so Linux; macOS checks the finished dylib with
# lipo instead.
#
# A dependency that ignores the cross compiler and builds for the host produces
# an archive that is perfectly valid and simply wrong, and nothing notices until
# the link at the very end of the build reports "file in wrong format" without
# naming an architecture. Comparing here costs a compile of one line.
verify_archives_match_compiler() {
    local cxx="$1"
    shift

    local probe expected archive machines
    probe="$(mktemp -d)" || fail "unable to create a temporary directory"

    echo 'int rocksdb_sharp_architecture_probe;' \
        | "$cxx" -x c++ -c - -o "${probe}/probe.o" \
        || fail "unable to compile an architecture probe with ${cxx}"

    expected="$(elf_machines "${probe}/probe.o")"
    rm -rf "$probe"

    for archive in "$@"; do
        machines="$(elf_machines "$archive")"
        test "$machines" = "$expected" \
            || fail "$(basename "$archive") holds ${machines:-unreadable} objects, but ${cxx} builds for ${expected}"
    done

    info "the static archives are ${expected}"
}

# True when the first version number is newer than the second.
newer_version() {
    test "$1" != "$2" && test "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -1)" = "$2"
}

# Fail if the library would need a newer glibc than the given floor.
#
# Which glibc an artifact requires is decided by the highest symbol version it
# ended up referencing, not by the glibc it happened to be built against: glibc
# only stamps a new version onto a symbol when that symbol's behaviour changes,
# so the great majority of any build still resolves against far older releases.
# The build image can therefore be much newer than the oldest distribution the
# result loads on -- but only as long as somebody checks, which is what this is
# for.
verify_glibc_floor() {
    local lib="$1"
    local readelf="$2"
    local floor="$3"

    # Every glibc symbol carries its version as "name@GLIBC_x.y", or
    # "name@@GLIBC_x.y" where it is the default version of a defined one.
    local tagged
    tagged="$("$readelf" --dyn-syms --wide "$lib" \
        | awk 'index($8, "@GLIBC_") { print $8 }' | sed 's/@@/@/' | sort -u)"

    test -n "$tagged" || fail "${lib} references no versioned glibc symbol at all, which cannot be right"

    local sym version highest="0" offenders=""

    while IFS= read -r sym; do
        version="${sym##*@GLIBC_}"
        newer_version "$version" "$highest" && highest="$version"
        newer_version "$version" "$floor" && offenders="${offenders}    ${sym}"$'\n'
    done <<< "$tagged"

    test -z "$offenders" || fail "$(basename "$lib") needs glibc ${highest}, past the ${floor} floor this package promises:
${offenders%$'\n'}"

    info "$(basename "$lib") loads on glibc ${highest} and newer"
}

# Copy a built library into build-native/runtimes/<rid>/native/<name>.
publish_library() {
    local rid="$1"
    local source="$2"
    local name="$3"

    mkdir -p "${RUNTIMES_DIR}/${rid}/native" || fail "unable to create ${RUNTIMES_DIR}/${rid}/native"
    cp -vL "$source" "${RUNTIMES_DIR}/${rid}/native/${name}" || fail "unable to publish ${name} for ${rid}"
}
