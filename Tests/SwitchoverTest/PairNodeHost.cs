using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RocksDbSharp;

namespace SwitchoverTest;

/// <summary>
/// Per-process host of one node of a primary/replica pair. Owns the RocksDB
/// instance, the <see cref="SwitchoverNode"/>, the MagicOnion server and, while
/// this node is the replica, the WAL stream from the other node.
/// </summary>
public sealed class PairNodeHost : IAsyncDisposable
{
    public RocksDb Db { get; private set; } = null!;
    public SwitchoverNode Node { get; private set; } = null!;
    public bool IsResyncing => _resyncing;
    public InjectedFaults Faults { get; } = new();

    private readonly NodeConfiguration _cfg;
    private readonly PairPeer _peer;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _resyncGate = new(1, 1);
    private readonly object _followerLock = new();
    private readonly object _ingestLock = new();

    private ReplicationRoleState _state = null!;
    private WebApplication? _app;
    private Task? _followerTask;
    private CancellationTokenSource? _followerCts;
    private volatile bool _resyncing;
    private int _resyncs;
    private int _promotesReceived;
    private int _checkpoints;
    private ulong _attachedFromSeq;

    public PairNodeHost(NodeConfiguration cfg)
    {
        _cfg = cfg;
        _peer = new PairPeer(cfg.PeerId, cfg.PeerEndpoint);
    }

    public async Task StartAsync()
    {
        Directory.CreateDirectory(_cfg.DbPath);
        _state = new ReplicationRoleState(Path.Combine(_cfg.DbPath, "role"));

        bool replica = _state.IsFresh ? _cfg.InitialRole == "replica" : _state.Role == ReplicationRole.Replica;
        if (replica && !File.Exists(Path.Combine(_cfg.DbPath, "CURRENT")))
        {
            // A new replica, or one that died while restoring a snapshot.
            await PullSnapshotWithRetryAsync();
            if (_state.IsFresh) _state.Set(0, ReplicationRole.Replica, _cfg.PeerId);
        }

        OpenDatabase();
        if (_cfg.InitialRole == "primary") SwitchoverNode.BootstrapPrimary(_state, Db, _cfg.NodeId);

        Node = new SwitchoverNode(_cfg.NodeId, _state, Db, DetachFromPrimaryAsync);

        // A node restarting as primary may have been replaced while it was
        // down (ForcePromoteAsync on the other node). Ask before serving
        // writes; if the other node cannot be reached, serve anyway - with two
        // nodes there is nobody else to ask.
        if (Node.Role != ReplicationRole.Replica)
        {
            var resolved = await Node.ResolveAsync(_peer);
            if (resolved != null) Log($"resolve at startup: {resolved.Outcome} (epoch {resolved.Epoch}){(resolved.Reason != null ? " - " + resolved.Reason : "")}");
        }

        // Subscribed only now: the follower stream may find the log diverged
        // and rebuild the database, which must not start while this method
        // is still setting up around it.
        Node.RoleChanged += (_, e) =>
        {
            Log($"role -> {e.Role} in epoch {e.Epoch} (primary: {e.PrimaryId ?? "unknown"})");
            if (e.Role == ReplicationRole.Replica) EnsureFollowerStream();
        };

        await StartServerAsync();
        Log($"started as {Node.Role} in epoch {Node.Epoch} at seq {Node.GetStatus().LatestSeq:n0}");

        if (Node.Role == ReplicationRole.Replica) EnsureFollowerStream();
        _ = Task.Run(ResolveLoopAsync);
    }

    private void OpenDatabase()
    {
        // The WAL is what lets a node that was down - for an upgrade - rejoin
        // without a snapshot, so the archive has to cover the longest a node
        // is expected to be away, at the write rate the pair takes.
        var options = new DbOptions()
            .SetCreateIfMissing(true)
            .SetWalDir(Path.Combine(_cfg.DbPath, "journal"))
            .SetWalTtlSeconds(600)
            .SetWalSizeLimitMB(4096)
            .SetMaxTotalWalSize(1024UL * 1024 * 32);
        Db = RocksDb.Open(options, _cfg.DbPath);
    }

    public StatusDto GetStatus()
    {
        var s = Node.GetStatus();
        return new StatusDto
        {
            NodeId = s.NodeId,
            Role = (int)s.Role,
            Epoch = s.Epoch,
            PromisedEpoch = s.PromisedEpoch,
            PrimaryId = s.PrimaryId,
            LatestSeq = s.LatestSeq,
            Promoting = s.Promoting,
            Resyncing = _resyncing,
            Resyncs = Volatile.Read(ref _resyncs),
            WritesOpen = Node.Writes.IsOpen,
            AttachedFromSeq = Volatile.Read(ref _attachedFromSeq),
            PromotesReceived = Volatile.Read(ref _promotesReceived),
        };
    }

    public void OnPromoteReceived() => Interlocked.Increment(ref _promotesReceived);

    public async Task<SwitchoverResult> HandOffAsync(SwitchoverOptions options)
    {
        Log($"handoff to {_cfg.PeerId} requested");
        var r = await Node.HandOffAsync(_peer, options);
        Log($"handoff to {_cfg.PeerId}: {r.Outcome} (epoch {r.Epoch}, handoff seq {r.HandoffSeq:n0}, writes held {r.WritesHeld.TotalMilliseconds:n1}ms){(r.Reason != null ? " - " + r.Reason : "")}");
        return r;
    }

    /// <summary>
    /// Settles a handoff this node could not finish - after a restart, or
    /// when the other node was unreachable.
    /// </summary>
    private async Task ResolveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (Node.Role == ReplicationRole.HandingOff)
                {
                    var r = await Node.ResolveAsync(_peer, cancellationToken: _cts.Token);
                    if (r != null) Log($"resolve: {r.Outcome} (epoch {r.Epoch}){(r.Reason != null ? " - " + r.Reason : "")}");
                }
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log($"resolve failed: {ex.Message}"); }
        }
    }

    // -----------------------------------------------------------------------
    // Replica side: the WAL stream from the primary
    // -----------------------------------------------------------------------

    private void EnsureFollowerStream()
    {
        lock (_followerLock)
        {
            if (_resyncing || _cts.IsCancellationRequested) return;
            if (_followerCts != null && _followerTask is { IsCompleted: false }) return;
            _followerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var ct = _followerCts.Token;
            _followerTask = Task.Run(() => RunFollowerStreamAsync(ct));
        }
    }

    /// <summary>
    /// Called by the switchover once this replica holds the handoff point:
    /// returns as soon as nothing ingests into the database any more. Writes
    /// are held until then, so this does not wait for the gRPC stream to shut
    /// down - that takes tens of milliseconds - it only waits out a chunk
    /// being applied; the stream task sees its token and ends on its own.
    /// CancelAsync, because Cancel runs gRPC's cancellation callbacks - the
    /// stream teardown - on this thread before it returns.
    /// </summary>
    private Task DetachFromPrimaryAsync(CancellationToken cancellationToken)
    {
        lock (_followerLock)
        {
            _ = _followerCts?.CancelAsync();
            _followerCts = null;
        }
        lock (_ingestLock) { }
        return Task.CompletedTask;
    }

    private async Task StopFollowerStreamAsync()
    {
        Task? running;
        lock (_followerLock)
        {
            _followerCts?.Cancel();
            _followerCts = null;
            running = _followerTask;
        }
        if (running != null)
        {
            try { await running; } catch { }
        }
    }

    private async Task RunFollowerStreamAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ch = GrpcChannel.ForAddress(_cfg.PeerEndpoint);
                var client = MagicOnionClient.Create<IPairService>(ch).WithCancellationToken(ct);

                // The attach check: our log must be a prefix of the primary's.
                ulong latest = Db.GetLatestSequenceNumber();
                var check = await client.CheckReplicaLogAsync(latest, Node.GetEpochForSeq(latest));
                if (!check.IsPrimary)
                {
                    await Task.Delay(200, ct);
                    continue;
                }
                if (!check.Consistent)
                {
                    Log($"log at {latest:n0} (epoch {Node.GetEpochForSeq(latest)}) is not a prefix of {_cfg.PeerId}'s (epochs there: {check.Epochs.Replace('\n', ' ')}); rebuilding from a snapshot");
                    TriggerResync();
                    return;
                }
                if (!Node.AcceptStream(_cfg.PeerId, check.Epoch))
                {
                    Log($"{_cfg.PeerId} is primary in epoch {check.Epoch}, older than epoch {Node.Epoch}; not following it");
                    await Task.Delay(500, ct);
                    continue;
                }
                _state.MergeEpochs(check.Epochs);
                long seenEpoch = check.Epoch;

                Volatile.Write(ref _attachedFromSeq, latest + 1);
                Log($"following {_cfg.PeerId} (epoch {check.Epoch}) from seq {latest + 1:n0}");

                using var stream = await client.SyncUpdatesAsync(latest + 1);
                var consumer = new ReplicationConsumer(Db);
                while (await stream.ResponseStream.MoveNext(ct))
                {
                    var chunk = stream.ResponseStream.Current;
                    try
                    {
                        while (Volatile.Read(ref Faults.PauseIngest)) await Task.Delay(10, ct);

                        if (!Node.AcceptStream(chunk.PrimaryId, chunk.PrimaryEpoch))
                        {
                            Log($"dropping the stream from {chunk.PrimaryId}: epoch {chunk.PrimaryEpoch} is older than {Node.Epoch}");
                            break;
                        }
                        if (chunk.PrimaryEpoch != seenEpoch)
                        {
                            // The primary moved to a new epoch (it resumed after
                            // an aborted handoff); learn where it starts.
                            _state.MergeEpochs(await client.GetEpochsAsync());
                            seenEpoch = chunk.PrimaryEpoch;
                        }

                        // A replica applies the primary's entries at exactly the
                        // same sequence numbers - which is what makes it able to
                        // take over at the handoff point. Anything else diverges.
                        ulong expected = Db.GetLatestSequenceNumber() + 1;
                        if (chunk.FirstSeq != expected)
                        {
                            Log($"stream mismatch: expected seq {expected:n0}, got {chunk.FirstSeq:n0}; rebuilding from a snapshot");
                            TriggerResync();
                            return;
                        }
                        lock (_ingestLock)
                        {
                            if (ct.IsCancellationRequested) return;
                            consumer.IngestChunk(chunk.Data);
                        }
                    }
                    finally
                    {
                        chunk.ReturnToPool();
                    }
                }
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log($"stream from {_cfg.PeerId} failed: {ex.GetType().Name}: {ex.Message}; retrying");
                try { await Task.Delay(500, ct); } catch { return; }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Snapshot: initial sync and resync
    // -----------------------------------------------------------------------

    public void BeginCheckpoint()
    {
        if (Interlocked.Increment(ref _checkpoints) == 1) Db.DisableFileDeletions();
    }

    public void EndCheckpoint()
    {
        if (Interlocked.Decrement(ref _checkpoints) == 0) Db.EnableFileDeletions();
    }

    private async Task PullSnapshotWithRetryAsync()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await PullSnapshotAsync();
                return;
            }
            catch (Exception ex) when (attempt < 60 && !_cts.IsCancellationRequested)
            {
                Log($"snapshot from {_cfg.PeerId} failed: {ex.Message}; retrying");
                await Task.Delay(1000);
            }
        }
    }

    private async Task PullSnapshotAsync()
    {
        using var ch = GrpcChannel.ForAddress(_cfg.PeerEndpoint);
        var client = MagicOnionClient.Create<IPairService>(ch);

        // A full transfer. The cluster sample's ReplicationDelta exchange would
        // reuse SSTs both sides hold - but after a switchover the two nodes
        // have flushed their own SSTs independently, so it would reuse little.
        ReplicationDelta.PrepareForRestore(_cfg.DbPath, Array.Empty<string>(), new[] { "role", "node.json" });

        using var stream = await client.SyncInitialStateAsync();
        FileStream? current = null;
        string? currentName = null;
        long bytes = 0;
        int files = 0;
        try
        {
            while (await stream.ResponseStream.MoveNext(_cts.Token))
            {
                var chunk = stream.ResponseStream.Current;
                if (currentName != chunk.FileName)
                {
                    current?.Dispose();
                    currentName = chunk.FileName;
                    current = new FileStream(Path.Combine(_cfg.DbPath, chunk.FileName), FileMode.Create, FileAccess.Write);
                    files++;
                }
                await current!.WriteAsync(chunk.Content);
                bytes += chunk.Content.Length;
            }
        }
        finally
        {
            current?.Dispose();
        }
        if (files == 0) throw new InvalidOperationException($"{_cfg.PeerId} sent no snapshot (not primary?)");

        // The restored log is byte-for-byte the primary's, so its epoch map is
        // the right description of it.
        _state.ReplaceEpochs(await client.GetEpochsAsync());
        Log($"snapshot from {_cfg.PeerId}: {files} files, {bytes:n0} bytes");
    }

    private void TriggerResync()
    {
        if (_resyncing || _cts.IsCancellationRequested) return;
        _ = Task.Run(PerformResyncAsync);
    }

    private async Task PerformResyncAsync()
    {
        if (!await _resyncGate.WaitAsync(0)) return;
        try
        {
            _resyncing = true;
            Node.DetachDatabase();
            await StopFollowerStreamAsync();
            await Task.Delay(500); // let in-flight RPC handlers finish with the old instance
            Db.Dispose();

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await PullSnapshotAsync();
                    OpenDatabase();
                    Node.AttachDatabase(Db);
                    Interlocked.Increment(ref _resyncs);
                    _resyncing = false;
                    Log($"resync complete at seq {Db.GetLatestSequenceNumber():n0}");
                    EnsureFollowerStream();
                    return;
                }
                catch (Exception ex)
                {
                    Log($"resync attempt failed: {ex.Message}; retrying");
                    await Task.Delay(1000);
                }
            }
        }
        finally
        {
            _resyncGate.Release();
        }
    }

    // -----------------------------------------------------------------------

    private async Task StartServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddGrpc();
        builder.Services.AddMagicOnion();
        builder.Services.AddSingleton(this);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.ListenLocalhost(_cfg.Port, lo => lo.Protocols = HttpProtocols.Http2);
        });
        _app = builder.Build();
        _app.MapMagicOnionService();
        _ = _app.RunAsync(_cts.Token);
        await Task.Delay(300); // let the listener bind
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_app != null) await _app.StopAsync(); } catch { }
        await StopFollowerStreamAsync();
        _peer.Dispose();
        if (!_resyncing) Db?.Dispose();
    }

    public void Log(string msg) =>
        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss.fff} {_cfg.NodeId}] {msg}");
}

/// <summary>The other node of the pair, as <see cref="SwitchoverNode"/> calls it.</summary>
internal sealed class PairPeer : ISwitchoverPeer, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly IPairService _client;

    public PairPeer(string nodeId, string endpoint)
    {
        NodeId = nodeId;
        _channel = GrpcChannel.ForAddress(endpoint);
        _client = MagicOnionClient.Create<IPairService>(_channel);
    }

    public string NodeId { get; }

    // Wait-for-ready: a channel whose last connection attempt failed (the
    // other node was still starting) otherwise fails every call instantly
    // until its reconnect backoff runs out. Every call has a deadline, so
    // this waits at most that long.
    private IPairService Call(CancellationToken ct) =>
        _client.WithOptions(new CallOptions(cancellationToken: ct).WithWaitForReady());

    public async Task<ReplicationNodeStatus?> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            return ToStatus(await Call(timeout.Token).GetStatusAsync());
        }
        catch { return null; }
    }

    public async Task<PromoteResult?> PromoteAsync(PromoteRequest request, CancellationToken ct)
    {
        var r = await Call(ct).PromoteAsync(new PromoteRequestDto
        {
            Epoch = request.Epoch,
            FromPrimaryId = request.FromPrimaryId,
            HandoffSeq = request.HandoffSeq,
            CatchUpTimeoutMs = request.CatchUpTimeoutMs,
        });
        return new PromoteResult(r.Promoted, r.Epoch, (ReplicationRole)r.Role, r.Reason);
    }

    public async Task<ReplicationNodeStatus?> RejectEpochAsync(long epoch, CancellationToken ct)
    {
        try { return ToStatus(await Call(ct).RejectEpochAsync(epoch)); }
        catch { return null; }
    }

    private static ReplicationNodeStatus? ToStatus(StatusDto? s) =>
        s == null || s.Resyncing ? null
            : new ReplicationNodeStatus(s.NodeId, s.RoleValue, s.Epoch, s.PromisedEpoch, s.PrimaryId, s.LatestSeq, s.Promoting);

    public void Dispose() => _channel.Dispose();
}

public sealed class InjectedFaults
{
    public bool PauseIngest;
    public int DropNextPromoteReply;
    public int DelayNextPromoteMs;
}

public sealed class NodeConfiguration
{
    public string NodeId { get; set; } = "";
    public int Port { get; set; }
    public string DbPath { get; set; } = "";
    /// <summary>The role a brand-new node starts in. Ignored once the node has role state.</summary>
    public string InitialRole { get; set; } = "replica";
    public string PeerId { get; set; } = "";
    public string PeerEndpoint { get; set; } = "";
}
