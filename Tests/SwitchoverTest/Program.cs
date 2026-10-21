using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using MagicOnion.Client;
using RocksDbSharp;

namespace SwitchoverTest;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "node")
        {
            var cfg = JsonSerializer.Deserialize<NodeConfiguration>(await File.ReadAllTextAsync(args[1]))
                      ?? throw new Exception("Bad config file");
            var host = new PairNodeHost(cfg);
            await host.StartAsync();
            await Task.Delay(-1);
            return 0;
        }

        string scenario = args.Length == 0 ? "all" : args[0] != "coordinator" ? args[0] : args.Length > 1 ? args[1] : "all";
        return await new Coordinator().RunAsync(scenario);
    }
}

/// <summary>
/// Drives a primary/replica pair through a rolling upgrade and through the
/// failure paths of a planned switchover, under a continuous client write
/// load, and checks after each scenario that:
///  - every write a client got an acknowledgement for is on both nodes;
///  - both nodes hold byte-identical data;
///  - no epoch ever had two primaries, and no node's epoch went backwards.
/// </summary>
internal sealed class Coordinator
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "RocksDbSwitchoverTest_" + DateTime.UtcNow.Ticks);
    private readonly string _exePath;
    private readonly string _argsPrefix = "";
    private readonly List<NodeProcess> _processes = new();

    private readonly object _invariantGate = new();
    private readonly Dictionary<long, string> _primaryByEpoch = new();
    private readonly Dictionary<string, long> _highestEpochSeen = new();
    private readonly List<string> _violations = new();

    public Coordinator()
    {
        Directory.CreateDirectory(_tempRoot);
        _exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "dotnet";
        if (_exePath.EndsWith("dotnet") || _exePath.EndsWith("dotnet.exe"))
            _argsPrefix = $"\"{typeof(Coordinator).Assembly.Location}\" ";
    }

    public async Task<int> RunAsync(string scenario)
    {
        var scenarios = new Dictionary<string, Func<Task>>
        {
            ["upgrade"] = ScenarioRollingUpgradeAsync,
            ["abort"] = ScenarioAbortAsync,
            ["lost-reply"] = ScenarioLostReplyAsync,
            ["late-promote"] = ScenarioLatePromoteAsync,
            ["unreachable"] = ScenarioUnreachableAsync,
            ["force"] = ScenarioForcePromoteAsync,
        };
        Log($"temp dir: {_tempRoot}");
        try
        {
            if (scenario == "all")
            {
                foreach (var s in scenarios.Values) await s();
            }
            else if (scenarios.TryGetValue(scenario, out var run))
            {
                await run();
            }
            else
            {
                Console.Error.WriteLine($"Unknown scenario '{scenario}'. Use: {string.Join(" | ", scenarios.Keys)} | all");
                return 2;
            }
            Log("ALL SCENARIOS PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FAILED: {ex.Message}");
            return 1;
        }
        finally
        {
            KillAll();
        }
    }

    // -----------------------------------------------------------------------
    // Scenarios
    // -----------------------------------------------------------------------

    /// <summary>
    /// The deploy this is for: upgrade both nodes of a pair one at a time
    /// while clients keep writing. Hand the primary role to the replica,
    /// restart the old primary (on the new build), let it catch up from the
    /// WAL, hand the role back, restart the other node.
    /// </summary>
    private async Task ScenarioRollingUpgradeAsync()
    {
        Log("=== scenario: rolling upgrade (A -> B, restart A, B -> A, restart B) ===");
        var (a, b) = StartPair("upgrade", 52000);
        await WaitCaughtUpAsync(b, a);

        await using var load = new WriteLoad(a.Config, b.Config);
        await Task.Delay(2000);

        var r1 = await HandOffAsync(a, catchUpTimeoutMs: 5000);
        Expect(r1.Outcome == "Completed", $"A -> B handoff completed ({r1.Outcome}: {r1.Reason})");
        await WaitRoleAsync(b, ReplicationRole.Primary);
        await WaitRoleAsync(a, ReplicationRole.Replica);
        await Task.Delay(1000);
        Expect(load.AckedWithin(TimeSpan.FromMilliseconds(500)), "writes are flowing to B");

        a = await RestartAsync(a, "upgrade A", downFor: TimeSpan.FromSeconds(3));
        await WaitCaughtUpAsync(a, b);
        var aStatus = await GetStatusAsync(a);
        Expect(aStatus.Resyncs == 0, $"A rejoined from the WAL without a snapshot (attached from seq {aStatus.AttachedFromSeq:n0})");

        var r2 = await HandOffAsync(b, catchUpTimeoutMs: 5000);
        Expect(r2.Outcome == "Completed", $"B -> A handoff completed ({r2.Outcome}: {r2.Reason})");
        await WaitRoleAsync(a, ReplicationRole.Primary);

        b = await RestartAsync(b, "upgrade B", downFor: TimeSpan.FromSeconds(3));
        await WaitCaughtUpAsync(b, a);
        var bStatus = await GetStatusAsync(b);
        Expect(bStatus.Resyncs == 0, $"B rejoined from the WAL without a snapshot (attached from seq {bStatus.AttachedFromSeq:n0})");

        await load.StopAsync();
        Log($"client view: {load.Summary()}");
        Log($"writes held by the handoffs: A -> B {r1.WritesHeldMs:n1}ms, B -> A {r2.WritesHeldMs:n1}ms");
        await ValidateAsync(a, b, load);
        KillAll();
    }

    /// <summary>
    /// A replica that is too far behind: refused before writes are held, and
    /// - when the lag check is bypassed - aborted after the catch-up timeout,
    /// with the old primary resuming in a new epoch the replica then follows.
    /// </summary>
    private async Task ScenarioAbortAsync()
    {
        Log("=== scenario: lagging replica - rejected, then aborted, then completed ===");
        var (a, b) = StartPair("abort", 52010);
        await WaitCaughtUpAsync(b, a);
        await using var load = new WriteLoad(a.Config, b.Config);
        await Task.Delay(1000);

        await SetFaultsAsync(b, new FaultsDto { PauseIngest = true });
        await Task.Delay(1500);

        var rejected = await HandOffAsync(a, catchUpTimeoutMs: 1000, maxLagToStart: 100);
        Expect(rejected.Outcome == "Rejected", $"handoff to a lagging replica is rejected up front ({rejected.Outcome}: {rejected.Reason})");

        var aborted = await HandOffAsync(a, catchUpTimeoutMs: 1000, maxLagToStart: ulong.MaxValue / 2);
        Expect(aborted.Outcome == "Aborted", $"handoff past the lag check aborts after the catch-up timeout ({aborted.Outcome}: {aborted.Reason})");
        var aStatus = await GetStatusAsync(a);
        Expect(aStatus.RoleValue == ReplicationRole.Primary && aStatus.WritesOpen, $"A is primary again, in epoch {aStatus.Epoch}");

        await SetFaultsAsync(b, new FaultsDto { PauseIngest = false });
        await WaitCaughtUpAsync(b, a);
        var bStatus = await GetStatusAsync(b);
        Expect(bStatus.Epoch == aStatus.Epoch, $"B follows A in its new epoch {bStatus.Epoch}");

        var completed = await HandOffAsync(a, catchUpTimeoutMs: 5000);
        Expect(completed.Outcome == "Completed", $"handoff after catching up completes ({completed.Outcome}: {completed.Reason})");
        await WaitRoleAsync(a, ReplicationRole.Replica);

        await load.StopAsync();
        Log($"client view: {load.Summary()}");
        await ValidateAsync(a, b, load);
        KillAll();
    }

    /// <summary>The replica takes over but its answer never reaches the old primary.</summary>
    private async Task ScenarioLostReplyAsync()
    {
        Log("=== scenario: promote answer lost - old primary learns the outcome by asking ===");
        var (a, b) = StartPair("lost-reply", 52020);
        await WaitCaughtUpAsync(b, a);
        await using var load = new WriteLoad(a.Config, b.Config);
        await Task.Delay(1000);

        await SetFaultsAsync(b, new FaultsDto { DropNextPromoteReply = true });
        var r = await HandOffAsync(a, catchUpTimeoutMs: 3000);
        Expect(r.Outcome == "Completed", $"handoff completes although the answer was lost ({r.Outcome}: {r.Reason})");
        await WaitRoleAsync(b, ReplicationRole.Primary);
        await WaitRoleAsync(a, ReplicationRole.Replica);
        await Task.Delay(1000);

        await load.StopAsync();
        Log($"client view: {load.Summary()}");
        await ValidateAsync(a, b, load);
        KillAll();
    }

    /// <summary>
    /// The promote request is stuck on the wire past the old primary's
    /// deadline. The old primary has the replica promise the epoch and
    /// resumes; when the stale request finally lands it must be refused.
    /// </summary>
    private async Task ScenarioLatePromoteAsync()
    {
        Log("=== scenario: promote request delivered late - must not create a second primary ===");
        var (a, b) = StartPair("late-promote", 52030);
        await WaitCaughtUpAsync(b, a);
        await using var load = new WriteLoad(a.Config, b.Config);
        await Task.Delay(1000);

        await SetFaultsAsync(b, new FaultsDto { DelayNextPromoteMs = 6000 });
        // Deadline: 1s catch-up + 2s grace, well inside the 6s delay.
        var r = await HandOffAsync(a, catchUpTimeoutMs: 1000);
        Expect(r.Outcome == "Aborted", $"the old primary resumes ({r.Outcome}: {r.Reason})");

        await Task.Delay(6000); // the late request lands on B now
        var aStatus = await GetStatusAsync(a);
        var bStatus = await GetStatusAsync(b);
        Expect(aStatus.RoleValue == ReplicationRole.Primary, $"A is still primary (epoch {aStatus.Epoch})");
        Expect(bStatus.RoleValue == ReplicationRole.Replica, $"B refused the late promote and is a replica (epoch {bStatus.Epoch}, promised {bStatus.PromisedEpoch})");

        await WaitCaughtUpAsync(b, a);
        await load.StopAsync();
        Log($"client view: {load.Summary()}");
        await ValidateAsync(a, b, load);
        KillAll();
    }

    /// <summary>
    /// The replica freezes (SIGSTOP: a GC pause, a hung VM, a partition) after
    /// the promote request reached it. The old primary cannot tell whether it
    /// took over, so it must stop accepting writes rather than guess; once the
    /// replica is back, the handoff settles one way or the other.
    /// </summary>
    private async Task ScenarioUnreachableAsync()
    {
        Log("=== scenario: replica unreachable mid-handoff - writes stop until it is settled ===");
        var (a, b) = StartPair("unreachable", 52040);
        await WaitCaughtUpAsync(b, a);
        await using var load = new WriteLoad(a.Config, b.Config);
        await Task.Delay(1000);

        await SetFaultsAsync(b, new FaultsDto { DelayNextPromoteMs = 1500 });
        var handoff = HandOffAsync(a, catchUpTimeoutMs: 1000);
        // Freeze B only once the promote request has reached it - earlier,
        // and the handoff is refused before it starts.
        await WaitUntilAsync(async () => (await TryGetStatusAsync(b.Config))?.PromotesReceived > 0,
                             TimeSpan.FromSeconds(10), "B received the promote request");
        if (!SendUnixSignal(b.Process, 19))
        {
            Log("SIGSTOP unavailable on this OS; skipping");
            KillAll();
            return;
        }
        Log("froze B");

        var r = await handoff;
        Expect(r.Outcome == "Unresolved", $"the old primary does not guess ({r.Outcome}: {r.Reason})");
        var aStatus = await GetStatusAsync(a);
        Expect(!aStatus.WritesOpen, "A refuses writes while the handoff is unsettled");

        await Task.Delay(2000);
        SendUnixSignal(b.Process, 18);
        Log("resumed B");

        await WaitUntilAsync(async () =>
        {
            var sa = await TryGetStatusAsync(a.Config);
            var sb = await TryGetStatusAsync(b.Config);
            return sa != null && sb != null
                   && new[] { sa, sb }.Count(s => s.RoleValue == ReplicationRole.Primary && s.WritesOpen) == 1
                   && new[] { sa, sb }.Count(s => s.RoleValue == ReplicationRole.Replica) == 1;
        }, TimeSpan.FromSeconds(30), "the handoff settled with exactly one primary");
        var primary = (await GetStatusAsync(a)).RoleValue == ReplicationRole.Primary ? a : b;
        var replica = primary == a ? b : a;
        Log($"settled: {primary.Config.NodeId} is primary in epoch {(await GetStatusAsync(primary)).Epoch}");

        await WaitCaughtUpAsync(replica, primary);
        await load.StopAsync();
        Log($"client view: {load.Summary()}");
        await ValidateAsync(a, b, load);
        KillAll();
    }

    /// <summary>
    /// Not a planned switchover - the unplanned failover it is the
    /// counterpart of. The primary dies with writes the replica never
    /// received, the replica is promoted without it, and the old primary,
    /// restarted, must notice it was replaced, find its log diverged, and
    /// rebuild itself from a snapshot. The writes it acknowledged but never
    /// replicated are lost - which is the price of failing over a pair whose
    /// replication is asynchronous, and what a planned switchover avoids.
    /// </summary>
    private async Task ScenarioForcePromoteAsync()
    {
        Log("=== scenario: unplanned failover - primary dies, replica force-promoted, old primary rejoins ===");
        var (a, b) = StartPair("force", 52050);
        await WaitCaughtUpAsync(b, a);

        var before = new WriteLoad("before", a.Config, b.Config);
        await Task.Delay(1000);
        await SetFaultsAsync(b, new FaultsDto { PauseIngest = true });
        await Task.Delay(1000); // A acknowledges writes B never receives
        await before.StopAsync();

        Log("killing primary A");
        a.Process.Kill(entireProcessTree: true);
        await a.Process.WaitForExitAsync();
        _processes.Remove(a);

        // The operator's call: A is gone, take what B has.
        using (var ch = GrpcChannel.ForAddress(Endpoint(b.Config)))
        {
            long epoch = await MagicOnionClient.Create<IPairService>(ch).ForcePromoteAsync();
            Log($"force-promoted B in epoch {epoch}");
        }
        await SetFaultsAsync(b, new FaultsDto { PauseIngest = false });

        await using var after = new WriteLoad("after", b.Config, a.Config);
        await Task.Delay(1500);

        Log("starting A again");
        a = StartNodeProcess(a.Config);
        await WaitRoleAsync(a, ReplicationRole.Replica);
        await WaitCaughtUpAsync(a, b);
        var aStatus = await GetStatusAsync(a);
        Expect(aStatus.Resyncs == 1, $"A found its log diverged and rebuilt itself from a snapshot ({aStatus.Resyncs} resync)");
        await after.StopAsync();
        Log($"client view after the failover: {after.Summary()}");

        using (var ch = GrpcChannel.ForAddress(Endpoint(b.Config)))
        {
            var client = MagicOnionClient.Create<IPairService>(ch);
            int lost = 0;
            foreach (var key in before.AckedKeys)
            {
                if (!(await client.ReadAsync(key)).Found) lost++;
            }
            Log($"acknowledged before the failover: {before.AckedKeys.Count:n0}, lost by it: {lost:n0}");
            Expect(lost > 0, "the unreplicated writes A acknowledged are gone - an unplanned failover of an asynchronous pair loses them");
        }

        await ValidateAsync(a, b, after);
        KillAll();
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    private async Task ValidateAsync(NodeProcess a, NodeProcess b, WriteLoad load)
    {
        var sa = await GetStatusAsync(a);
        var sb = await GetStatusAsync(b);
        var (primary, replica) = sa.RoleValue == ReplicationRole.Primary ? (a, b) : (b, a);
        await WaitCaughtUpAsync(replica, primary);

        foreach (var node in new[] { a, b })
        {
            using var ch = GrpcChannel.ForAddress(Endpoint(node.Config));
            var client = MagicOnionClient.Create<IPairService>(ch);
            var missing = new List<string>();
            foreach (var key in load.AckedKeys)
            {
                if (!(await client.ReadAsync(key)).Found) missing.Add(key);
            }
            Expect(missing.Count == 0, $"all {load.AckedKeys.Count:n0} acknowledged writes are on {node.Config.NodeId}" +
                                       (missing.Count > 0 ? $" - {missing.Count} missing, e.g. {missing[0]}" : ""));
        }

        var sums = new List<(string Node, ChecksumDto Sum)>();
        foreach (var node in new[] { a, b })
        {
            using var ch = GrpcChannel.ForAddress(Endpoint(node.Config));
            sums.Add((node.Config.NodeId, await MagicOnionClient.Create<IPairService>(ch).ChecksumAsync()));
        }
        Expect(sums.All(s => s.Sum.Available) && sums[0].Sum.Hash == sums[1].Sum.Hash && sums[0].Sum.KeyCount == sums[1].Sum.KeyCount,
               "both nodes hold identical data: " + string.Join(", ", sums.Select(s => $"{s.Node} {s.Sum.KeyCount:n0} keys / {s.Sum.Hash:x16} @ {s.Sum.LatestSeq:n0}")));

        lock (_invariantGate)
        {
            Expect(_violations.Count == 0, "no epoch had two primaries, no epoch went backwards" +
                                           (_violations.Count > 0 ? ":\n  " + string.Join("\n  ", _violations.Distinct()) : ""));
        }
    }

    private void CheckInvariants(StatusDto s)
    {
        lock (_invariantGate)
        {
            if (_highestEpochSeen.TryGetValue(s.NodeId, out var prev) && s.Epoch < prev)
                _violations.Add($"epoch went backwards on {s.NodeId}: {prev} -> {s.Epoch}");
            _highestEpochSeen[s.NodeId] = Math.Max(prev, s.Epoch);

            if (s.RoleValue == ReplicationRole.Primary)
            {
                if (_primaryByEpoch.TryGetValue(s.Epoch, out var other) && other != s.NodeId)
                    _violations.Add($"SPLIT BRAIN: two primaries in epoch {s.Epoch}: {other} and {s.NodeId}");
                _primaryByEpoch[s.Epoch] = s.NodeId;
            }
        }
    }

    private static void Expect(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
        Log($"ok: {what}");
    }

    // -----------------------------------------------------------------------
    // Pair helpers
    // -----------------------------------------------------------------------

    private (NodeProcess A, NodeProcess B) StartPair(string scenario, int basePort)
    {
        lock (_invariantGate)
        {
            _primaryByEpoch.Clear();
            _highestEpochSeen.Clear();
            _violations.Clear();
        }
        var a = new NodeConfiguration { NodeId = "A", Port = basePort, DbPath = Path.Combine(_tempRoot, scenario, "A"), InitialRole = "primary", PeerId = "B", PeerEndpoint = $"http://localhost:{basePort + 1}" };
        var b = new NodeConfiguration { NodeId = "B", Port = basePort + 1, DbPath = Path.Combine(_tempRoot, scenario, "B"), InitialRole = "replica", PeerId = "A", PeerEndpoint = $"http://localhost:{basePort}" };
        var pa = StartNodeProcess(a);
        Thread.Sleep(1000);
        var pb = StartNodeProcess(b);
        return (pa, pb);
    }

    private async Task<NodeProcess> RestartAsync(NodeProcess node, string why, TimeSpan downFor)
    {
        Log($"{why}: stopping {node.Config.NodeId} (pid {node.Process.Id})");
        node.Process.Kill(entireProcessTree: true);
        await node.Process.WaitForExitAsync();
        _processes.Remove(node);
        await Task.Delay(downFor);
        Log($"{why}: starting {node.Config.NodeId} again");
        return StartNodeProcess(node.Config);
    }

    private async Task<HandOffResponseDto> HandOffAsync(NodeProcess from, int catchUpTimeoutMs, ulong maxLagToStart = 50_000)
    {
        Log($"asking {from.Config.NodeId} to hand off (catch-up timeout {catchUpTimeoutMs}ms)");
        using var ch = GrpcChannel.ForAddress(Endpoint(from.Config));
        var r = await MagicOnionClient.Create<IPairService>(ch).HandOffAsync(catchUpTimeoutMs, maxLagToStart);
        Log($"handoff: {r.Outcome}, epoch {r.Epoch}, handoff seq {r.HandoffSeq:n0}, writes held {r.WritesHeldMs:n1}ms{(r.Reason != null ? " - " + r.Reason : "")}");
        return r;
    }

    private async Task SetFaultsAsync(NodeProcess node, FaultsDto faults)
    {
        using var ch = GrpcChannel.ForAddress(Endpoint(node.Config));
        await MagicOnionClient.Create<IPairService>(ch).SetFaultsAsync(faults);
    }

    private async Task WaitRoleAsync(NodeProcess node, ReplicationRole role)
    {
        await WaitUntilAsync(async () => (await TryGetStatusAsync(node.Config))?.RoleValue == role,
                             TimeSpan.FromSeconds(20), $"{node.Config.NodeId} is {role}");
    }

    /// <summary>The replica follows the primary and is within a few hundred entries of it.</summary>
    private async Task WaitCaughtUpAsync(NodeProcess replica, NodeProcess primary)
    {
        await WaitUntilAsync(async () =>
        {
            var r = await TryGetStatusAsync(replica.Config);
            var p = await TryGetStatusAsync(primary.Config);
            return r is { Resyncing: false } && p != null
                   && r.RoleValue == ReplicationRole.Replica && r.PrimaryId == p.NodeId
                   && r.LatestSeq + 500 >= p.LatestSeq;
        }, TimeSpan.FromSeconds(60), $"{replica.Config.NodeId} caught up with {primary.Config.NodeId}");
    }

    private async Task<StatusDto> GetStatusAsync(NodeProcess node) =>
        await TryGetStatusAsync(node.Config) ?? throw new InvalidOperationException($"{node.Config.NodeId} is unreachable");

    private async Task<StatusDto?> TryGetStatusAsync(NodeConfiguration cfg)
    {
        try
        {
            using var ch = GrpcChannel.ForAddress(Endpoint(cfg));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var s = await MagicOnionClient.Create<IPairService>(ch).WithCancellationToken(timeout.Token).GetStatusAsync();
            CheckInvariants(s);
            return s;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> check, TimeSpan timeout, string label)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try { if (await check()) return; } catch { }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Timeout waiting for: {label}");
    }

    internal static string Endpoint(NodeConfiguration cfg) => $"http://localhost:{cfg.Port}";

    private NodeProcess StartNodeProcess(NodeConfiguration cfg)
    {
        Directory.CreateDirectory(cfg.DbPath);
        string cfgPath = Path.Combine(cfg.DbPath, "node.json");
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg));
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = $"{_argsPrefix}node \"{cfgPath}\"",
            UseShellExecute = false,
        })!;
        var np = new NodeProcess(cfg, proc);
        _processes.Add(np);
        Log($"started {cfg.NodeId} pid={proc.Id} on port {cfg.Port}");
        return np;
    }

    private void KillAll()
    {
        foreach (var p in _processes)
        {
            try { SendUnixSignal(p.Process, 18); if (!p.Process.HasExited) p.Process.Kill(entireProcessTree: true); } catch { }
        }
        _processes.Clear();
    }

    private static bool SendUnixSignal(Process p, int signal)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        try { return NativeKill(p.Id, signal) == 0; } catch { return false; }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int sig);

    internal static void Log(string msg) =>
        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss.fff} coordinator] {msg}");

    private sealed class NodeProcess
    {
        public NodeConfiguration Config { get; }
        public Process Process { get; }
        public NodeProcess(NodeConfiguration cfg, Process p) { Config = cfg; Process = p; }
    }
}

/// <summary>
/// Clients writing as fast as they can, the way an application would: send
/// to the node believed to be primary, follow a redirect hint, try the other
/// node on an error. Records every acknowledged key and the longest stretch
/// without any acknowledgement - the write unavailability a switchover costs.
/// </summary>
internal sealed class WriteLoad : IAsyncDisposable
{
    private const int Writers = 4;

    private readonly NodeConfiguration[] _nodes;
    private readonly string _keyPrefix;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _tasks;
    private readonly ConcurrentQueue<string> _acked = new();
    private long _lastAckTicks = Stopwatch.GetTimestamp();
    private long _longestGapTicks;
    private long _redirects;
    private long _errors;
    private bool _stopped;

    public WriteLoad(params NodeConfiguration[] nodes) : this("w", nodes) { }

    public WriteLoad(string keyPrefix, params NodeConfiguration[] nodes)
    {
        _keyPrefix = keyPrefix;
        _nodes = nodes;
        _tasks = Enumerable.Range(0, Writers).Select(i => Task.Run(() => RunAsync(i))).ToArray();
    }

    public IReadOnlyCollection<string> AckedKeys => _acked;

    public bool AckedWithin(TimeSpan window) =>
        (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastAckTicks)) * 1000.0 / Stopwatch.Frequency < window.TotalMilliseconds;

    public string Summary() =>
        $"{_acked.Count:n0} writes acknowledged, {Interlocked.Read(ref _redirects):n0} redirects, {Interlocked.Read(ref _errors):n0} errors, " +
        $"longest gap without an acknowledged write {Interlocked.Read(ref _longestGapTicks) * 1000.0 / Stopwatch.Frequency:n1}ms";

    private async Task RunAsync(int writer)
    {
        var channels = _nodes.Select(n => GrpcChannel.ForAddress(Coordinator.Endpoint(n))).ToArray();
        var clients = channels.Select(c => MagicOnionClient.Create<IPairService>(c)).ToArray();
        int target = 0;
        long n = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                string key = $"{_keyPrefix}{writer}_{n:D9}";
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var r = await clients[target].WithCancellationToken(timeout.Token).WriteAsync(key, n.ToString());
                    if (r.Ok)
                    {
                        _acked.Enqueue(key);
                        n++;
                        long now = Stopwatch.GetTimestamp();
                        long gap = now - Interlocked.Exchange(ref _lastAckTicks, now);
                        long longest;
                        while (gap > (longest = Interlocked.Read(ref _longestGapTicks)) &&
                               Interlocked.CompareExchange(ref _longestGapTicks, gap, longest) != longest) { }
                        continue;
                    }
                    Interlocked.Increment(ref _redirects);
                    int hinted = Array.FindIndex(_nodes, x => x.NodeId == r.PrimaryHint);
                    target = hinted >= 0 ? hinted : (target + 1) % _nodes.Length;
                }
                catch
                {
                    Interlocked.Increment(ref _errors);
                    target = (target + 1) % _nodes.Length;
                }
                // The key is retried: a failed write was not acknowledged, so
                // it may or may not exist - which the validation tolerates,
                // since it only checks acknowledged keys.
                await Task.Delay(5);
            }
        }
        finally
        {
            foreach (var c in channels) c.Dispose();
        }
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        _stop.Cancel();
        await Task.WhenAll(_tasks);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
