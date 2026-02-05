using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RocksDbSharp;

namespace ReplicationTest
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                RunTest();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Test Failed: {ex}");
                Environment.Exit(1);
            }
        }

        static void RunTest()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "RocksDbReplicationTest_" + Guid.NewGuid().ToString());
            string sourcePath = Path.Combine(tempRoot, "source_db");
            string destPath = Path.Combine(tempRoot, "dest_db");

            Directory.CreateDirectory(sourcePath);
            // Dest path will be created by IngestFile

            try
            {
                Console.WriteLine($"Using temp directory: {tempRoot}");
                Console.WriteLine("Initializing Source DB...");
                var options = new DbOptions()
                    .SetCreateIfMissing(true)
                    .SetWALTtlSeconds(0) // Keep WAL
                    .SetMaxTotalWalSize(1024 * 1024 * 100);

                // Small SST files to ensure multiple files
                options.SetWriteBufferSize(4 * 1024);
                options.SetTargetFileSizeBase(4 * 1024);

                using (var sourceDb = RocksDb.Open(options, sourcePath))
                {
                    Console.WriteLine("Populating Source DB (Phase 1)...");
                    for (int i = 0; i < 1000; i++)
                    {
                        sourceDb.Put($"key{i}", $"value{i}");
                    }

                    // Force flush
                    sourceDb.Flush(new FlushOptions().SetWaitForFlush(true));

                    Console.WriteLine("Replicating Initial State (Checkpoint)...");
                    var replicator = new ReplicationSource(sourceDb, sourcePath);

                    using (var session = replicator.GetInitialState())
                    {
                        foreach (var file in session.Files)
                        {
                            Console.WriteLine($"Replicating file: {file.FileName} ({file.FileSize} bytes)");
                            ReplicationConsumer.IngestFile(file, destPath);
                            file.Dispose();
                        }
                    }

                    ulong startSeq = 0;
                    // Verify Initial State in Dest DB
                    Console.WriteLine("Verifying Initial Replication...");
                    using (var destDb = RocksDb.Open(options, destPath))
                    {
                        string val = destDb.Get("key0");
                        if (val != "value0") throw new Exception($"Verification failed. Expected 'value0', got '{val}'");
                        val = destDb.Get("key999");
                        if (val != "value999") throw new Exception($"Verification failed. Expected 'value999', got '{val}'");

                        startSeq = destDb.GetLatestSequenceNumber() + 1;
                        Console.WriteLine($"Dest DB Sequence Number: {startSeq - 1}. Next update from: {startSeq}");
                    }

                    Console.WriteLine("Populating Source DB (Phase 2 - WAL)...");
                    // Write more data
                    for (int i = 1000; i < 2000; i++)
                    {
                        sourceDb.Put($"key{i}", $"value{i}");
                    }

                    // Delete some keys
                    sourceDb.Remove("key0");

                    Console.WriteLine($"Replicating WAL Updates from {startSeq}...");
                    using (var destDb = RocksDb.Open(options, destPath))
                    {
                        var consumer = new ReplicationConsumer(destDb);

                        int batchCount = 0;
                        foreach (var batch in replicator.GetWalUpdates(startSeq))
                        {
                            consumer.IngestBatch(batch);
                            batchCount++;
                        }
                        Console.WriteLine($"Ingested {batchCount} WAL batches.");

                        // Verify Phase 2
                        Console.WriteLine("Verifying WAL Replication...");
                        string val = destDb.Get("key1000");
                        if (val != "value1000") throw new Exception($"Verification failed. Expected 'value1000', got '{val}'");

                        val = destDb.Get("key0");
                        if (val != null) throw new Exception($"Verification failed. Expected 'key0' to be deleted, got '{val}'");
                    }
                }

                Console.WriteLine("Test Passed!");
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    try
                    {
                        Directory.Delete(tempRoot, true);
                        Console.WriteLine("Cleaned up temp directory.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Failed to clean up temp directory {tempRoot}: {ex.Message}");
                    }
                }
            }
        }
    }
}
