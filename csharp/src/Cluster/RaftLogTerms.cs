#if NET5_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RocksDbSharp;

/// <summary>
/// Sparse map of "log entry term" for the WAL. Each new term records the
/// starting WAL sequence number at which it became active. To look up the
/// term of any sequence number we binary-search for the latest range that
/// starts at or before it.
/// </summary>
public sealed class RaftLogTerms
{
    // sorted by StartSeq ascending
    private readonly List<(ulong StartSeq, long Term)> _ranges = new();
    private readonly object _gate = new();

    /// <summary>
    /// Records that entries from <paramref name="startSeq"/> onwards belong to
    /// <paramref name="term"/>. Returns true if the map changed. Stale or
    /// out-of-order records are ignored rather than thrown on, because the
    /// WAL-stream consumer may legitimately re-observe old batches after a
    /// reconnect.
    /// </summary>
    public bool Record(ulong startSeq, long term)
    {
        lock (_gate)
        {
            if (_ranges.Count > 0)
            {
                var last = _ranges[^1];
                // We only record the first sequence number of each term.
                if (term <= last.Term) return false;
                if (startSeq < last.StartSeq) return false;
                if (startSeq == last.StartSeq)
                {
                    // The previous term produced no entries (its range starts
                    // beyond the log tail); the new term supersedes it.
                    _ranges[^1] = (startSeq, term);
                    return true;
                }
            }
            _ranges.Add((startSeq, term));
            return true;
        }
    }

    /// <summary>
    /// Returns the term assigned to the WAL entry at <paramref name="seq"/>.
    /// Returns 0 if there are no recorded ranges or seq is before the first.
    /// </summary>
    public long GetTermForSeq(ulong seq)
    {
        lock (_gate)
        {
            if (_ranges.Count == 0) return 0;
            // binary search for the largest StartSeq <= seq
            int lo = 0, hi = _ranges.Count - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_ranges[mid].StartSeq <= seq)
                {
                    idx = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return idx < 0 ? 0 : _ranges[idx].Term;
        }
    }

    public (ulong StartSeq, long Term) GetLast()
    {
        lock (_gate)
        {
            return _ranges.Count == 0 ? (0UL, 0L) : _ranges[^1];
        }
    }

    /// <summary>A copy of the recorded ranges, oldest first.</summary>
    public IReadOnlyList<(ulong StartSeq, long Term)> GetRanges()
    {
        lock (_gate) return _ranges.ToList();
    }

    public string Serialize()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            foreach (var (s, t) in _ranges) sb.Append(s).Append(',').Append(t).Append('\n');
            return sb.ToString();
        }
    }

    public static RaftLogTerms Deserialize(string content)
    {
        var t = new RaftLogTerms();
        if (string.IsNullOrEmpty(content)) return t;
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length != 2) continue;
            if (ulong.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) &&
                long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var term))
            {
                t._ranges.Add((s, term));
            }
        }
        return t;
    }
}
#endif
