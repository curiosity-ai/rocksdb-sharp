#!/usr/bin/env bash
#
# Checks that a built native library exports every symbol csharp/src/Native.cs
# declares, and reports the ones it does not.
#
# Usage: .../verify-exports.sh <path to librocksdb.so|.dylib>
#
# RocksDbSharp binds late: AutoNativeImport walks the abstract methods of
# Native at startup and resolves each one by name against the loaded library,
# so a binding the library does not export is not a compile error anywhere --
# it is a TypeInitializationException the first time anything touches RocksDb,
# on a user's machine. The generator will happily emit a declaration for a
# function upstream removed from the library but left in c.h, which is the one
# way a release bump can produce a package that never loads.
#
# Run it against the library the build just produced, before publishing.

set -u

LIB="${1:-}"
test -n "$LIB" || { >&2 echo "usage: $0 <library>"; exit 2; }
test -f "$LIB" || { >&2 echo "no such library: $LIB"; exit 2; }

REPO_ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
NATIVE_CS="${REPO_ROOT}/csharp/src/Native.cs"
test -f "$NATIVE_CS" || { >&2 echo "cannot find ${NATIVE_CS}"; exit 2; }

# The 52-byte placeholders committed under csharp/runtimes are not libraries;
# checking one would pass vacuously and prove nothing.
if [ "$(wc -c < "$LIB")" -lt 100000 ]; then
    >&2 echo "${LIB} is only $(wc -c < "$LIB") bytes -- that is the committed"
    >&2 echo "placeholder, not a built library. Point this at build-native's output."
    exit 2
fi

DECLARED="$(mktemp)"; EXPORTED="$(mktemp)"
trap 'rm -f "$DECLARED" "$EXPORTED"' EXIT

# Every abstract method on Native is resolved by name at load time; overloads
# share one native symbol, hence sort -u.
# The capture matters: "public abstract rocksdb_t_ptr rocksdb_open(" contains two
# rocksdb_ words, and taking every match would add return types such as
# rocksdb_t_ptr to the list and report them as missing exports.
sed -nE 's/^[[:space:]]*public abstract [^ ]+ (rocksdb_[A-Za-z0-9_]+)\(.*/\1/p' \
    "$NATIVE_CS" | sort -u > "$DECLARED"

case "$(uname)" in
    Darwin) nm -gU "$LIB" 2>/dev/null | awk '{print $NF}' | sed 's/^_//' ;;
    *)      nm -D --defined-only "$LIB" 2>/dev/null | awk '{print $NF}' ;;
esac | grep -E '^rocksdb_' | sort -u > "$EXPORTED"

test -s "$EXPORTED" || {
    >&2 echo "${LIB} exports no rocksdb_ symbol at all, which cannot be right"
    exit 1
}

MISSING="$(comm -23 "$DECLARED" "$EXPORTED")"

echo "declared in Native.cs : $(wc -l < "$DECLARED")"
echo "exported by library   : $(wc -l < "$EXPORTED")"

if [ -n "$MISSING" ]; then
    echo
    echo "MISSING -- declared but not exported ($(wc -l <<< "$MISSING")):"
    sed 's/^/    /' <<< "$MISSING"
    echo
    echo "Each of these throws at type-initialisation time on first use. Either the"
    echo "library was built from a different release than rocksdbversion pins, or"
    echo "upstream dropped the function from the library while leaving it in c.h --"
    echo "check its history before deciding which."
    exit 1
fi

echo
echo "OK: every declared binding is exported."
