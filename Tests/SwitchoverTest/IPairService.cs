using System;
using System.Buffers;
using MagicOnion;
using MessagePack;
using MessagePack.Formatters;

namespace SwitchoverTest;

public interface IPairService : IService<IPairService>
{
    // ---- Switchover RPCs (SwitchoverNode <-> SwitchoverNode) ----
    UnaryResult<StatusDto> GetStatusAsync();
    UnaryResult<PromoteResponseDto> PromoteAsync(PromoteRequestDto req);
    UnaryResult<StatusDto> RejectEpochAsync(long epoch);

    // ---- Replication (reused from the replication path) ----
    UnaryResult<LogCheckDto> CheckReplicaLogAsync(ulong latestSeq, long epochAtLatest);
    UnaryResult<string> GetEpochsAsync();
    Task<ServerStreamingResult<SnapshotChunkDto>> SyncInitialStateAsync();
    Task<ServerStreamingResult<WalChunkDto>> SyncUpdatesAsync(ulong startSeq);

    // ---- Operations, driven by a deploy script (the coordinator here) ----
    UnaryResult<HandOffResponseDto> HandOffAsync(int catchUpTimeoutMs, ulong maxLagToStart);
    UnaryResult<long> ForcePromoteAsync();

    // ---- Client API and test hooks ----
    UnaryResult<WriteResponseDto> WriteAsync(string key, string value);
    UnaryResult<ReadResponseDto> ReadAsync(string key);
    UnaryResult<ChecksumDto> ChecksumAsync();
    UnaryResult<bool> SetFaultsAsync(FaultsDto faults);
}

[MessagePackObject]
public class StatusDto
{
    [Key(0)] public string NodeId { get; set; } = "";
    [Key(1)] public int Role { get; set; }
    [Key(2)] public long Epoch { get; set; }
    [Key(3)] public long PromisedEpoch { get; set; }
    [Key(4)] public string? PrimaryId { get; set; }
    [Key(5)] public ulong LatestSeq { get; set; }
    [Key(6)] public bool Promoting { get; set; }
    [Key(7)] public bool Resyncing { get; set; }
    [Key(8)] public int Resyncs { get; set; }
    [Key(9)] public bool WritesOpen { get; set; }
    [Key(10)] public ulong AttachedFromSeq { get; set; }
    [Key(11)] public int PromotesReceived { get; set; }

    [IgnoreMember] public RocksDbSharp.ReplicationRole RoleValue => (RocksDbSharp.ReplicationRole)Role;
}

[MessagePackObject]
public class PromoteRequestDto
{
    [Key(0)] public long Epoch { get; set; }
    [Key(1)] public string FromPrimaryId { get; set; } = "";
    [Key(2)] public ulong HandoffSeq { get; set; }
    [Key(3)] public int CatchUpTimeoutMs { get; set; }
}

[MessagePackObject]
public class PromoteResponseDto
{
    [Key(0)] public bool Promoted { get; set; }
    [Key(1)] public long Epoch { get; set; }
    [Key(2)] public int Role { get; set; }
    [Key(3)] public string? Reason { get; set; }
}

[MessagePackObject]
public class LogCheckDto
{
    [Key(0)] public bool IsPrimary { get; set; }
    [Key(1)] public bool Consistent { get; set; }
    [Key(2)] public long Epoch { get; set; }
    [Key(3)] public string Epochs { get; set; } = "";
}

[MessagePackObject]
public class HandOffResponseDto
{
    [Key(0)] public string Outcome { get; set; } = "";
    [Key(1)] public long Epoch { get; set; }
    [Key(2)] public ulong HandoffSeq { get; set; }
    [Key(3)] public double WritesHeldMs { get; set; }
    [Key(4)] public string? Reason { get; set; }
}

[MessagePackObject]
public class WriteResponseDto
{
    [Key(0)] public bool Ok { get; set; }
    [Key(1)] public string? PrimaryHint { get; set; }
    [Key(2)] public ulong Seq { get; set; }
}

[MessagePackObject]
public class ReadResponseDto
{
    [Key(0)] public bool Found { get; set; }
    [Key(1)] public string? Value { get; set; }
}

[MessagePackObject]
public class ChecksumDto
{
    [Key(0)] public bool Available { get; set; }
    [Key(1)] public long KeyCount { get; set; }
    [Key(2)] public ulong Hash { get; set; }
    [Key(3)] public ulong LatestSeq { get; set; }
}

/// <summary>Fault injection, so the coordinator can drive the failure paths of a handoff.</summary>
[MessagePackObject]
public class FaultsDto
{
    /// <summary>Stop applying the replication stream (the replica falls behind).</summary>
    [Key(0)] public bool PauseIngest { get; set; }
    /// <summary>Carry out the next promote request but fail the RPC, as if the answer was lost.</summary>
    [Key(1)] public bool DropNextPromoteReply { get; set; }
    /// <summary>Hold the next promote request this long before handling it, as if it was stuck on the wire.</summary>
    [Key(2)] public int DelayNextPromoteMs { get; set; }
}

/// <summary>
/// One message of the snapshot stream: a chunk of a checkpoint file. A new
/// FileName starts a new file; CURRENT comes last.
/// </summary>
[MessagePackObject]
public class SnapshotChunkDto
{
    [Key(0)] public string FileName { get; set; } = "";
    [Key(1)] public byte[] Content { get; set; } = Array.Empty<byte>();
}

/// <summary>A <see cref="RocksDbSharp.WalChunk"/> on the wire, tagged with the sender's role epoch.</summary>
[MessagePackObject]
[MessagePackFormatter(typeof(WalChunkDtoSerializer))]
public class WalChunkDto
{
    [Key(0)] public ulong FirstSeq { get; set; }
    [Key(1)] public ulong NextSeq { get; set; }
    [Key(2)] public long PrimaryEpoch { get; set; }
    [Key(3)] public string PrimaryId { get; set; } = "";
    [Key(4)] public int Length { get; set; }
    [Key(5)] public byte[] PooledData { get; set; } = Array.Empty<byte>();

    [IgnoreMember] public ReadOnlySpan<byte> Data => PooledData.AsSpan(0, Length);

    public void ReturnToPool()
    {
        if (PooledData.Length > 0) ArrayPool<byte>.Shared.Return(PooledData);
        PooledData = Array.Empty<byte>();
    }
}

public class WalChunkDtoSerializer : IMessagePackFormatter<WalChunkDto>
{
    public WalChunkDto Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var d = new WalChunkDto();
        d.FirstSeq = reader.ReadUInt64();
        d.NextSeq = reader.ReadUInt64();
        d.PrimaryEpoch = reader.ReadInt64();
        d.PrimaryId = reader.ReadString() ?? "";
        d.Length = reader.ReadInt32();
        var pooled = ArrayPool<byte>.Shared.Rent(Math.Max(1, d.Length));
        reader.ReadRaw(d.Length).CopyTo(pooled.AsSpan(0, d.Length));
        d.PooledData = pooled;
        return d;
    }

    public void Serialize(ref MessagePackWriter writer, WalChunkDto value, MessagePackSerializerOptions options)
    {
        writer.WriteUInt64(value.FirstSeq);
        writer.WriteUInt64(value.NextSeq);
        writer.WriteInt64(value.PrimaryEpoch);
        writer.Write(value.PrimaryId);
        writer.WriteInt32(value.Length);
        writer.WriteRaw(value.PooledData.AsSpan(0, value.Length));
    }
}
