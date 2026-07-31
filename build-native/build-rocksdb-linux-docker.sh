#!/usr/bin/env bash
#
# Runs build-rocksdb-linux.sh inside a container, for the Linux flavours that
# cannot be produced directly on a glibc x64 agent.
#
# Usage: ./build-rocksdb-linux-docker.sh --arch x64|arm64 --libc glibc|musl
#
#   x64   / glibc  debian, native speed
#   x64   / musl   alpine, native speed
#   arm64 / glibc  debian + the aarch64-linux-gnu cross toolchain, native speed
#   arm64 / musl   arm64 alpine through qemu-user-static; this one is emulated
#                  and takes several times as long as the others
#
# Building in a container rather than on the agent directly pins the glibc and
# musl versions the artifacts are built against, which is what decides how old a
# distribution they still load on.
#
# The build tree and the resulting build-native/runtimes tree are shared with
# the host through a bind mount, so the output lands in the same place as a
# direct run of build-rocksdb-linux.sh.

set -u

BASEDIR="$(cd "$(dirname "$0")" && pwd)"
REPODIR="$(cd "${BASEDIR}/.." && pwd)"

TARGET_ARCH=""
TARGET_LIBC=""

while [ $# -gt 0 ]; do
    case "$1" in
        --arch) TARGET_ARCH="${2:-}"; shift 2 ;;
        --libc) TARGET_LIBC="${2:-}"; shift 2 ;;
        -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
        *) >&2 echo "unknown argument: $1"; exit 1 ;;
    esac
done

test -n "$TARGET_ARCH" || { >&2 echo "--arch is required"; exit 1; }
test -n "$TARGET_LIBC" || { >&2 echo "--libc is required"; exit 1; }

command -v docker > /dev/null 2>&1 || { >&2 echo "docker is required"; exit 1; }

PLATFORM="linux/amd64"
SETUP=""
EXTRA_ARGS=""

case "${TARGET_ARCH}/${TARGET_LIBC}" in
    x64/glibc)
        # bullseye keeps the glibc floor of the artifact at 2.31.
        IMAGE="debian:bullseye"
        SETUP="apt-get update && apt-get install -y --no-install-recommends \
                   build-essential cmake git curl ca-certificates perl \
                   libjemalloc-dev"
        ;;
    x64/musl)
        IMAGE="alpine:3.21"
        SETUP="apk add --no-cache bash make cmake g++ git curl perl tar coreutils findutils linux-headers"
        ;;
    arm64/glibc)
        # Cross compiled rather than emulated. bullseye keeps the glibc floor of
        # the artifact at 2.31.
        IMAGE="debian:bullseye"
        SETUP="apt-get update && apt-get install -y --no-install-recommends \
                   build-essential cmake git curl ca-certificates perl \
                   g++-aarch64-linux-gnu"
        # No aarch64 jemalloc to link against here, and the package only ships
        # a jemalloc flavour for linux-x64 anyway.
        EXTRA_ARGS="--no-jemalloc"
        ;;
    arm64/musl)
        # No musl cross toolchain is packaged for Alpine, so this one runs
        # emulated. Register the qemu handlers on the host first:
        #   docker run --rm --privileged tonistiigi/binfmt --install arm64
        PLATFORM="linux/arm64"
        IMAGE="alpine:3.21"
        SETUP="apk add --no-cache bash make cmake g++ git curl perl tar coreutils findutils linux-headers"
        ;;
    *)
        >&2 echo "no container defined for ${TARGET_ARCH}/${TARGET_LIBC}; run build-rocksdb-linux.sh directly"
        exit 1
        ;;
esac

echo "building ${TARGET_ARCH}/${TARGET_LIBC} in ${IMAGE} (${PLATFORM})"

# --libc is passed explicitly because for the cross compiled arm64/glibc case
# the container's own ldd says nothing about the target.
docker run --rm \
    --platform "${PLATFORM}" \
    -v "${REPODIR}:/src" \
    -w /src/build-native \
    -e CONCURRENCY="${CONCURRENCY:-}" \
    "${IMAGE}" \
    sh -c "set -e
           ${SETUP}
           # The bind mounted tree belongs to the host user, not to root inside
           # the container, which git otherwise refuses to touch.
           git config --global --add safe.directory '*'
           ./build-rocksdb-linux.sh --arch ${TARGET_ARCH} --libc ${TARGET_LIBC} ${EXTRA_ARGS}"
