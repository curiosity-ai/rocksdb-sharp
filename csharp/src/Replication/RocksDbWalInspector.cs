#if !NETSTANDARD2_0
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZstdSharp;

namespace RocksDbSharp;

public static class RocksDbWalInspector
{
    private const int BlockSize = 32768;
    private const int HeaderSize = 7;
    private const int RecyclableHeaderSize = 11;
    private const int WriteBatchHeaderSize = 12;
    private const uint ZstdCompressionType = 7;

    private enum RecordType : byte
    {
        ZeroType = 0,
        FullType = 1,
        FirstType = 2,
        MiddleType = 3,
        LastType = 4,

        RecyclableFullType = 5,
        RecyclableFirstType = 6,
        RecyclableMiddleType = 7,
        RecyclableLastType = 8,

        SetCompressionType = 9,

        UserDefinedTimestampSizeType = 10,
        RecyclableUserDefinedTimestampSizeType = 11,

        PredecessorWalInfoType = 130,
        RecyclePredecessorWalInfoType = 131
    }

    public static Dictionary<string, ulong> GetFirstSequenceNumbers(string archiveWalFolder)
    {
        if (archiveWalFolder == null) throw new ArgumentNullException(nameof(archiveWalFolder));
        if (!Directory.Exists(archiveWalFolder))
            throw new DirectoryNotFoundException($"Directory not found: {archiveWalFolder}");

        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(archiveWalFolder, "*.log", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            result[Path.GetFileName(path)] = ReadFirstSequenceNumber(path);
        }

        return result;
    }

    private static ulong ReadFirstSequenceNumber(string walPath)
    {
        using var stream = new FileStream(
            walPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var block = new byte[BlockSize];
        var logical = new MemoryStream();
        var haveFragmentedRecord = false;

        WalZstdState? zstd = null;

        try
        {
            while (true)
            {
                int bytesRead = ReadBlock(stream, block, 0, BlockSize);
                if (bytesRead == 0)
                    return 0;

                int offset = 0;
                while (true)
                {
                    int remaining = bytesRead - offset;
                    if (remaining < HeaderSize)
                        break;

                    byte typeByte = block[offset + 6];
                    bool recyclable = IsRecyclableType(typeByte);
                    int currentHeaderSize = recyclable ? RecyclableHeaderSize : HeaderSize;

                    if (remaining < currentHeaderSize)
                        break;

                    ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
                        new ReadOnlySpan<byte>(block, offset + 4, 2));

                    int recordTotalSize = currentHeaderSize + length;
                    if (recordTotalSize > remaining)
                        return 0;

                    var payload = new ReadOnlySpan<byte>(block, offset + currentHeaderSize, length);
                    var type = (RecordType)typeByte;

                    offset += recordTotalSize;

                    switch (type)
                    {
                        case RecordType.ZeroType:
                            if (length == 0)
                                continue;
                            return 0;

                        case RecordType.SetCompressionType:
                        {
                            if (length < 4)
                                throw new InvalidDataException($"Invalid SetCompressionType record in '{walPath}'.");

                            uint compressionType = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
                            if (compressionType == ZstdCompressionType)
                            {
                                zstd = new WalZstdState();
                            }
                            else
                            {
                                throw new NotSupportedException(
                                    $"WAL '{walPath}' uses compression type {compressionType}, " +
                                    "but this reader currently supports only ZSTD.");
                            }

                            continue;
                        }

                        case RecordType.UserDefinedTimestampSizeType:
                        case RecordType.RecyclableUserDefinedTimestampSizeType:
                        case RecordType.PredecessorWalInfoType:
                        case RecordType.RecyclePredecessorWalInfoType:
                            continue;

                        case RecordType.FullType:
                        case RecordType.RecyclableFullType:
                        {
                            byte[] logicalRecord = zstd == null
                                ? payload.ToArray()
                                : zstd.DecompressRecord(payload);

                            ulong seq = TryReadWriteBatchSequence(logicalRecord);
                            if (seq != 0)
                                return seq;
                            continue;
                        }

                        case RecordType.FirstType:
                        case RecordType.RecyclableFirstType:
                        {
                            logical.SetLength(0);

                            byte[] firstFragment = zstd == null
                                ? payload.ToArray()
                                : zstd.DecompressRecord(payload);

                            logical.Write(firstFragment, 0, firstFragment.Length);
                            haveFragmentedRecord = true;
                            continue;
                        }

                        case RecordType.MiddleType:
                        case RecordType.RecyclableMiddleType:
                        {
                            if (!haveFragmentedRecord)
                                continue;

                            byte[] midFragment = zstd == null
                                ? payload.ToArray()
                                : zstd.DecompressRecord(payload);

                            logical.Write(midFragment, 0, midFragment.Length);
                            continue;
                        }

                        case RecordType.LastType:
                        case RecordType.RecyclableLastType:
                        {
                            if (!haveFragmentedRecord)
                                continue;

                            byte[] lastFragment = zstd == null
                                ? payload.ToArray()
                                : zstd.DecompressRecord(payload);

                            logical.Write(lastFragment, 0, lastFragment.Length);
                            haveFragmentedRecord = false;

                            ulong seq = TryReadWriteBatchSequence(
                                logical.GetBuffer().AsSpan(0, checked((int)logical.Length)));

                            if (seq != 0)
                                return seq;

                            logical.SetLength(0);
                            continue;
                        }

                        default:
                            return 0;
                    }
                }

                if (bytesRead < BlockSize)
                    return 0;
            }
        }
        finally
        {
            zstd?.Dispose();
        }
    }

    private static ulong TryReadWriteBatchSequence(ReadOnlySpan<byte> logicalRecord)
    {
        if (logicalRecord.Length < WriteBatchHeaderSize)
            return 0;

        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(logicalRecord.Slice(0, 8));
        return seq;
    }

    private static bool IsRecyclableType(byte typeByte)
    {
        return typeByte == (byte)RecordType.RecyclableFullType
            || typeByte == (byte)RecordType.RecyclableFirstType
            || typeByte == (byte)RecordType.RecyclableMiddleType
            || typeByte == (byte)RecordType.RecyclableLastType
            || typeByte == (byte)RecordType.RecyclableUserDefinedTimestampSizeType
            || typeByte == (byte)RecordType.RecyclePredecessorWalInfoType;
    }

    private static int ReadBlock(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, offset + total, count - total);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    private sealed class WalZstdState : IDisposable
    {
        private readonly Decompressor _decompressor = new();
        private bool _disposed;

        public byte[] DecompressRecord(ReadOnlySpan<byte> compressed)
        {
            EnsureNotDisposed();

            if (compressed.Length == 0)
                return Array.Empty<byte>();

            byte[] input = compressed.ToArray();
            using var inputStream = new MemoryStream(input, writable: false);

            // Reuse the same Decompressor instance across records so the
            // decompression context persists for the whole WAL.
            using var zstdStream = new DecompressionStream(
                inputStream,
                _decompressor,
                bufferSize: 0,
                checkEndOfStream: true,
                preserveDecompressor: true,
                leaveOpen: false);

            using var output = new MemoryStream();
            zstdStream.CopyTo(output);
            return output.ToArray();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _decompressor.Dispose();
            _disposed = true;
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WalZstdState));
        }
    }
}
#endif