using System;
using System.Buffers;
using System.Reflection;
using System.Threading.Tasks;
using Grpc.Core;
using MagicOnion;
using MagicOnion.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace ReplicationTest
{
    public interface IReplicationService : IService<IReplicationService>
    {
        Task<ServerStreamingResult<ReplicationFileData>> SyncInitialStateAsync();
        Task<ServerStreamingResult<ReplicationBatchData>> SyncUpdatesAsync(ulong startSeq);
        UnaryResult<bool> ReportLastSyncSequenceNumber(int replicaIndex, ulong seqNumber);
    }

    [MessagePackObject]
    public class ReplicationFileData
    {
        [Key(0)] public string FileName { get; set; } = string.Empty;
        [Key(1)] public ulong FileSize { get; set; }
        [Key(2)] public byte[] Content { get; set; } = Array.Empty<byte>();
    }

    [MessagePackObject]
    [MessagePackFormatter(typeof(PooledReplicationBatchDataSerializer))]
    public class ReplicationBatchData
    {
        [Key(0)] public ulong SequenceNumber { get; set; }
        [Key(1)] public int Length { get; set; } = 0;
        [Key(2)] public byte[] PooledData { get; set; } = Array.Empty<byte>();

        [IgnoreMember] public ReadOnlySpan<byte> Data => PooledData.AsSpan(0, Length);

        internal void ReturnToPool()
        {
            ArrayPool<byte>.Shared.Return(PooledData);
            PooledData = null;
        }
    }


    public class PooledReplicationBatchDataSerializer : IMessagePackFormatter<ReplicationBatchData>
    {
        public ReplicationBatchData Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            var data = new ReplicationBatchData();
            data.SequenceNumber = reader.ReadUInt64();
            data.Length = reader.ReadInt32();
            var pooledData = ArrayPool<byte>.Shared.Rent(data.Length);
            var dataRaw = reader.ReadRaw(data.Length);
            dataRaw.CopyTo(pooledData.AsSpan(0, data.Length));
            data.PooledData  = pooledData;
            return data;
        }

        public void Serialize(ref MessagePackWriter writer, ReplicationBatchData value, MessagePackSerializerOptions options)
        {
            writer.WriteUInt64(value.SequenceNumber);
            writer.WriteInt32(value.Length);
            writer.WriteRaw(value.PooledData.AsSpan(0, value.Length));
        }
    }
}
