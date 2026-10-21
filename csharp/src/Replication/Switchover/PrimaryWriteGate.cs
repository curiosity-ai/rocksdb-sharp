#if NET5_0_OR_GREATER
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RocksDbSharp;

/// <summary>
/// Admission control for client writes on a primary, with the one property a
/// planned switchover needs: it can stop admitting writes and then tell when
/// every write it already admitted has reached the database, so the sequence
/// number read after <see cref="HoldAsync"/> is final.
///
/// Three states:
///  - <b>Open</b>: writes are admitted.
///  - <b>Holding</b>: new writes wait (up to the caller's timeout) instead of
///    failing, because the gate is about to either reopen - a switchover that
///    aborts - or close.
///  - <b>Closed</b>: writes are refused; the caller should redirect them to
///    the primary.
///
/// The hot path is one interlocked increment and one read. Admission and
/// holding form a Dekker pair: a writer increments the in-flight count before
/// it reads the state, and the switchover sets the state before it reads the
/// count, both through full fences - so either the writer sees the hold, or
/// the drain sees the writer.
/// </summary>
public sealed class PrimaryWriteGate
{
    private const int Closed = 0;
    private const int Open = 1;
    private const int Holding = 2;

    private int _state;
    private int _inFlight;
    private TaskCompletionSource<bool> _changed = NewSignal();
    private readonly object _transition = new();

    public PrimaryWriteGate(bool open = false)
    {
        _state = open ? Open : Closed;
    }

    public bool IsOpen => Volatile.Read(ref _state) == Open;
    public bool IsHolding => Volatile.Read(ref _state) == Holding;
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Admits one write. Returns true when the write may proceed - the caller
    /// must then call <see cref="Exit"/> once it has been applied. Returns
    /// false when the gate is closed, or stayed held for longer than
    /// <paramref name="holdTimeout"/>.
    /// </summary>
    public async ValueTask<bool> TryEnterAsync(TimeSpan holdTimeout, CancellationToken cancellationToken = default)
    {
        long deadline = Environment.TickCount64 + (long)holdTimeout.TotalMilliseconds;
        while (true)
        {
            // Taken before the state is read, so a transition that happens in
            // between still completes the signal this writer waits on.
            var changed = Volatile.Read(ref _changed).Task;

            Interlocked.Increment(ref _inFlight);
            int state = Volatile.Read(ref _state);
            if (state == Open) return true;
            Interlocked.Decrement(ref _inFlight);

            if (state == Closed) return false;

            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return false;
            var completed = await Task.WhenAny(changed, Task.Delay((int)remaining, cancellationToken)).ConfigureAwait(false);
            if (completed != changed) return false;
        }
    }

    /// <summary>Marks a write admitted by <see cref="TryEnterAsync"/> as applied.</summary>
    public void Exit() => Interlocked.Decrement(ref _inFlight);

    public void OpenGate() => Transition(Open);

    public void Close() => Transition(Closed);

    /// <summary>
    /// Stops admitting writes - new ones wait - and completes once every write
    /// admitted before the hold has called <see cref="Exit"/>.
    /// </summary>
    public async Task HoldAsync(CancellationToken cancellationToken = default)
    {
        Transition(Holding);
        var spin = new SpinWait();
        while (Volatile.Read(ref _inFlight) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (spin.NextSpinWillYield) await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            else spin.SpinOnce();
        }
    }

    private void Transition(int state)
    {
        TaskCompletionSource<bool> previous;
        lock (_transition)
        {
            Interlocked.Exchange(ref _state, state);
            previous = Interlocked.Exchange(ref _changed, NewSignal());
        }
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
#endif
