# Scripts for building RocksDb native libraries for the rocksdb-sharp project

Windows Build Script: Building rocksdb for Windows is hard; this project contains a build script to make it easy.

## Building Native Library

Pre-built native binaries can be downloaded from the releases page.  You may also build them yourself.

This is now buildable on Windows thanks to the Bing team at Microsoft who are actively using rocksdb.  Rocksdb-sharp should work on any platform provided the native unmanaged library is available.

### Windows Native Build Instructions

#### Prerequisities:
* Git for Windows (specifically, the git bash environment)
* CMake
* Visual Studio 2017 (older versions may work but are not tested)

#### Build Instructions:
1. Open "Developer Command Prompt for VS2017"
2. Run git's ```bash.exe```
3. execute ```./build-rocksdb.sh```

This will create a rocksdb.dll and copy it to the where the .sln file is expecting it to be.  (If you only need to run this in Windows, you can remove the references to the other two platform binaries from the .sln)

### Linux Native Build Instructions

1. ```./build-rocksdb.sh```

### Mac Native Build Instructions

1. ```./build-rocksdb.sh```

  
