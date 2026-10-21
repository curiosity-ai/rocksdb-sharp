using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MagicOnion;
using MagicOnion.Server;
using RocksDbSharp;

namespace SwitchoverTest;

public class PairService : ServiceBase<IPairService>, IPairService
{
    private readonly PairNodeHost _host;

    public PairService(PairNodeHost host)
    {
        _host = host;
    }

    private SwitchoverNode Node => _host.Node;

    public UnaryResult<StatusDto> GetStatusAsync() => new(_host.GetStatus());

    public async UnaryResult<PromoteResponseDto> PromoteAsync(PromoteRequestDto req)
    {
        _host.OnPromoteReceived();
        var faults = _host.Faults;
        int delayMs = Interlocked.Exchange(ref faults.DelayNextPromoteMs, 0);
        if (delayMs > 0)
        {
            _host.Log($"promote to epoch {req.Epoch} held for {delayMs}ms (injected fault)");
            await Task.Delay(delayMs);
        }

        var r = await Node.HandlePromoteAsync(new PromoteRequest(req.Epoch, req.FromPrimaryId, req.HandoffSeq, req.CatchUpTimeoutMs));
        _host.Log($"promote to epoch {req.Epoch} at {req.HandoffSeq:n0}: {(r.Promoted ? "promoted" : "refused: " + r.Reason)}");

        if (Interlocked.Exchange(ref faults.DropNextPromoteReply, 0) == 1)
        {
            throw new IOException("injected fault: promote reply dropped");
        }

        return new PromoteResponseDto { Promoted = r.Promoted, Epoch = r.Epoch, Role = (int)r.Role, Reason = r.Reason };
    }

    public async UnaryResult<StatusDto> RejectEpochAsync(long epoch)
    {
        await Node.HandleRejectEpochAsync(epoch);
        _host.Log($"epoch {epoch} rejected on request; now {Node.Role} in epoch {Node.Epoch}");
        return _host.GetStatus();
    }

    public UnaryResult<LogCheckDto> CheckReplicaLogAsync(ulong latestSeq, long epochAtLatest)
    {
        var r = Node.CheckReplicaLog(latestSeq, epochAtLatest);
        return new(new LogCheckDto { IsPrimary = r.IsPrimary, Consistent = r.Consistent, Epoch = r.Epoch, Epochs = r.Epochs });
    }

    public UnaryResult<string> GetEpochsAsync() => new(Node.SerializeEpochs());

    public async Task<ServerStreamingResult<SnapshotChunkDto>> SyncInitialStateAsync()
    {
        var stream = GetServerStreamingContext<SnapshotChunkDto>();
        if (_host.IsResyncing || Node.Role == ReplicationRole.Replica) return stream.Result();

        // File deletions are off only while the checkpoint is copied: see
        // ReplicationService in the replication sample for why it must not be
        // held for the life of the process.
        _host.BeginCheckpoint();
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "rocksdb_switchover_snap_" + Guid.NewGuid());
            using var session = new ReplicationSource(_host.Db).GetInitialState(tempPath);

            var names = session.GetManifest().Select(f => f.FileName).OrderBy(n => n == "CURRENT" ? 1 : 0).ToList();
            var buffer = new byte[1024 * 1024];
            foreach (var name in names)
            {
                using var file = session.OpenFile(name);
                bool sentAny = false;
                int read;
                while ((read = await file.FileStream.ReadAsync(buffer)) > 0)
                {
                    await stream.WriteAsync(new SnapshotChunkDto { FileName = name, Content = buffer.AsSpan(0, read).ToArray() });
                    sentAny = true;
                }
                if (!sentAny) await stream.WriteAsync(new SnapshotChunkDto { FileName = name });
            }
        }
        finally
        {
            _host.EndCheckpoint();
        }
        return stream.Result();
    }

    public async Task<ServerStreamingResult<WalChunkDto>> SyncUpdatesAsync(ulong startSeq)
    {
        var stream = GetServerStreamingContext<WalChunkDto>();
        var ct = Context.CallContext.CancellationToken;

        using var tailer = new ReplicationSource(_host.Db).TailWal(startSeq);
        while (!ct.IsCancellationRequested)
        {
            // Epoch before role: if the role still allows streaming afterwards,
            // the epoch read belongs to this node's time as primary. A node in
            // HandingOff keeps streaming - that is how the replica reaches the
            // handoff point.
            long epoch = Node.Epoch;
            if (Node.Role == ReplicationRole.Replica || _host.IsResyncing) break;

            if (tailer.TryReadChunk(out var chunk))
            {
                await stream.WriteAsync(new WalChunkDto
                {
                    FirstSeq = chunk.FirstSequenceNumber,
                    NextSeq = chunk.NextSequenceNumber,
                    PrimaryEpoch = epoch,
                    PrimaryId = Node.NodeId,
                    Length = chunk.Length,
                    PooledData = chunk.Buffer,
                });
            }
            else
            {
                try { await tailer.WaitForUpdatesAsync(ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        return stream.Result();
    }

    public async UnaryResult<HandOffResponseDto> HandOffAsync(int catchUpTimeoutMs, ulong maxLagToStart)
    {
        var r = await _host.HandOffAsync(new SwitchoverOptions
        {
            CatchUpTimeout = TimeSpan.FromMilliseconds(catchUpTimeoutMs),
            MaxLagToStart = maxLagToStart,
        });
        return new HandOffResponseDto
        {
            Outcome = r.Outcome.ToString(),
            Epoch = r.Epoch,
            HandoffSeq = r.HandoffSeq,
            WritesHeldMs = r.WritesHeld.TotalMilliseconds,
            Reason = r.Reason,
        };
    }

    public async UnaryResult<long> ForcePromoteAsync()
    {
        long epoch = await Node.ForcePromoteAsync();
        _host.Log($"force-promoted to primary in epoch {epoch} at seq {_host.Db.GetLatestSequenceNumber():n0}");
        return epoch;
    }

    public async UnaryResult<WriteResponseDto> WriteAsync(string key, string value)
    {
        // Writes that arrive while a handoff holds the gate wait for it to
        // reopen (abort) or close (completed - then they are redirected).
        if (!await Node.Writes.TryEnterAsync(TimeSpan.FromSeconds(2)))
        {
            return new WriteResponseDto { Ok = false, PrimaryHint = Node.PrimaryId == Node.NodeId ? null : Node.PrimaryId };
        }
        try
        {
            _host.Db.Put(key, value);
            return new WriteResponseDto { Ok = true, Seq = _host.Db.GetLatestSequenceNumber() };
        }
        finally
        {
            Node.Writes.Exit();
        }
    }

    public UnaryResult<ReadResponseDto> ReadAsync(string key)
    {
        if (_host.IsResyncing) return new(new ReadResponseDto());
        var value = _host.Db.Get(key);
        return new(new ReadResponseDto { Found = value != null, Value = value });
    }

    public UnaryResult<ChecksumDto> ChecksumAsync()
    {
        if (_host.IsResyncing) return new(new ChecksumDto());

        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;
        ulong hash = FnvOffset;
        long count = 0;
        using (var it = _host.Db.NewIterator())
        {
            for (it.SeekToFirst(); it.Valid(); it.Next())
            {
                foreach (var b in it.GetKeySpan()) { hash ^= b; hash *= FnvPrime; }
                hash ^= 0xFF; hash *= FnvPrime;
                foreach (var b in it.GetValueSpan()) { hash ^= b; hash *= FnvPrime; }
                hash ^= 0xFE; hash *= FnvPrime;
                count++;
            }
        }
        return new(new ChecksumDto { Available = true, KeyCount = count, Hash = hash, LatestSeq = _host.Db.GetLatestSequenceNumber() });
    }

    public UnaryResult<bool> SetFaultsAsync(FaultsDto faults)
    {
        Volatile.Write(ref _host.Faults.PauseIngest, faults.PauseIngest);
        Volatile.Write(ref _host.Faults.DropNextPromoteReply, faults.DropNextPromoteReply ? 1 : 0);
        Volatile.Write(ref _host.Faults.DelayNextPromoteMs, faults.DelayNextPromoteMs);
        return new(true);
    }
}
