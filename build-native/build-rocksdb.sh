#!/bin/bash
# WINDOWS:
#   If you are in Windows, this is designed to be run from git bash
#     You therefore should install git bash, Visual Studio 2017, and cmake
#     Your best bet in Windows is to open a Developer Command Prompt and then run bash from there.
# MAC:
#   You will need snappy (must build: homebrew version is not universal)
#     brew install automake
#     brew install libtool
#     git clone git@github.com:google/snappy.git
#     cd snappy
#     ./autogen.sh
#     ./configure --prefix=/usr/local CFLAGS="-arch i386 -arch x86_64" CXXFLAGS="-arch i386 -arch x86_64" LDFLAGS="-arch i386 -arch x86_64" --disable-dependency-tracking
#     make
#     sudo make install
#
# Instructions for upgrading rocksdb version
# 1. Fetch the desired version locally with something like:
#    git fetch https://github.com/facebook/rocksdb.git v4.13
#    git checkout FETCH_HEAD
#    git push -f warrenfalk HEAD:rocksdb_sharp
# 2. Get the hash of the commit for the version and replace below
# 3. Also see instructions for modifying Native.Raw.cs with updates to c.h since current revisions
# 4. Push the desired version to the rocksdb_sharp branch at https://github.com/warrenfalk/rocksdb
# 5. Search through code for old hash and old version number and replace
# 6. Run this script to build (see README.md for more info)

ROCKSDBVNUM=`cat ../rocksdbversion`
ROCKSDBVERSION=v${ROCKSDBVNUM}
SNAPPYVERSION=1.1.7

ROCKSDBREMOTE=https://github.com/facebook/rocksdb
SNAPPYREMOTE=https://github.com/google/snappy

CONCURRENCY=8

fail() {
    >&2 echo -e "\033[1;31m$1\033[0m"
    exit 1
}

warn() {
    >&2 echo -e "\033[1;33m$1\033[0m"
}

run_rocksdb_test() {
    NAME=$1
    echo "Running test \"${NAME}\":"
    cmd //c "build\\Debug\\${NAME}.exe" || fail "Test failed"
}

checkout() {
    NAME="$1"
    REMOTE="$2"
    VERSION="$3"
    FETCHREF="$4"
    test -d .git || git init
    test -d .git || fail "unable to initialize $NAME repository"
    echo git fetch "$REMOTE" "${FETCHREF}"
    git fetch "$REMOTE" "${FETCHREF}" || fail "Unable to fetch latest ${FETCHREF} from {$REMOTE}"
    git checkout FETCH_HEAD || fail "Unable to checkout $NAME version ${VERSION}"
}

update_vcxproj(){
    echo "Patching vcxproj for static vc runtime"
    /bin/find . -type f -name '*.vcxproj' -exec sed -i 's/MultiThreadedDLL/MultiThreaded/g; s/MultiThreadedDebugDLL/MultiThreadedDebug/g' '{}' ';'
}

BASEDIR=$(dirname "$0")
OSINFO=$(uname)

if [[ $OSINFO == *"MSYS"* || $OSINFO == *"MINGW"* ]]; then
    echo "Detected Windows (MSYS)..."
    # Make sure cmake is installed
    hash cmake 2> /dev/null || { fail "CMake is not installed (https://cmake.org/download/)"; }

    # Make sure git is installed
    hash git 2> /dev/null || { fail "Build requires git"; }
    test -z "$WindowsSdkDir" && fail "This must be run from a build environment such as the Developer Command Prompt"

    BASEDIRWIN=$(cd "${BASEDIR}" && pwd -W)

    mkdir -p snappy || fail "unable to create snappy directory"
    (cd snappy && {
        checkout "snappy" "$SNAPPYREMOTE" "$SNAPPYVERSION" "$SNAPPYVERSION"
        mkdir -p build
        (cd build && {
            cmake -G "Visual Studio 16 2019" -DSNAPPY_BUILD_TESTS=0 .. || fail "Running cmake on snappy failed"
            update_vcxproj || warn "unable to patch snappy for static vc runtime"
        }) || fail "cmake build generation failed"

        test -z "$RUNTESTS" || {
            cmd //c "msbuild build/snappy.sln /p:Configuration=Debug /m:$CONCURRENCY" || fail "Build of snappy (debug config) failed"
        }
        cmd //c "msbuild build/snappy.sln /p:Configuration=Release /m:$CONCURRENCY" || fail "Build of snappy failed"
    }) || fail "Snappy build failed"

    mkdir -p vcpkg || fail "unable to make vcpkg directory"
    (cd vcpkg && {
        checkout "vcpkg" "https://github.com/Microsoft/vcpkg" "master" "master"
        ./bootstrap-vcpkg.sh
        ./vcpkg.exe install zlib:x64-windows-static snappy:x64-windows-static lz4:x64-windows-static zstd:x64-windows-static || fail "unable to install libraries with vcpkg.exe"
    })

    mkdir -p rocksdb || fail "unable to create rocksdb directory"
    (cd rocksdb && {
        checkout "rocksdb" "$ROCKSDBREMOTE" "$ROCKSDBVERSION" "$ROCKSDBVERSION"

        mkdir -p build
        VCPKG_HOME="$(realpath ../vcpkg/installed/x64-windows-static)"
        VCPKG_INCLUDE="${VCPKG_HOME}/include"
        VCPKG_LIB_DEBUG="${VCPKG_HOME}/debug"
        VCPKG_LIB_RELEASE="${VCPKG_HOME}/lib"
        export ZLIB_INCLUDE="${VCPKG_INCLUDE}"
        export ZLIB_LIB_DEBUG="${VCPKG_LIB_DEBUG}/zlib.lib"
        export ZLIB_LIB_RELEASE="${VCPKG_LIB_RELEASE}/zlib.lib"
        export SNAPPY_INCLUDE="${VCPKG_INCLUDE}"
        export SNAPPY_LIB_DEBUG="${VCPKG_LIB_DEBUG}/snappy.lib"
        export SNAPPY_LIB_RELEASE="${VCPKG_LIB_RELEASE}/snappy.lib"
        export LZ4_INCLUDE="${VCPKG_INCLUDE}"
        export LZ4_LIB_DEBUG="${VCPKG_LIB_DEBUG}/lz4.lib"
        export LZ4_LIB_RELEASE="${VCPKG_LIB_RELEASE}/lz4.lib"
        export ZSTD_INCLUDE="${VCPKG_INCLUDE}"
        export ZSTD_LIB_DEBUG="${VCPKG_LIB_DEBUG}/zstd_static.lib"
        export ZSTD_LIB_RELEASE="${VCPKG_LIB_RELEASE}/zstd_static.lib"
        (cd build && {
            cmake -G "Visual Studio 16 2019" -WITH_TESTS=OFF -DWITH_MD_LIBRARY=OFF -DOPTDBG=1 -DGFLAGS=0 -DSNAPPY=1 -DWITH_ZLIB=1 -DWITH_LZ4=1 -DWITH_ZSTD=1 -DPORTABLE=1 -DWITH_TOOLS=0 .. || fail "Running cmake failed"
            update_vcxproj || warn "failed to patch vcxproj files for static vc runtime"
        }) || fail "cmake build generation failed"

        cmd //c "msbuild build/rocksdb.sln /p:Configuration=Release /m:$CONCURRENCY" || fail "Rocksdb release build failed"

        mkdir -p ../runtimes/win-x64/native && cp -v ./build/Release/rocksdb-shared.dll ../runtimes/win-x64/native/rocksdb.dll
        mkdir -p ../rocksdb-${ROCKSDBVERSION}/win-x64/native && cp -v ./build/Release/rocksdb-shared.dll ../rocksdb-${ROCKSDBVERSION}/win-x64/native/rocksdb.dll
    }) || fail "rocksdb build failed"
else
    echo "Assuming a posix-like environment"
    if [ "$(uname)" == "Darwin" ]; then
        echo "Mac (Darwin) detected"
        export CC=gcc-8
        export CXX=g++-8
        CFLAGS="-I/usr/local/include -I/Library/Developer/CommandLineTools/SDKs/MacOSX.sdk/usr/include"
        LDFLAGS="-L/usr/local/lib"
        LIBEXT=.dylib
        RUNTIME=osx-x64
        
        xcode-select --install
        brew install gcc
        brew install gcc48
        brew install llvm
        brew install snappy
        brew install zstd
        brew install lz4
        brew install zlib
        brew install bzip2
        brew install gflags
    else
        echo "Linux detected"
        CFLAGS=-static-libstdc++
        LIBEXT=.so
        RUNTIME=linux-x64
        # Linux Dependencies    
        sudo apt-get install libsnappy-dev libbz2-dev libz-dev liblz4-dev libzstd-dev
    fi
    
    

    # Mac Dependencies
    ## (Note: gcc 8.2 worked below)
    # xcode-select --install
    # brew install gcc
    # brew install snappy
    # brew install zstd
    # Don't have universal version of lz4 through brew, have to build manually
    # git clone git@github.com:Cyan4973/lz4.git
    # make -C lz4/lib
    # cp -L lz4/lib/liblz4.dylib ./liblz4_64.dylib
    # make -C lz4/lib clean
    # CFLAGS="-arch i386" CXXFLAGS="-arch i386" LDFLAGS="-arch i386" make -C lz4/lib
    # cp -L lz4/lib/liblz4.dylib ./liblz4_32.dylib
    # lipo -create ./liblz4_32.dylib ./liblz4_64.dylib -output ./liblz4.dylib
    # cp -v ./liblz4.dylib lz4/lib/$(readlink lz4/lib/liblz4.dylib)
    # touch lz4/lib/liblz4
    # make -C lz4/lib install


    mkdir -p rocksdb || fail "unable to create rocksdb directory"
    (cd rocksdb && {
        checkout "rocksdb" "$ROCKSDBREMOTE" "$ROCKSDBVERSION" "$ROCKSDBVERSION"

        export CFLAGS
        export LDFLAGS
        export ROCKSDB_DISABLE_GFLAGS=1
        (. ./build_tools/build_detect_platform detected~; {
            grep detected~ -e '-DZLIB' &> /dev/null || fail "failed to detect zlib, install libzlib-dev"
            grep detected~ -e '-DSNAPPY' &> /dev/null || fail "failed to detect snappy, install libsnappy-dev"
            grep detected~ -e '-DLZ4' &> /dev/null || fail "failed to detect lz4, install liblz4-dev"
            grep detected~ -e '-DZSTD' &> /dev/null || fail "failed to detect zstd, install libzstd-dev"
            grep detected~ -e '-DGFLAGS' &> /dev/null && fail "gflags detected, see https://github.com/facebook/rocksdb/issues/2310" || true
        }) || fail "dependency detection failed"

        echo "----- Build 64 bit --------------------------------------------------"
        make clean
        CFLAGS="${CFLAGS}" PORTABLE=1 make -j$CONCURRENCY shared_lib || fail "64-bit build failed"
        strip librocksdb${LIBEXT}
        mkdir -p ../runtimes/${RUNTIME}/native && cp -vL ./librocksdb${LIBEXT} ../runtimes/${RUNTIME}/native/librocksdb${LIBEXT}
        mkdir -p ../rocksdb-${ROCKSDBVERSION}/${RUNTIME}/native && cp -vL ./librocksdb${LIBEXT} ../rocksdb-${ROCKSDBVERSION}/${RUNTIME}/native/librocksdb${LIBEXT}

        # This no longer seems to work on a mac, so I'm removing support for it
        # If someone wants to try to fix this, then I'm happy to take a PR
        # 32-bit linux dependencies:
        # sudo apt-get install gcc-5-multilib g++-5-multilib
        # sudo apt-get install libsnappy-dev:i386 libbz2-dev:i386 libz-dev:i386
        #echo "----- Build 32 bit --------------------------------------------------"
        #make clean
        #CFLAGS="${CFLAGS} -m32" PORTABLE=1 make -j$CONCURRENCY shared_lib || fail "32-bit build failed"
        #strip librocksdb${LIBEXT}
        #mkdir -p ../native/i386 && cp -vL ./librocksdb${LIBEXT} ../native/i386/librocksdb${LIBEXT}
        #mkdir -p ../native-${ROCKSDBVERSION}/i386 && cp -vL ./librocksdb${LIBEXT} ../native-${ROCKSDBVERSION}/i386/librocksdb${LIBEXT}


    }) || fail "rocksdb build failed"
fi



