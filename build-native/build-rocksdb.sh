#!/usr/bin/env bash
#
# Dispatches to the build script for the current operating system. All
# arguments are forwarded, so for example:
#
#     ./build-rocksdb.sh --arch arm64
#
# runs ./build-rocksdb-macos.sh --arch arm64 on a Mac.

set -u

BASEDIR="$(cd "$(dirname "$0")" && pwd)"

case "$(uname)" in
    Darwin)      script=build-rocksdb-macos.sh ;;
    Linux)       script=build-rocksdb-linux.sh ;;
    MSYS*|MINGW*|CYGWIN*) script=build-rocksdb-windows.sh ;;
    *)
        >&2 echo -e "\033[1;31munsupported operating system: $(uname)\033[0m"
        exit 1
        ;;
esac

exec "${BASEDIR}/${script}" "$@"
