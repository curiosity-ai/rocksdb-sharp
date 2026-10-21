#if NET5_0_OR_GREATER
#nullable enable
using System;
using System.IO;
using System.Text;

namespace RocksDbSharp;

/// <summary>
/// The role a node of a primary/replica pair holds. Only a <see cref="Primary"/>
/// accepts writes.
/// </summary>
public enum ReplicationRole
{
    Replica = 0,
    Primary = 1,

    /// <summary>
    /// The node was the primary and has started handing the role to its
    /// replica: writes are held, and whether the replica took over is not
    /// settled yet. A node that restarts in this role stays read-only until
    /// it has asked the other node how the handoff ended.
    /// </summary>
    HandingOff = 2,
}

/// <summary>
/// Crash-safe role state of one node in a primary/replica pair, the
/// switchover counterpart of <see cref="RaftPersistentState"/>:
///  - the epoch: the epoch of the primary this node is or follows. It grows
///    on every change of primary, and each epoch belongs to at most one
///    primary ever (the equivalent of a Raft term);
///  - the promised epoch: the highest epoch this node has been asked to take
///    over in. Like a Raft vote, it is persisted before the node acts on the
///    request, and no promotion to an epoch at or below it is accepted again;
///  - the node's role, and which node it believes is the primary;
///  - for a node in <see cref="ReplicationRole.HandingOff"/>, the node it is
///    handing off to;
///  - the epoch map: the WAL sequence number each epoch's writes start at,
///    which is what lets a node rejoining the pair prove its log is a prefix
///    of the primary's.
///
/// Everything is written through a temp file that is flushed to disk and
/// renamed over the target, so the role a node promised before answering an
/// RPC survives a crash.
/// </summary>
public sealed class ReplicationRoleState
{
    private readonly string _statePath;
    private readonly string _epochsPath;
    private readonly object _gate = new();

    private long _epoch;
    private long _promisedEpoch;
    private ReplicationRole _role;
    private string? _primaryId;
    private string? _handoffTarget;
    private RaftLogTerms _epochs = new();

    public ReplicationRoleState(string directory)
    {
        Directory.CreateDirectory(directory);
        _statePath = Path.Combine(directory, "role.state");
        _epochsPath = Path.Combine(directory, "role.epochs");
        IsFresh = !File.Exists(_statePath);
        Load();
    }

    /// <summary>True when no state had been written before this instance was created.</summary>
    public bool IsFresh { get; }

    public long Epoch { get { lock (_gate) return _epoch; } }
    public long PromisedEpoch { get { lock (_gate) return Math.Max(_epoch, _promisedEpoch); } }
    public ReplicationRole Role { get { lock (_gate) return _role; } }
    public string? PrimaryId { get { lock (_gate) return _primaryId; } }
    public string? HandoffTarget { get { lock (_gate) return _handoffTarget; } }

    public long GetEpochForSeq(ulong seq)
    {
        lock (_gate) return _epochs.GetTermForSeq(seq);
    }

    public string SerializeEpochs()
    {
        lock (_gate) return _epochs.Serialize();
    }

    /// <summary>
    /// Sets the role state and persists it. The epoch may never go backwards:
    /// an epoch a node has seen is a promise not to accept anything older.
    /// </summary>
    public void Set(long epoch, ReplicationRole role, string? primaryId, string? handoffTarget = null)
    {
        lock (_gate)
        {
            if (epoch < _epoch)
                throw new InvalidOperationException($"epoch may not go backwards ({_epoch} -> {epoch})");
            _epoch = epoch;
            _role = role;
            _primaryId = primaryId;
            _handoffTarget = handoffTarget;
            Persist();
        }
    }

    /// <summary>
    /// Persists that no promotion to <paramref name="epoch"/> or below may be
    /// accepted from now on. Returns false when an equal or higher epoch was
    /// already promised.
    /// </summary>
    public bool Promise(long epoch)
    {
        lock (_gate)
        {
            if (epoch <= Math.Max(_epoch, _promisedEpoch)) return false;
            _promisedEpoch = epoch;
            Persist();
            return true;
        }
    }

    /// <summary>
    /// Records that this node's own writes from <paramref name="startSeq"/> on
    /// belong to <paramref name="epoch"/>, for a node becoming primary. Ranges
    /// starting at or after <paramref name="startSeq"/> are dropped first: a
    /// replica may have learned them from a primary whose entries it never
    /// received, and its own writes take those sequence numbers now. Left in
    /// place they would make <see cref="RaftLogTerms.Record"/> ignore the new
    /// epoch, and a diverged replica pass the attach check.
    /// </summary>
    public void StartEpoch(ulong startSeq, long epoch)
    {
        lock (_gate)
        {
            var kept = new RaftLogTerms();
            foreach (var (start, e) in _epochs.GetRanges())
            {
                if (start < startSeq) kept.Record(start, e);
            }
            if (!kept.Record(startSeq, epoch))
                throw new InvalidOperationException($"epoch {epoch} is not newer than the epochs already recorded");
            _epochs = kept;
            RaftPersistentState.AtomicWrite(_epochsPath, _epochs.Serialize());
        }
    }

    /// <summary>
    /// Adds the ranges of another node's epoch map in order. Ranges this map
    /// already covers are ignored, so this is how a replica that passed the
    /// attach check picks up the epochs it is about to receive entries for.
    /// </summary>
    public void MergeEpochs(string serialized)
    {
        var other = RaftLogTerms.Deserialize(serialized);
        lock (_gate)
        {
            bool changed = false;
            foreach (var (startSeq, epoch) in other.GetRanges())
                changed |= _epochs.Record(startSeq, epoch);
            if (changed) RaftPersistentState.AtomicWrite(_epochsPath, _epochs.Serialize());
        }
    }

    /// <summary>
    /// Replaces the epoch map wholesale. Used after a snapshot restore, when
    /// the local log is byte-for-byte the primary's.
    /// </summary>
    public void ReplaceEpochs(string serialized)
    {
        lock (_gate)
        {
            _epochs = RaftLogTerms.Deserialize(serialized);
            RaftPersistentState.AtomicWrite(_epochsPath, _epochs.Serialize());
        }
    }

    private void Persist()
    {
        var sb = new StringBuilder();
        sb.Append(_epoch).Append('\n');
        sb.Append((int)_role).Append('\n');
        sb.Append(_primaryId ?? string.Empty).Append('\n');
        sb.Append(_handoffTarget ?? string.Empty).Append('\n');
        sb.Append(_promisedEpoch).Append('\n');
        RaftPersistentState.AtomicWrite(_statePath, sb.ToString());
    }

    private void Load()
    {
        if (File.Exists(_statePath))
        {
            var lines = File.ReadAllLines(_statePath);
            if (lines.Length >= 1 && long.TryParse(lines[0], out var e)) _epoch = e;
            if (lines.Length >= 2 && int.TryParse(lines[1], out var r)) _role = (ReplicationRole)r;
            if (lines.Length >= 3) _primaryId = string.IsNullOrEmpty(lines[2]) ? null : lines[2];
            if (lines.Length >= 4) _handoffTarget = string.IsNullOrEmpty(lines[3]) ? null : lines[3];
            if (lines.Length >= 5 && long.TryParse(lines[4], out var p)) _promisedEpoch = p;
        }
        if (File.Exists(_epochsPath))
        {
            _epochs = RaftLogTerms.Deserialize(File.ReadAllText(_epochsPath));
        }
    }
}
#endif
