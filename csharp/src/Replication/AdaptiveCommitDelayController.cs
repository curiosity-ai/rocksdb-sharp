#if NET5_0_OR_GREATER

namespace RocksDbSharp;


using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public sealed class ReplicaLagSample
{
    public int ReplicaIndex { get; }
    public long LagVersions { get; }
    public DateTime TimestampUtc { get; }

    public ReplicaLagSample(int replicaIndex, long lagVersions, DateTime? timestampUtc = null)
    {
        ReplicaIndex = replicaIndex;
        LagVersions = Math.Max(0, lagVersions);
        TimestampUtc = timestampUtc ?? DateTime.UtcNow;
    }
}

public sealed record CommitDelaySnapshot(
    long CurrentLag,
    double AverageLag,
    double EmaLag,
    double Trend,
    int RecommendedDelayMs,
    IReadOnlyList<long> History,
    IReadOnlyList<long> ReplicaLags);

public sealed class AdaptiveCommitDelayController
{
    private readonly int _historySize;
    private readonly double _emaAlpha;
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;
    private readonly double _delayPerLagUnitMs;
    private readonly double _trendWeight;
    private readonly double _burstWeight;

    // Per-replica latest lag; one slot per replica.
    private readonly long[] _latestReplicaLag;

    // Ring buffer of recent cluster lag values.
    private readonly long[] _history;

    // Monotonic write counter. Each report claims one slot.
    private long _writeSequence;

    // Number of valid items currently in ring buffer (<= _historySize).
    private long _historyCount;

    // Rolling sum of values currently inside ring buffer.
    private long _historySum;

    // EMA stored as bits so it can be read/written atomically with Interlocked.
    private long _emaBits;

    public AdaptiveCommitDelayController(
        int replicaCount,
        int historySize = 20,
        double emaAlpha = 0.25,
        int minDelayMs = 0,
        int maxDelayMs = 2000,
        double delayPerLagUnitMs = 5.0,
        double trendWeight = 2.0,
        double burstWeight = 1.5)
    {
        if (replicaCount <= 0) throw new ArgumentOutOfRangeException(nameof(replicaCount));
        if (historySize <= 0) throw new ArgumentOutOfRangeException(nameof(historySize));
        if (emaAlpha <= 0 || emaAlpha > 1) throw new ArgumentOutOfRangeException(nameof(emaAlpha));
        if (minDelayMs < 0) throw new ArgumentOutOfRangeException(nameof(minDelayMs));
        if (maxDelayMs < minDelayMs) throw new ArgumentOutOfRangeException(nameof(maxDelayMs));
        if (delayPerLagUnitMs < 0) throw new ArgumentOutOfRangeException(nameof(delayPerLagUnitMs));
        if (trendWeight < 0) throw new ArgumentOutOfRangeException(nameof(trendWeight));
        if (burstWeight < 0) throw new ArgumentOutOfRangeException(nameof(burstWeight));

        _latestReplicaLag = new long[replicaCount];
        _history = new long[historySize];
        _historySize = historySize;
        _emaAlpha = emaAlpha;
        _minDelayMs = minDelayMs;
        _maxDelayMs = maxDelayMs;
        _delayPerLagUnitMs = delayPerLagUnitMs;
        _trendWeight = trendWeight;
        _burstWeight = burstWeight;

        Interlocked.Exchange(ref _emaBits, BitConverter.DoubleToInt64Bits(0.0));
    }

    public void ReportLag(ReplicaLagSample sample)
    {
        if (sample == null) throw new ArgumentNullException(nameof(sample));
        if ((uint)sample.ReplicaIndex >= (uint)_latestReplicaLag.Length)
            throw new ArgumentOutOfRangeException(nameof(sample.ReplicaIndex));

        // Update this replica's latest lag atomically.
        Interlocked.Exchange(ref _latestReplicaLag[sample.ReplicaIndex], sample.LagVersions);

        // Compute cluster lag as current max across replicas.
        long clusterLag = ReadMaxReplicaLag();

        // Claim next slot in ring buffer.
        long sequence = Interlocked.Increment(ref _writeSequence) - 1;
        int slot = (int)(sequence % _historySize);

        // Replace old value in slot with new cluster lag.
        long oldValue = Interlocked.Exchange(ref _history[slot], clusterLag);

        // Track count until full.
        long priorCount;
        do
        {
            priorCount = Volatile.Read(ref _historyCount);
            if (priorCount >= _historySize)
                break;
        }
        while (Interlocked.CompareExchange(ref _historyCount, priorCount + 1, priorCount) != priorCount);

        bool overwriting = sequence >= _historySize;

        if (overwriting)
        {
            Interlocked.Add(ref _historySum, clusterLag - oldValue);
        }
        else
        {
            Interlocked.Add(ref _historySum, clusterLag);
        }

        UpdateEma(clusterLag);
    }

    public int GetRecommendedDelayMs()
    {
        long currentLag = ReadCurrentLag();
        long count = Math.Min(Volatile.Read(ref _historyCount), _historySize);

        if (count == 0)
            return _minDelayMs;

        double averageLag = (double)Volatile.Read(ref _historySum) / count;
        double emaLag = ReadEma();
        double trend = ComputeTrendSnapshot(count);
        double burst = Math.Max(0, currentLag - averageLag);

        double score =
            emaLag +
            (_trendWeight * Math.Max(0, trend)) +
            (_burstWeight * burst);

        int delay = (int)Math.Round(score * _delayPerLagUnitMs);
        return Math.Clamp(delay, _minDelayMs, _maxDelayMs);
    }

    public Task DelayIfNeededAsync(CancellationToken cancellationToken = default)
    {
        int delayMs = GetRecommendedDelayMs();
        return delayMs <= 0 ? Task.CompletedTask : Task.Delay(delayMs, cancellationToken);
    }

    public CommitDelaySnapshot GetSnapshot()
    {
        long count = Math.Min(Volatile.Read(ref _historyCount), _historySize);
        long currentLag = ReadCurrentLag();
        double averageLag = count == 0 ? 0 : (double)Volatile.Read(ref _historySum) / count;
        double emaLag = ReadEma();
        double trend = ComputeTrendSnapshot(count);
        int recommended = GetRecommendedDelayMs();

        long[] history = ReadHistorySnapshot(count);
        long[] replicaLags = ReadReplicaLagSnapshot();

        return new CommitDelaySnapshot(
            CurrentLag: currentLag,
            AverageLag: averageLag,
            EmaLag: emaLag,
            Trend: trend,
            RecommendedDelayMs: recommended,
            History: history,
            ReplicaLags: replicaLags);
    }

    private long ReadMaxReplicaLag()
    {
        long max = 0;
        for (int i = 0; i < _latestReplicaLag.Length; i++)
        {
            long value = Volatile.Read(ref _latestReplicaLag[i]);
            if (value > max)
                max = value;
        }
        return max;
    }

    private long ReadCurrentLag()
    {
        long sequence = Volatile.Read(ref _writeSequence);
        if (sequence <= 0)
            return 0;

        int slot = (int)((sequence - 1) % _historySize);
        return Volatile.Read(ref _history[slot]);
    }

    private void UpdateEma(long clusterLag)
    {
        while (true)
        {
            long oldBits = Volatile.Read(ref _emaBits);
            double oldEma = BitConverter.Int64BitsToDouble(oldBits);
            double newEma = oldEma == 0.0
                ? clusterLag
                : (_emaAlpha * clusterLag) + ((1.0 - _emaAlpha) * oldEma);

            long newBits = BitConverter.DoubleToInt64Bits(newEma);

            if (Interlocked.CompareExchange(ref _emaBits, newBits, oldBits) == oldBits)
                return;
        }
    }

    private double ReadEma()
    {
        long bits = Volatile.Read(ref _emaBits);
        return BitConverter.Int64BitsToDouble(bits);
    }

    private double ComputeTrendSnapshot(long count)
    {
        if (count < 2)
            return 0;

        long[] history = ReadHistorySnapshot(count);
        if (history.Length < 2)
            return 0;

        double totalDelta = 0;
        for (int i = 1; i < history.Length; i++)
        {
            totalDelta += history[i] - history[i - 1];
        }

        return totalDelta / (history.Length - 1);
    }

    private long[] ReadHistorySnapshot(long count)
    {
        if (count <= 0)
            return Array.Empty<long>();

        count = Math.Min(count, _historySize);

        long sequence = Volatile.Read(ref _writeSequence);
        long start = Math.Max(0, sequence - count);

        long[] snapshot = new long[count];
        for (long i = 0; i < count; i++)
        {
            int slot = (int)((start + i) % _historySize);
            snapshot[i] = Volatile.Read(ref _history[slot]);
        }

        return snapshot;
    }

    private long[] ReadReplicaLagSnapshot()
    {
        var result = new long[_latestReplicaLag.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Volatile.Read(ref _latestReplicaLag[i]);
        }
        return result;
    }
}
#endif