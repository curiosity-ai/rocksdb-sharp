#!/bin/bash

ROCKSDBVNUM=`cat ../rocksdbversion`
ROCKSDBVERSION=v${ROCKSDBVNUM}

ROCKSDBREMOTE=https://github.com/facebook/rocksdb

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
    export SystemDrive="$SYSTEMDRIVE"
    export SystemRoot="$SYSTEMROOT"
    export windir="$WINDIR"
    
    # Make sure cmake is installed
    hash cmake 2> /dev/null || { fail "CMake is not installed (https://cmake.org/download/)"; }

    # Make sure git is installed
    hash git 2> /dev/null || { fail "Build requires git"; }
    test -z "$WindowsSdkDir" && fail "This must be run from a build environment such as the Developer Command Prompt"

    BASEDIRWIN=$(cd "${BASEDIR}" && pwd -W)
    
    export VCPKG_C_FLAGS=/arch:SSE2
    export VCPKG_CXX_FLAGS=/arch:SSE2
    export CMAKE_C_FLAGS=/arch:SSE2
    export CMAKE_CXX_FLAGS=/arch:SSE2
    
    mkdir -p vcpkg-curiosity || fail "unable to make vcpkg-curiosity directory"
    (cd vcpkg-curiosity && {
        checkout "vcpkg" "https://github.com/curiosity-ai/vcpkg-registry" "main" "main"
        ls
    })

    mkdir -p vcpkg || fail "unable to make vcpkg directory"
    (cd vcpkg && {
        checkout "vcpkg" "https://github.com/Microsoft/vcpkg" "master" "master"
        cmd //c "bootstrap-vcpkg.bat"  || fail "unable to build vcpkg.exe"
        # --overlay-ports=../vcpkg-curiosity/ports
        ./vcpkg.exe install zlib:x64-windows-static snappy:x64-windows-static lz4:x64-windows-static zstd:x64-windows-static || fail "unable to install libraries with vcpkg.exe"
        ls -R D:/a/1/s/build-native/vcpkg/packages/
    })

    mkdir -p rocksdb || fail "unable to create rocksdb directory"
    (cd rocksdb && {
        checkout "rocksdb" "$ROCKSDBREMOTE" "$ROCKSDBVERSION" "$ROCKSDBVERSION"

        mkdir -p build
        # VCPKG_HOME="$(realpath ../vcpkg/packages)"
        VCPKG_HOME="D:/a/1/s/build-native/vcpkg/packages/"
        ls -R ${VCPKG_HOME}
        
        export
        
        export ZLIB_INCLUDE="${VCPKG_HOME}/zlib_x64-windows-static/include"
        export ZLIB_LIB_DEBUG="${VCPKG_HOME}/zlib_x64-windows-static/debug/lib/zlib.lib"
        export ZLIB_LIB_RELEASE="${VCPKG_HOME}/zlib_x64-windows-static/lib/zlib.lib"
        
        export LZ4_INCLUDE="${VCPKG_HOME}/lz4_x64-windows-static/include"
        export LZ4_LIB_DEBUG="${VCPKG_HOME}/lz4_x64-windows-static/debug/lib/lz4.lib"
        export LZ4_LIB_RELEASE="${VCPKG_HOME}/lz4_x64-windows-static/lib/lz4.lib"

        export SNAPPY_INCLUDE="${VCPKG_HOME}/snappy_x64-windows-static/include"
        export SNAPPY_LIB_DEBUG="${VCPKG_HOME}/snappy_x64-windows-static/debug/lib/snappy.lib"
        export SNAPPY_LIB_RELEASE="${VCPKG_HOME}/snappy_x64-windows-static/lib/snappy.lib"

        export ZSTD_INCLUDE="${VCPKG_HOME}/zstd_x64-windows-static/include"
        export ZSTD_LIB_DEBUG="${VCPKG_HOME}/zstd_x64-windows-static/debug/lib/zstd_d.lib"
        export ZSTD_LIB_RELEASE="${VCPKG_HOME}/zstd_x64-windows-static/lib/zstd.lib"
                
        (cd build && {
            cmake -G "Visual Studio 17 2022"  -A x64 -DCMAKE_CXX_STANDARD=17 -DCMAKE_CXX_FLAGS="/arch:SSE2 /wd4267" -DCMAKE_C_FLAGS=/arch:SSE2 -DWITH_WINDOWS_UTF8_FILENAMES=1 -DROCKSDB_WINDOWS_UTF8_FILENAMES=1 -DWITH_TESTS=0 -DWITH_MD_LIBRARY=0 -DOPTDBG=1 -DGFLAGS=0 -DSNAPPY=1 -DWITH_ZLIB=1 -DWITH_LZ4=1 -DWITH_ZSTD=1 -DPORTABLE=1 -DSNAPPY_REQUIRE_AVX=0 -DSNAPPY_REQUIRE_AVX2=0 -DSNAPPY_HAVE_BMI2=0 -DSTATIC_BMI2=0 -DWITH_TOOLS=0 -DWITH_BENCHMARK_TOOLS=0 -DWITH_TESTS=0 -DWITH_FOLLY_DISTRIBUTED_MUTEX=0 -DUSE_RTTI=1  .. || fail "Running cmake failed"
            update_vcxproj || warn "failed to patch vcxproj files for static vc runtime"
        }) || fail "cmake build generation failed"

        cmd //c "msbuild build/rocksdb.sln /p:Configuration=Release /m:$CONCURRENCY /p:VCBuildAdditionalOptions=/arch:SSE2" || fail "Rocksdb release build failed"

        ls -R ./build/Release/

        mkdir -p ../runtimes/win-x64/native && cp -v ./build/Release/rocksdb-shared.dll ../runtimes/win-x64/native/rocksdb.dll
        mkdir -p ../rocksdb-${ROCKSDBVERSION}/win-x64/native && cp -v ./build/Release/rocksdb-shared.dll ../rocksdb-${ROCKSDBVERSION}/win-x64/native/rocksdb.dll
    }) || fail "rocksdb build failed"
else
    
    echo "Assuming a posix-like environment"
    
    if [ "$(uname)" == "Darwin" ]; then
        echo "Mac (Darwin) detected"
        LIBEXT=.dylib
        RUNTIME=osx-x64
        
        CFLAGS="-Wno-defaulted-function-deleted -Wno-shadow -std=c++17 -Wmissing-exception-spec "
        
        echo "${CMAKE_INSTALL_LIBDIR}"
        echo "${CMAKE_INSTALL_INCLUDEDIR}"
        
        brew up
        brew install snappy
        brew install zstd
        brew install lz4
        brew install zlib
        # brew install bzip2
        # brew install gflags
        
        export ZLIB_INCLUDE="${HOMEBREW_CELLAR}/zlib/1.2.11/include"
        export ZLIB_LIB_RELEASE="${HOMEBREW_CELLAR}/zlib/1.2.11/lib/libz.a"
        
        export LZ4_INCLUDE="${HOMEBREW_CELLAR}/lz4/1.9.3/include"
        export LZ4_LIB_RELEASE="${HOMEBREW_CELLAR}/lz4/1.9.3/lib/liblz4.a"

        export SNAPPY_INCLUDE="${HOMEBREW_CELLAR}/snappy/1.1.9/include"
        export SNAPPY_LIB_RELEASE="${HOMEBREW_CELLAR}/snappy/1.1.9/lib/libsnappy.a"

        export ZSTD_INCLUDE="${HOMEBREW_CELLAR}/zstd/1.5.0/include"
        export ZSTD_LIB_RELEASE="${HOMEBREW_CELLAR}/zstd/1.5.0/lib/libzstd.a"
        export WITH_BZ2=0
        export ROCKSDB_DISABLE_JEMALLOC=1       
    else
        echo "Linux detected"
        ldd --version
        CFLAGS=-static-libstdc++
        LIBEXT=.so
        RUNTIME=linux-x64
        export WITH_JEMALLOC=1
        export JEMALLOC=1
        export WITH_JEMALLOC_FLAG=1
        export JEMALLOC_INCLUDE="-I/usr/include/jemalloc/"
        export JEMALLOC_LIB="/usr/lib/x86_64-linux-gnu/libjemalloc_pic.a"

        # Linux Dependencies    
        # sudo apt-get install libsnappy-dev libbz2-dev libz-dev liblz4-dev libzstd-dev
        # Linux dependencies must be now installed in the docker image located here: 
        # https://github.com/theolivenbaum/rocksdb-docker-linux/blob/main/Dockerfile

    fi
    
    mkdir -p rocksdb || fail "unable to create rocksdb directory"
    (cd rocksdb && {
        checkout "rocksdb" "$ROCKSDBREMOTE" "$ROCKSDBVERSION" "$ROCKSDBVERSION"


        #fix min macos version on build file
        sed -i '' 's/-mmacosx-version-min=10\.12/-mmacosx-version-min=10\.13/g' build_tools/build_detect_platform
        echo build_tools/build_detect_platform
        
        export CFLAGS
        export LDFLAGS
        export ROCKSDB_DISABLE_GFLAGS=1
        export WITH_DYNAMIC_EXTENSION=0
        
        (. ./build_tools/build_detect_platform detected~; {
            cat detected~
            grep detected~ -e '-DLZ4'    &> /dev/null || fail "failed to detect lz4, install liblz4-dev"
            grep detected~ -e '-DZLIB'   &> /dev/null || fail "failed to detect zlib, install libzlib-dev"
            grep detected~ -e '-DSNAPPY' &> /dev/null || fail "failed to detect snappy, install libsnappy-dev"
            grep detected~ -e '-DZSTD'   &> /dev/null || fail "failed to detect zstd, install libzstd-dev"
            grep detected~ -e '-DGFLAGS' &> /dev/null && fail "gflags detected, see https://github.com/facebook/rocksdb/issues/2310" || true
        }) || fail "dependency detection failed"

        echo "----- Build 64 bit --------------------------------------------------"
        make clean
        CFLAGS="${CFLAGS}" PORTABLE=1 make -j$CONCURRENCY shared_lib  || fail "64-bit build failed"
        strip librocksdb${LIBEXT}
        mkdir -p ../runtimes/${RUNTIME}/native && cp -vL ./librocksdb${LIBEXT} ../runtimes/${RUNTIME}/native/librocksdb${LIBEXT}
        mkdir -p ../rocksdb-${ROCKSDBVERSION}/${RUNTIME}/native && cp -vL ./librocksdb${LIBEXT} ../rocksdb-${ROCKSDBVERSION}/${RUNTIME}/native/librocksdb${LIBEXT}

    }) || fail "rocksdb build failed"
fi



