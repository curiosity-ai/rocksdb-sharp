using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
                .SetWalSizeLimitMB(1024UL * 1024 * 1)
                .SetWalCompression(Compression.Zstd); //No if using RocksDbWalInspector
                //.SetWriteBufferSize(4 * 1024)
                //.SetTargetFileSizeBase(4 * 1024);

            using (var sourceDb = RocksDbSharp.RocksDb.Open(options, dbPath))
            {
                sourceDb.DisableFileDeletions();

                Console.WriteLine("[Primary] DB opened. Starting MagicOnion server...");

                var builder = WebApplication.CreateBuilder();
                builder.Services.AddGrpc();
                builder.Services.AddMagicOnion();
                builder.Services.AddSingleton(sourceDb);
                builder.Services.AddSingleton(dbPath); // Hacky: register string as dbPath
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
                
                await Task.Delay(10_000);

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
                        Console.WriteLine($"[Primary {DateTimeOffset.UtcNow:HH:mm:ss:ffff}] Wrote {count} keys. Seq. No. {sourceDb.GetLatestSequenceNumber()}");
                    }

                    if(count % 100_000 == 0)
                    {
                        Native.Instance.rocksdb_flush(sourceDb.Handle, flushOptions);

                    }
                }

                Native.Instance.rocksdb_flushoptions_destroy(flushOptions);

                await Task.Delay(60_000);
            }
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
                .SetWalCompression(Compression.Zstd)
                .SetMaxTotalWalSize(1024UL * 1024 * 10)
                .SetWalSizeLimitMB(1024UL * 1024 * 1);
                //.SetWriteBufferSize(4 * 1024)
                //.SetTargetFileSizeBase(4 * 1024);

            using (var destDb = RocksDbSharp.RocksDb.Open(options, dbPath))
            {
                ulong startSeq = destDb.GetLatestSequenceNumber() + 1;
                Console.WriteLine($"[Replica] Destination DB sequence number: {startSeq - 1}. Starting WAL sync...");

                var consumer = new ReplicationConsumer(destDb);

                var updatesStream = await client.SyncUpdatesAsync(startSeq);

                int batchCount = 0;

                while (await updatesStream.ResponseStream.MoveNext(CancellationToken.None))
                {
                    var batch = updatesStream.ResponseStream.Current;
                    consumer.IngestBatch(batch.SequenceNumber, batch.Data);
                    batch.ReturnToPool();

                    batchCount++;

                    if (batchCount % 10_000 == 0)
                    {
                        Console.WriteLine($"[Replica {DateTimeOffset.UtcNow:HH:mm:ss:ffff}]] Ingested {batchCount} WAL batches, last sequence number: {batch.SequenceNumber}. Current Sequence: {destDb.GetLatestSequenceNumber()}");

                        using (var iter = destDb.NewIterator())
                        {
                            iter.SeekToLast();
                            if (iter.Valid())
                            {
                                string val = iter.StringValue();
                                if (long.TryParse(val, out long writeTime))
                                {
                                    var lag = Stopwatch.GetTimestamp() - writeTime;
                                    Console.WriteLine($"[Replica] Latest key lag: {TimeSpan.FromTicks(lag).TotalMilliseconds:n0} ms");
                                }
                            }
                        }

                        await client.ReportLastSyncSequenceNumber(batch.SequenceNumber);
                    }
                }
            }
        }
    }
}
