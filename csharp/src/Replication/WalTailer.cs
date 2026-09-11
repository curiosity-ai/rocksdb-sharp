#if !NETSTANDARD2_0

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RocksDbSharp
{
    /// <summary>
    /// A run of consecutive WAL write batches, already merged into one write batch. Applying it
    /// on a replica through <see cref="ReplicationConsumer.IngestChunk"/> advances that replica's
    /// sequence number by exactly the number of entries the primary's batches carried, so the two
    /// databases stay on the same sequence numbers.
    /// <para>
    /// <see cref="Data"/> points into the tailer's reusable buffer and is only valid until the next
    /// call to <see cref="WalTailer.TryReadChunk"/>.
    /// </para>
    /// </summary>
    public readonly struct WalChunk
    {
        internal WalChunk(ulong firstSequenceNumber, ulong nextSequenceNumber, int entryCount, byte[] buffer, int length)
        {
            FirstSequenceNumber = firstSequenceNumber;
            NextSequenceNumber  = nextSequenceNumber;
            EntryCount          = entryCount;
            Buffer              = buffer;
            Length              = length;
        }

        /// <summary>The sequence number of the first entry in the chunk.</summary>
        public ulong FirstSequenceNumber { get; }

        /// <summary>The first sequence number *not* in the chunk - what a replica has to ask for next.</summary>
        public ulong NextSequenceNumber { get; }

        /// <summary>How many entries the merged batch holds; also how far it advances the replica's sequence number.</summary>
        public int EntryCount { get; }

        public byte[] Buffer { get; }
        public int    Length { get; }

        public ReadOnlySpan<byte> Data => Buffer.AsSpan(0, Length);
    }

    /// <summary>
    /// Tails a database's write-ahead log for replication.
    /// <para>
    /// Two things make this cheap enough for millisecond-level replication lag, and both are the
    /// reason not to go back to calling <see cref="RocksDb.GetUpdatesSince"/> per poll:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The log iterator is kept alive across polls. Creating one costs a linear scan of the WAL file
    /// from its first record up to the requested sequence number - tens of milliseconds once a file
    /// holds a hundred thousand records - and an iterator sitting at the tail of the newest file picks
    /// up records appended after it was created, so that scan only has to be paid again when the WAL
    /// rolls over to a new file.
    /// </description></item>
    /// <item><description>
    /// Consecutive batches are merged into a single write batch (the same concatenation
    /// <c>WriteBatchInternal::Append</c> performs: one 12-byte header holding the summed entry count,
    /// followed by every batch's entries). One write batch per chunk means one message on the wire and
    /// one write on the replica instead of one of each per record.
    /// </description></item>
    /// </list>
    /// </summary>
    public sealed class WalTailer : IDisposable
    {
        //A serialized write batch is an 8-byte sequence number, a 4-byte entry count, then the entries.
        private const int HEADER_SIZE = 12;

        public const int DEFAULT_MAX_CHUNK_BYTES = 256 * 1024;

        //How long the first wait after a read that found something keeps polling on the calling thread
        //before handing over to a timer, whose resolution is a thousand times coarser.
        private const long DEFAULT_SPIN_BUDGET_MICROSECONDS = 200;

        //How long an exhausted iterator has to stay exhausted, while the database is ahead of it, before we
        //conclude the WAL rolled and pay for a new iterator. A record that is merely still sitting in the
        //log writer's buffer shows up as the same symptom and clears in microseconds, so reopening on the
        //first sight of it would mean paying the scan over and over for nothing.
        private const long DEFAULT_REOPEN_GRACE_MICROSECONDS = 250;

        private readonly RocksDb _db;
        private readonly int     _maxChunkBytes;

        private TransactionLogIterator _iterator;
        private byte[]                 _buffer;
        private ulong                  _nextSequenceNumber;
        private long                   _exhaustedSince;
        private long                   _reopens;
        private int                    _emptyReads;
        private long                   _reopenGraceTicks = (long)(Stopwatch.Frequency * (DEFAULT_REOPEN_GRACE_MICROSECONDS / 1_000_000.0));

        internal WalTailer(RocksDb db, ulong sequenceNumber, int maxChunkBytes = DEFAULT_MAX_CHUNK_BYTES)
        {
            if (db is null) throw new ArgumentNullException(nameof(db));
            if (maxChunkBytes <= HEADER_SIZE) throw new ArgumentOutOfRangeException(nameof(maxChunkBytes));

            _db                 = db;
            _maxChunkBytes      = maxChunkBytes;
            _buffer             = new byte[maxChunkBytes];
            _nextSequenceNumber = sequenceNumber;
        }

        /// <summary>
        /// How long an exhausted iterator has to stay exhausted, while the database is ahead of it, before
        /// it is reopened. See <see cref="DEFAULT_REOPEN_GRACE_MICROSECONDS"/>.
        /// </summary>
        public long ReopenGraceMicroseconds
        {
            get => (long)(_reopenGraceTicks * 1_000_000.0 / Stopwatch.Frequency);
            set => _reopenGraceTicks = (long)(Stopwatch.Frequency * (value / 1_000_000.0));
        }

        /// <summary>The first sequence number this tailer has not handed out yet.</summary>
        public ulong NextSequenceNumber => _nextSequenceNumber;

        /// <summary>True when the database holds sequence numbers this tailer has not read yet.</summary>
        public bool HasUpdates => _nextSequenceNumber <= _db.GetLatestSequenceNumber();

        /// <summary>
        /// How many times the log iterator had to be reopened - once per WAL roll under steady replication.
        /// A number that climbs faster than the WAL rolls means the grace period is too short and the scan
        /// cost is being paid for nothing.
        /// </summary>
        public long Reopens => _reopens;

        /// <summary>How far behind the database's newest sequence number this tailer is.</summary>
        public ulong LagInSequenceNumbers
        {
            get
            {
                ulong latest = _db.GetLatestSequenceNumber();
                return latest < _nextSequenceNumber ? 0 : latest - _nextSequenceNumber + 1;
            }
        }

        /// <summary>
        /// Reads the next run of write batches, up to the chunk size. Returns false when the
        /// database has nothing newer, in which case <see cref="WaitForUpdatesAsync"/> is what to
        /// call before trying again.
        /// </summary>
        public bool TryReadChunk(out WalChunk chunk)
        {
            chunk = default;

            int   entryCount = 0;
            int   length     = HEADER_SIZE;
            ulong firstSequenceNumber = _nextSequenceNumber;

            while (true)
            {
                if (_iterator is null && !TryOpenIterator()) break;

                if (!_iterator.Valid())
                {
                    if (entryCount > 0) break;
                    if (!HasUpdates) break;

                    //An exhausted iterator resumes the file it was reading when asked for the next record,
                    //so this is what covers the ordinary case of the primary having appended since.
                    _iterator.Next();

                    if (!_iterator.Valid())
                    {
                        //The iterator holds the list of files it saw when it was created, so a WAL roll
                        //leaves it stuck on a file that will never grow again - but only a roll does, so
                        //give the alternative (a record still buffered by the log writer) time to clear.
                        if (_exhaustedSince == 0)
                        {
                            _exhaustedSince = Stopwatch.GetTimestamp();
                            break;
                        }

                        if (Stopwatch.GetTimestamp() - _exhaustedSince < _reopenGraceTicks) break;

                        _reopens++;
                        DisposeIterator();

                        if (!TryOpenIterator() || !_iterator.Valid()) break;
                    }
                }

                _exhaustedSince = 0;

                _iterator.Status();

                //GetBatch() hands the batch over rather than lending it, so it can only be called once per
                //position - whatever it returns has to go into this chunk.
                using (var batch = _iterator.GetBatch(out ulong batchSequenceNumber))
                {
                    int   batchEntryCount = batch.Count();
                    ulong afterBatch      = batchSequenceNumber + (ulong)batchEntryCount;

                    //GetUpdatesSince() starts at the batch *containing* the requested sequence number, so the
                    //first batch a freshly opened iterator hands out is usually one we already replicated.
                    if (afterBatch > _nextSequenceNumber)
                    {
                        var body = batch.AsSpan().Slice(HEADER_SIZE);

                        EnsureCapacity(length + body.Length);
                        body.CopyTo(_buffer.AsSpan(length));

                        if (entryCount == 0) firstSequenceNumber = batchSequenceNumber;

                        length              += body.Length;
                        entryCount          += batchEntryCount;
                        _nextSequenceNumber  = afterBatch;
                    }
                }

                _iterator.Next();

                //Tested after appending, since the batch was already consumed: a chunk overshoots the target
                //size by at most one WAL batch.
                if (length >= _maxChunkBytes) break;
            }

            if (entryCount == 0)
            {
                _emptyReads++;
                return false;
            }

            _emptyReads = 0;

            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(0, sizeof(ulong)), firstSequenceNumber);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(sizeof(ulong), sizeof(int)), entryCount);

            chunk = new WalChunk(firstSequenceNumber, _nextSequenceNumber, entryCount, _buffer, length);
            return true;
        }

        /// <summary>
        /// Waits before the next <see cref="TryReadChunk"/>, after one that found nothing.
        /// <para>
        /// The first such wait polls on the calling thread, because a record is usually microseconds away
        /// from being readable and a timer cannot resolve that. It does not keep polling, though, and
        /// deliberately does not use <see cref="HasUpdates"/> as its exit condition past that point: the
        /// database publishes a sequence number before a separate reader can see the bytes behind it, so
        /// under a busy primary that flag reads true continuously while there is still nothing to read -
        /// spinning on it costs a whole core and buys about a millisecond.
        /// </para>
        /// </summary>
        public Task WaitForUpdatesAsync(CancellationToken cancellationToken = default, long spinBudgetMicroseconds = DEFAULT_SPIN_BUDGET_MICROSECONDS)
        {
            if (_emptyReads > 1) return Task.Delay(1, cancellationToken);

            long budget = (long)(Stopwatch.Frequency * (spinBudgetMicroseconds / 1_000_000.0));
            long start  = Stopwatch.GetTimestamp();

            while (Stopwatch.GetTimestamp() - start < budget)
            {
                if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
                if (HasUpdates) return Task.CompletedTask;

                Thread.SpinWait(32);
            }

            return HasUpdates ? Task.CompletedTask : Task.Delay(1, cancellationToken);
        }

        private bool TryOpenIterator()
        {
            //Asking for a sequence number the database has not reached is an error, not an empty iterator.
            if (!HasUpdates) return false;

            _iterator = _db.GetUpdatesSince(_nextSequenceNumber);
            return true;
        }

        private void DisposeIterator()
        {
            _iterator?.Dispose();
            _iterator = null;
        }

        private void EnsureCapacity(int length)
        {
            if (_buffer.Length >= length) return;

            int size = _buffer.Length;
            while (size < length) size *= 2;

            Array.Resize(ref _buffer, size);
        }

        public void Dispose()
        {
            DisposeIterator();
        }
    }
}

#endif
