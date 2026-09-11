using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RocksDbSharp;

namespace ReplicationTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "coordinator")
            {
                await RunCoordinatorAsync();
            }
            else if (args[0] == "primary")
            {
                string dbPath = args[1];
                int port = int.Parse(args[2]);
                await RunPrimaryAsync(dbPath, port);
            }
            else if (args[0] == "primary-stress")
            {
                string dbPath = args[1];
                int port = int.Parse(args[2]);
                int durationSeconds = args.Length > 3 ? int.Parse(args[3]) : 1800;
                int writerThreads = args.Length > 4 ? int.Parse(args[4]) : 4;
                int operationsPerSecond = args.Length > 5 ? int.Parse(args[5]) : 25_000;
                await RunPrimaryStressAsync(dbPath, port, durationSeconds, writerThreads, operationsPerSecond);
            }
            else if (args[0] == "replica")
            {
                string dbPath = args[1];
                int primaryPort = int.Parse(args[2]);
                await RunReplicaAsync(dbPath, primaryPort);
            }
        }

        static async Task RunCoordinatorAsync()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "RocksDbReplicationTest_Distributed");

            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);

            string sourcePath = Path.Combine(tempRoot, "source_db");
            string destPath   = Path.Combine(tempRoot, "dest_db");

            Directory.CreateDirectory(sourcePath);

            Console.WriteLine($"Coordinator: Using temp directory: {tempRoot}");

            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "dotnet";
            string exeArgsPrefix = exePath == "dotnet" ? "run --project Tests/ReplicationTest/ReplicationTest.csproj -- " : "";
            int port = 50051;

            Console.WriteLine("Coordinator: Starting primary...");
            var primaryProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"{exeArgsPrefix}primary \"{sourcePath}\" {port}",
                    UseShellExecute = false,
                    CreateNoWindow = false
                }
            };
            primaryProcess.Start();

            // Give primary some time to start the MagicOnion server
            await Task.Delay(2000);

            Console.WriteLine("Coordinator: Starting replica...");
            var replicaProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"{exeArgsPrefix}replica \"{destPath}\" {port}",
                    UseShellExecute = false,
                    CreateNoWindow = false
                }
            };

            replicaProcess.Start();

            // Wait for both to exit or run for a set duration
            await Task.Delay(6000_000);

            Console.WriteLine("Coordinator: Test duration finished, stopping processes...");
            if (!replicaProcess.HasExited) replicaProcess.Kill();
            if (!primaryProcess.HasExited) primaryProcess.Kill();

            Console.WriteLine("Coordinator: Done.");
        }

        static async Task RunPrimaryAsync(string dbPath, int port)
        {
            var walDir = Path.Combine(dbPath, "journal");
            Console.WriteLine($"[Primary] Starting on port {port}, dbPath: {dbPath}");
            var options = new DbOptions()
                .SetCreateIfMissing(true)
                .SetWalDir(walDir)
                .SetWalTtlSeconds(10)
                .SetMaxTotalWalSize(1024UL * 1024 * 10)
                .SetWalSizeLimitMB(1024UL * 1024 * 1);
                //WAL compression is deliberately not set: it costs about ten times the p90 replication lag,
                //and RocksDbWalInspector - which this sample's WAL retention uses - cannot read a compressed
                //log either.
                //.SetWriteBufferSize(4 * 1024)
                //.SetTargetFileSizeBase(4 * 1024);

            using (var sourceDb = RocksDb.Open(options, dbPath))
            {
                sourceDb.DisableFileDeletions();
                var commitDelayController = new AdaptiveCommitDelayController(replicaCount: 1, delayPerLagUnitMs: 5, lagUnit: 1000); //Delays for 5ms for each 1000 seq. no. behind
                Console.WriteLine("[Primary] DB opened. Starting MagicOnion server...");

                var builder = WebApplication.CreateBuilder();
                builder.Services.AddGrpc();
                builder.Services.AddMagicOnion();
                builder.Services.AddSingleton(sourceDb);
                builder.Services.AddSingleton(commitDelayController);
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.ListenLocalhost(port, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
                });

                var app = builder.Build();
                app.MapMagicOnionService();

                var appRunTask = app.RunAsync();

                Console.WriteLine("[Primary] Server running. Adding data...");

                long count = 0;
                
                var flushOptions = Native.Instance.rocksdb_flushoptions_create();
                Native.Instance.rocksdb_flushoptions_set_wait(flushOptions, Native.MarshalBool(true));

                while (!appRunTask.IsCompleted && count < 1_000_000_000)
                {
                    string key = $"key_{count:000000000}"; //Keys need to be sortable so the lag iterator in the replica code always check the last value
                    string val = Stopwatch.GetTimestamp().ToString(); // Store timestamp to measure lag
                    sourceDb.Put(key, val);
                    count++;
                    
                    if (count % 10_000 == 0)
                    {
                        Console.WriteLine($"[Primary {DateTimeOffset.UtcNow:HH:mm:ss:ffff}] Wrote {count:n0} keys. Seq. No. {sourceDb.GetLatestSequenceNumber():n0}");
                        await commitDelayController.DelayIfNeededAsync();
                    }

                    if(count % 100_000 == 0)
                    {
                        Native.Instance.rocksdb_flush(sourceDb.Handle, flushOptions);
                    }
                }

                Native.Instance.rocksdb_flushoptions_destroy(flushOptions);

                await Task.Delay(60_000);

                //Before the database goes out of scope: a replication stream still holds a WAL iterator
                //over it, and disposing underneath that takes the process down.
                await app.StopAsync();
            }
        }

        //Keys the load generator never touches, so the replica can find them by name.
        internal const string HEARTBEAT_KEY = "zzz_heartbeat";
        internal const string SENTINEL_KEY  = "zzz_writes_finished";

        /// <summary>
        /// A sustained mixed add/delete load from several threads, for checking that replication stays
        /// stable rather than that it is fast. Deletes and multi-entry batches are the point: a batch
        /// consumes one sequence number per entry, and concurrent writers get grouped into shared WAL
        /// records, so this is what would catch the chunk arithmetic being off by an entry.
        /// </summary>
        static async Task RunPrimaryStressAsync(
            string dbPath,
            int    port,
            int    durationSeconds,
            int    writerThreads,
            int    operationsPerSecond)
        {
            const int KEY_SPACE = 200_000; //Bounded, so deletes hit live keys and the database stays a fixed size

            var walDir = Path.Combine(dbPath, "journal");
            Console.WriteLine($"[Primary] Stress: {writerThreads} writer threads, {operationsPerSecond:n0} ops/s target, {durationSeconds:n0}s, {KEY_SPACE:n0} keys, port {port}");

            var options = new DbOptions()
                .SetCreateIfMissing(true)
                .SetWalDir(walDir)
                .SetWalTtlSeconds(10)
                .SetMaxTotalWalSize(1024UL * 1024 * 10)
                .SetWalSizeLimitMB(1024UL * 1024 * 1);

            //Off by default, because it costs this pair an order of magnitude at the p90 - a compressed WAL
            //gathers records into a block before it emits any of them, so a reader tailing the log cannot
            //see the newest writes until that block is written out. Set REPLICATION_WAL_COMPRESSION=zstd to
            //measure with it. Turning it off needs no migration: the compression type is recorded per WAL
            //file, so an existing compressed log still reads back and still replicates.
            bool compressWal = Environment.GetEnvironmentVariable("REPLICATION_WAL_COMPRESSION") == "zstd";

            if (compressWal) options.SetWalCompression(Compression.Zstd);

            Console.WriteLine($"[Primary] WAL compression: {(compressWal ? "zstd" : "off")}");

            using (var sourceDb = RocksDb.Open(options, dbPath))
            {
                sourceDb.DisableFileDeletions();

                var commitDelayController = new AdaptiveCommitDelayController(replicaCount: 1, delayPerLagUnitMs: 5, lagUnit: 1000);

                var builder = WebApplication.CreateBuilder();
                builder.Services.AddGrpc();
                builder.Services.AddMagicOnion();
                builder.Services.AddSingleton(sourceDb);
                builder.Services.AddSingleton(commitDelayController);
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
                builder.WebHost.ConfigureKestrel(kestrel =>
                {
                    kestrel.ListenLocalhost(port, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
                });

                var app = builder.Build();
                app.MapMagicOnionService();

                var appRunTask = app.RunAsync();

                Console.WriteLine("[Primary] Server running. Starting load...");

                var  stop      = new CancellationTokenSource();
                long puts      = 0;
                long deletes   = 0;
                long batches   = 0;
                long bursting  = 0;

                var writers = new Thread[writerThreads];

                for (int t = 0; t < writerThreads; t++)
                {
                    int threadIndex = t;

                    writers[t] = new Thread(() =>
                    {
                        var rng          = new Random(1000 + threadIndex);
                        var perThreadOps = Math.Max(1, operationsPerSecond / writerThreads);
                        var rateClock    = Stopwatch.StartNew();
                        long done        = 0;

                        while (!stop.IsCancellationRequested)
                        {
                            int roll = rng.Next(100);

                            if (roll < 55)
                            {
                                sourceDb.Put(KeyFor(rng.Next(KEY_SPACE)), Stopwatch.GetTimestamp().ToString());
                                Interlocked.Increment(ref puts);
                                done++;
                            }
                            else if (roll < 80)
                            {
                                sourceDb.Remove(KeyFor(rng.Next(KEY_SPACE)));
                                Interlocked.Increment(ref deletes);
                                done++;
                            }
                            else
                            {
                                //A multi-entry batch: one WAL record carrying several sequence numbers.
                                int entries = 2 + rng.Next(7);

                                using (var batch = new WriteBatch())
                                {
                                    for (int i = 0; i < entries; i++)
                                    {
                                        var key = KeyFor(rng.Next(KEY_SPACE));

                                        if (rng.Next(100) < 70)
                                        {
                                            batch.Put(key, Stopwatch.GetTimestamp().ToString());
                                            Interlocked.Increment(ref puts);
                                        }
                                        else
                                        {
                                            batch.Delete(Encoding.UTF8.GetBytes(key));
                                            Interlocked.Increment(ref deletes);
                                        }
                                    }

                                    sourceDb.Write(batch);
                                }

                                Interlocked.Increment(ref batches);
                                done += entries;
                            }

                            //Throttled to a target rate so a half-hour run stays within the disk budget -
                            //except during the periodic bursts, which exist to build a backlog on purpose.
                            if (done >= 256 && Volatile.Read(ref bursting) == 0)
                            {
                                var expectedMs = done * 1000.0 / perThreadOps;

                                if (rateClock.Elapsed.TotalMilliseconds < expectedMs)
                                {
                                    Thread.Sleep(Math.Max(1, (int)(expectedMs - rateClock.Elapsed.TotalMilliseconds)));
                                }

                                if (done >= perThreadOps)
                                {
                                    done = 0;
                                    rateClock.Restart();
                                }
                            }
                        }
                    });

                    writers[t].IsBackground = true;
                    writers[t].Start();
                }

                //One writer owns the heartbeat, so the replica has a single key whose value is the time
                //the primary last committed anything.
                //The heartbeat also times its own commit. The replica measures lag as the age of this key,
                //which cannot tell a slow replication path from the primary being unable to commit - so the
                //primary has to report what its own writes cost before that number means anything.
                long heartbeatStallTicks = 0;

                var heartbeat = new Thread(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var before = Stopwatch.GetTimestamp();
                        sourceDb.Put(HEARTBEAT_KEY, Stopwatch.GetTimestamp().ToString());
                        var elapsed = Stopwatch.GetTimestamp() - before;

                        if (elapsed > Volatile.Read(ref heartbeatStallTicks)) Volatile.Write(ref heartbeatStallTicks, elapsed);

                        Thread.Sleep(1);
                    }
                });

                heartbeat.IsBackground = true;
                heartbeat.Start();

                var run    = Stopwatch.StartNew();
                var report = Stopwatch.StartNew();
                long lastPuts = 0, lastDeletes = 0;

                while (run.Elapsed.TotalSeconds < durationSeconds && !appRunTask.IsCompleted)
                {
                    await Task.Delay(1000);

                    //Every five minutes, drop the throttle for fifteen seconds. The backlog that builds is
                    //what makes the catch-up behaviour observable.
                    int inCycle = (int)(run.Elapsed.TotalSeconds % 300);
                    Volatile.Write(ref bursting, (run.Elapsed.TotalSeconds > 60 && inCycle < 15) ? 1 : 0);

                    if (report.ElapsedMilliseconds >= 15_000)
                    {
                        long p = Interlocked.Read(ref puts), d = Interlocked.Read(ref deletes);
                        double seconds = report.Elapsed.TotalSeconds;

                        Console.WriteLine($"[Primary {DateTimeOffset.UtcNow:HH:mm:ss}] t={run.Elapsed.TotalSeconds:n0}s puts={p:n0} deletes={d:n0} batches={Interlocked.Read(ref batches):n0} | {(p - lastPuts + d - lastDeletes) / seconds:n0} ops/s{(Volatile.Read(ref bursting) == 1 ? " BURST" : "")} | seq={sourceDb.GetLatestSequenceNumber():n0} wal={WalBytes(walDir) / 1024 / 1024:n0}MB slowestCommit={Interlocked.Exchange(ref heartbeatStallTicks, 0) * 1000.0 / Stopwatch.Frequency:n0}ms");

                        lastPuts    = p;
                        lastDeletes = d;
                        report.Restart();
                    }

                    await commitDelayController.DelayIfNeededAsync();
                }

                Console.WriteLine("[Primary] Stopping writers...");
                stop.Cancel();

                foreach (var writer in writers) writer.Join();
                heartbeat.Join();

                //The replica watches for this to know the keyspace has settled.
                sourceDb.Put(SENTINEL_KEY, "1");

                var (keys, digest) = ComputeDigest(sourceDb);

                Console.WriteLine($"[Primary] DONE seq={sourceDb.GetLatestSequenceNumber():n0} puts={Interlocked.Read(ref puts):n0} deletes={Interlocked.Read(ref deletes):n0} batches={Interlocked.Read(ref batches):n0}");
                Console.WriteLine($"[Primary] DIGEST keys={keys} digest={digest:x16} seq={sourceDb.GetLatestSequenceNumber()}");

                //Left running so the replica can finish draining and report its own digest.
                await Task.Delay(120_000);

                await app.StopAsync();
            }
        }

        static string KeyFor(int index) => $"key_{index:000000000}";

        static long WalBytes(string walDir)
        {
            if (!Directory.Exists(walDir)) return 0;

            long total = 0;

            foreach (var file in Directory.GetFiles(walDir))
            {
                try { total += new FileInfo(file).Length; } catch (IOException) { /* rotated away mid-scan */ }
            }

            return total;
        }

        /// <summary>
        /// An order-dependent hash of every key and value in the database. Two databases agree on it only
        /// if they hold exactly the same keyspace, which is the point of comparing it across the pair.
        /// </summary>
        internal static (long keys, ulong digest) ComputeDigest(RocksDb db)
        {
            const ulong FNV_OFFSET = 14695981039346656037UL;
            const ulong FNV_PRIME  = 1099511628211UL;

            ulong digest = FNV_OFFSET;
            long  keys   = 0;

            using (var iterator = db.NewIterator())
            {
                iterator.SeekToFirst();

                while (iterator.Valid())
                {
                    foreach (var b in iterator.GetKeySpan()) digest = (digest ^ b) * FNV_PRIME;

                    digest = (digest ^ 0xFF) * FNV_PRIME; //separator, so a byte moving between key and value still shows

                    foreach (var b in iterator.GetValueSpan()) digest = (digest ^ b) * FNV_PRIME;

                    digest = (digest ^ 0xFE) * FNV_PRIME;

                    keys++;
                    iterator.Next();
                }
            }

            return (keys, digest);
        }

        static async Task RunReplicaAsync(string dbPath, int primaryPort)
        {
            Console.WriteLine($"[Replica] Starting, connecting to port {primaryPort}, dbPath: {dbPath}");

            var channel = GrpcChannel.ForAddress($"http://localhost:{primaryPort}");
            var client  = MagicOnionClient.Create<IReplicationService>(channel);

            Console.WriteLine("[Replica] Requesting Initial State...");
            var stream = await client.SyncInitialStateAsync();

            Directory.CreateDirectory(dbPath);

            while (await stream.ResponseStream.MoveNext(CancellationToken.None))
            {
                var file = stream.ResponseStream.Current;
                Console.WriteLine($"[Replica] Received file: {file.FileName} ({file.FileSize} bytes)");
                string destFilePath = Path.Combine(dbPath, file.FileName);
                await File.WriteAllBytesAsync(destFilePath, file.Content);
            }

            Console.WriteLine("[Replica] Initial State replicated. Opening local DB...");

            var options = new DbOptions()
                .SetCreateIfMissing(true)
                .SetWalTtlSeconds(10)
                .SetMaxTotalWalSize(1024UL * 1024 * 10)
                .SetWalSizeLimitMB(1024UL * 1024 * 1);

            if (Environment.GetEnvironmentVariable("REPLICATION_WAL_COMPRESSION") == "zstd") options.SetWalCompression(Compression.Zstd);
                //.SetWriteBufferSize(4 * 1024)
                //.SetTargetFileSizeBase(4 * 1024);

            using (var destDb = RocksDb.Open(options, dbPath))
            {
                ulong startSeq = destDb.GetLatestSequenceNumber() + 1;
                Console.WriteLine($"[Replica] Destination DB sequence number: {startSeq - 1:n0}. Starting WAL sync...");

                var consumer = new ReplicationConsumer(destDb);

                var updatesStream = await client.SyncUpdatesAsync(startSeq);

                long chunkCount  = 0;
                long ingestTicks = 0;
                var  report      = Stopwatch.StartNew();
                Task reporting   = null;

                //Lag is sampled on its own clock rather than inside the ingest loop, so a stretch where
                //nothing arrives is measured as the lag it is instead of going unreported. The same task
                //watches for the stress primary's sentinel, for the same reason: once the primary goes
                //quiet there are no chunks left to hang a check off.
                var samples      = new List<double>();
                var drained      = new CancellationTokenSource();
                var stopSampling = new CancellationTokenSource();
                var sampler      = SampleLagAsync(destDb, samples, drained, stopSampling.Token);

                try
                {
                    while (await updatesStream.ResponseStream.MoveNext(drained.Token))
                    {
                        var chunk        = updatesStream.ResponseStream.Current;
                        var beforeIngest = Stopwatch.GetTimestamp();

                        consumer.IngestChunk(chunk.Data);
                        chunk.ReturnToPool();

                        ingestTicks += Stopwatch.GetTimestamp() - beforeIngest;
                        chunkCount++;

                        if (report.ElapsedMilliseconds >= 2000)
                        {
                            Console.WriteLine($"[Replica {DateTimeOffset.UtcNow:HH:mm:ss.fff}] chunks={chunkCount:n0} chunkSeq={chunk.SequenceNumber:n0} localSeq={destDb.GetLatestSequenceNumber():n0} ingest={ingestTicks * 1000.0 / Stopwatch.Frequency:n0}ms LAG={ReadLagMs(destDb):n1}ms{Summarize(samples)}");

                            ingestTicks = 0;
                            report.Restart();

                            //Deliberately not awaited. The primary walks its WAL directory before it
                            //answers this, and awaiting it here stopped the replica consuming the stream
                            //for as long as that took - which was a quarter of the wall clock, and the
                            //whole of this pair's lag tail.
                            if (reporting is null || reporting.IsCompleted)
                            {
                                reporting = ReportAsync(client, destDb.GetLatestSequenceNumber());
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    //The sentinel was seen and the drain window has passed.
                }
                catch (RpcException e)
                {
                    Console.WriteLine($"[Replica] Stream ended: {e.Status.StatusCode}");
                }
                finally
                {
                    //Before the database is disposed - the sampler reads it, and reading a disposed
                    //database takes the process down rather than throwing.
                    stopSampling.Cancel();
                    await sampler;
                }

                var (keys, digest) = ComputeDigest(destDb);

                Console.WriteLine($"[Replica] DONE chunks={chunkCount:n0} seq={destDb.GetLatestSequenceNumber()}");
                Console.WriteLine($"[Replica] DIGEST keys={keys} digest={digest:x16} seq={destDb.GetLatestSequenceNumber()}");
                Console.WriteLine($"[Replica] LAG SUMMARY{Summarize(samples, full: true)}");
            }
        }

        /// <summary>
        /// The lag the replica is behind by, read off the key the primary stamps with its commit time.
        /// Falls back to the last key in the database, which is what the ascending-key benchmark load
        /// leaves behind.
        /// </summary>
        static async Task ReportAsync(IReplicationService client, ulong sequenceNumber)
        {
            await client.ReportLastSyncSequenceNumber(0, sequenceNumber);
        }

        static double ReadLagMs(RocksDb db)
        {
            var heartbeat = db.Get(HEARTBEAT_KEY);

            if (heartbeat is object && long.TryParse(heartbeat, out long stamped))
            {
                return (Stopwatch.GetTimestamp() - stamped) * 1000.0 / Stopwatch.Frequency;
            }

            using (var iterator = db.NewIterator())
            {
                iterator.SeekToLast();

                if (iterator.Valid() && long.TryParse(iterator.StringValue(), out long writeTime))
                {
                    return (Stopwatch.GetTimestamp() - writeTime) * 1000.0 / Stopwatch.Frequency;
                }
            }

            return -1;
        }

        //Samples up to WARM_UP_SECONDS are dropped: the replica opens on a checkpoint and has to drain
        //whatever the primary wrote while it was starting, which is a real number but not the steady-state
        //one this test is about.
        private const int WARM_UP_SECONDS = 60;

        static async Task SampleLagAsync(RocksDb db, List<double> samples, CancellationTokenSource drained, CancellationToken cancellationToken)
        {
            var  running       = Stopwatch.StartNew();
            bool sentinelSeen  = false;
            var  sinceSentinel = new Stopwatch();

            //A per-sample trace, so the shape of a catch-up can be looked at after the run rather than
            //only its percentiles.
            var tracePath = Environment.GetEnvironmentVariable("REPLICATION_LAG_TRACE");
            using var trace = tracePath is object ? new StreamWriter(tracePath) { AutoFlush = true } : null;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken);

                    //Only while the primary is still writing: the heartbeat is the time it last committed,
                    //so once it stops the same reading is just elapsed wall-clock time, not lag.
                    if (!sentinelSeen)
                    {
                        double lag = ReadLagMs(db);

                        if (lag >= 0 && running.Elapsed.TotalSeconds > WARM_UP_SECONDS)
                        {
                            lock (samples) samples.Add(lag);
                            trace?.WriteLine($"{running.Elapsed.TotalSeconds:F1},{lag:F2},{db.GetLatestSequenceNumber()}");
                        }
                    }

                    if (!sentinelSeen && db.Get(SENTINEL_KEY) is object)
                    {
                        Console.WriteLine($"[Replica] Sentinel seen at seq={db.GetLatestSequenceNumber()}, draining...");
                        sentinelSeen = true;
                        sinceSentinel.Restart();
                    }

                    if (sentinelSeen && sinceSentinel.Elapsed.TotalSeconds > 10)
                    {
                        drained.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //The database is about to be disposed; reading it after that would crash the process.
            }
        }

        static string Summarize(List<double> samples, bool full = false)
        {
            double[] sorted;

            lock (samples)
            {
                if (samples.Count == 0) return string.Empty;
                sorted = samples.ToArray();
            }

            Array.Sort(sorted);

            string percentiles = $" n={sorted.Length:n0} median={sorted[sorted.Length / 2]:n1}ms p90={sorted[(int)(sorted.Length * 0.90)]:n1}ms p99={sorted[(int)(sorted.Length * 0.99)]:n1}ms max={sorted[sorted.Length - 1]:n1}ms";

            return full ? percentiles : $" [p99={sorted[(int)(sorted.Length * 0.99)]:n0}ms]";
        }
    }
}
