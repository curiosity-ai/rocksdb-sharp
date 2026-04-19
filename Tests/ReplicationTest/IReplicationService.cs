using System;
using System.Threading.Tasks;
using MagicOnion;
using MessagePack;

namespace ReplicationTest
{
    public interface IReplicationService : IService<IReplicationService>
    {
        Task<ServerStreamingResult<ReplicationFileData>> SyncInitialStateAsync();
        Task<ServerStreamingResult<ReplicationBatchData>> SyncUpdatesAsync(ulong startSeq);
    }

    [MessagePackObject]
    public class ReplicationFileData
    {
        [Key(0)] public string FileName { get; set; } = string.Empty;
        [Key(1)] public ulong FileSize { get; set; }
        [Key(2)] public byte[] Content { get; set; } = Array.Empty<byte>();
    }

    [MessagePackObject]
    public class ReplicationBatchData
    {
        [Key(0)] public ulong SequenceNumber { get; set; }
        [Key(1)] public byte[] Data { get; set; } = Array.Empty<byte>();
    }
}
