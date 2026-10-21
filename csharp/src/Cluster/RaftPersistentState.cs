#if NET5_0_OR_GREATER
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace RocksDbSharp;

/// <summary>
/// Persists the bits of Raft state that must survive a process crash:
///  - currentTerm
///  - votedFor (in the current term)
///  - clusterMode (Bootstrap vs Cluster)
///  - the per-seqNo term log (so a recovering follower can answer
///    "what was the term of log entry X?")
///
/// Everything is written through a temp-file that is flushed to disk and then
/// renamed over the target, so a crash mid-write cannot leave us with corrupt
/// state and an acknowledged vote/term can never be rolled back by power loss.
/// </summary>
public sealed class RaftPersistentState
{
    private readonly string _statePath;
    private readonly string _termsPath;
    private readonly object _gate = new();

    private long _currentTerm;
    private string? _votedFor;
    private int _mode; // ClusterMode
    private RaftLogTerms _terms;

    public RaftPersistentState(string directory)
    {
        Directory.CreateDirectory(directory);
        _statePath = Path.Combine(directory, "raft.state");
        _termsPath = Path.Combine(directory, "raft.terms");
        _terms = new RaftLogTerms();
        Load();
    }

    public long CurrentTerm => Interlocked.Read(ref _currentTerm);

    public string? VotedFor
    {
        get { lock (_gate) return _votedFor; }
    }

    public ClusterMode Mode
    {
        get => (ClusterMode)Volatile.Read(ref _mode);
    }

    public RaftLogTerms Terms
    {
        get { lock (_gate) return _terms; }
    }

    public void SetMode(ClusterMode mode)
    {
        lock (_gate)
        {
            _mode = (int)mode;
            Persist();
        }
    }

    /// <summary>
    /// Atomically updates currentTerm and votedFor. If the new term is higher
    /// than the recorded term, votedFor is cleared unless a vote is being cast
    /// for this same term.
    /// </summary>
    public void UpdateTermAndVote(long newTerm, string? newVotedFor)
    {
        lock (_gate)
        {
            if (newTerm > _currentTerm)
            {
                _currentTerm = newTerm;
                _votedFor = newVotedFor;
            }
            else if (newTerm == _currentTerm && newVotedFor != null && _votedFor == null)
            {
                _votedFor = newVotedFor;
            }
            else
            {
                return;
            }
            Persist();
        }
    }

    /// <summary>
    /// Record the term of a log range starting at <paramref name="startSeq"/>.
    /// Persists immediately if the map changed.
    /// </summary>
    public void RecordTermRange(ulong startSeq, long term)
    {
        lock (_gate)
        {
            if (_terms.Record(startSeq, term))
                AtomicWrite(_termsPath, _terms.Serialize());
        }
    }

    /// <summary>
    /// Replaces the whole term map. Used after a snapshot restore, when the
    /// local log is byte-for-byte the leader's log so the leader's term map is
    /// the correct description of it.
    /// </summary>
    public void ReplaceTerms(RaftLogTerms terms)
    {
        lock (_gate)
        {
            _terms = terms ?? throw new ArgumentNullException(nameof(terms));
            AtomicWrite(_termsPath, _terms.Serialize());
        }
    }

    private void Persist()
    {
        var sb = new StringBuilder();
        sb.Append(_currentTerm).Append('\n');
        sb.Append(_votedFor ?? string.Empty).Append('\n');
        sb.Append(_mode).Append('\n');
        AtomicWrite(_statePath, sb.ToString());
    }

    internal static void AtomicWrite(string path, string content)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
            // Raft safety depends on term/vote surviving a crash *before* the
            // RPC reply leaves this node, so force the data to disk.
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private void Load()
    {
        if (File.Exists(_statePath))
        {
            try
            {
                var lines = File.ReadAllLines(_statePath);
                if (lines.Length >= 1 && long.TryParse(lines[0], out var t)) _currentTerm = t;
                if (lines.Length >= 2) _votedFor = string.IsNullOrEmpty(lines[1]) ? null : lines[1];
                if (lines.Length >= 3 && int.TryParse(lines[2], out var m)) _mode = m;
            }
            catch
            {
                // corrupt file; start fresh
                _currentTerm = 0;
                _votedFor = null;
                _mode = 0;
            }
        }

        if (File.Exists(_termsPath))
        {
            try
            {
                _terms = RaftLogTerms.Deserialize(File.ReadAllText(_termsPath));
            }
            catch
            {
                _terms = new RaftLogTerms();
            }
        }
    }
}
#endif
