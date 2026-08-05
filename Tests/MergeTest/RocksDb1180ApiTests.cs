using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;

namespace Tests;

/// <summary>
/// Covers the API this binding gained for RocksDB 11.8.0, plus the asynchronous read
/// methods added alongside it. Every option here is read back through its getter, which
/// is what proves the native library actually carries the new C API entry points rather
/// than an older one that happened to be on the search path.
/// </summary>
[TestClass]
public class RocksDb1180ApiTests
{
    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), "rocksdb-sharp-tests", Guid.NewGuid().ToString());

    private static RocksDb OpenTempDb(out string dbPath, DbOptions options = null)
    {
        dbPath = NewDbPath();
        Directory.CreateDirectory(dbPath);
        return RocksDb.Open(options ?? new DbOptions().SetCreateIfMissing(), dbPath);
    }

    private static void DeleteQuietly(string dbPath)
    {
        try
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, true);
        }
        catch (IOException)
        {
            // A leftover directory is not worth failing a test over.
        }
    }

    [TestMethod]
    public void FlushOptionsListenerWaitRoundTrips()
    {
        var flushOptions = new FlushOptions();

        // The default the 11.8.0 release notes give for FlushOptions::listener_wait.
        Assert.IsFalse(flushOptions.GetWaitForListeners());

        flushOptions.SetWaitForListeners(true);
        Assert.IsTrue(flushOptions.GetWaitForListeners());

        flushOptions.SetWaitForListeners(false);
        Assert.IsFalse(flushOptions.GetWaitForListeners());
    }

    [TestMethod]
    public void FlushOptionsOtherSettersRoundTrip()
    {
        var flushOptions = new FlushOptions()
            .SetWaitForFlush(true)
            .SetAllowWriteStall(true)
            .SetForceAtomicFlush(true);

        Assert.IsTrue(flushOptions.GetWaitForFlush());
        Assert.IsTrue(flushOptions.GetAllowWriteStall());
        Assert.IsTrue(flushOptions.GetForceAtomicFlush());

        flushOptions.SetWaitForFlush(false).SetAllowWriteStall(false).SetForceAtomicFlush(false);

        Assert.IsFalse(flushOptions.GetWaitForFlush());
        Assert.IsFalse(flushOptions.GetAllowWriteStall());
        Assert.IsFalse(flushOptions.GetForceAtomicFlush());
    }

    [TestMethod]
    public void FlushWaitingForListenersFlushesTheMemtable()
    {
        using var db = OpenTempDb(out var dbPath);
        try
        {
            for (var i = 0; i < 1000; i++)
                db.Put($"key{i}", $"value{i}");

            db.Flush(new FlushOptions().SetWaitForFlush(true).SetWaitForListeners(true));

            // The memtable is empty once the flush is committed, and everything written
            // above is still readable from the SST it went into.
            Assert.AreEqual("0", db.GetProperty("rocksdb.num-entries-active-mem-table"));
            Assert.AreEqual("value999", db.Get("key999"));
        }
        finally
        {
            db.Dispose();
            DeleteQuietly(dbPath);
        }
    }

    [TestMethod]
    public void ReadIoExecutorThreadsRoundTripsAndOpens()
    {
        var options = new DbOptions().SetCreateIfMissing();

        Assert.AreEqual(1, options.GetReadIoExecutorThreads(), "1 is the DBOptions::read_io_executor_threads default");

        options.SetReadIoExecutorThreads(4);
        Assert.AreEqual(4, options.GetReadIoExecutorThreads());

        using var db = OpenTempDb(out var dbPath, options);
        try
        {
            db.Put("key", "value");
            Assert.AreEqual("value", db.Get("key"));
        }
        finally
        {
            db.Dispose();
            DeleteQuietly(dbPath);
        }
    }

    [TestMethod]
    public void AsyncDbOptionsRoundTrip()
    {
        var options = new DbOptions();

        Assert.IsFalse(options.GetOpenFilesAsync());
        Assert.IsFalse(options.GetAsyncWalPrecreate());

        options.SetOpenFilesAsync(true).SetAsyncWalPrecreate(true);

        Assert.IsTrue(options.GetOpenFilesAsync());
        Assert.IsTrue(options.GetAsyncWalPrecreate());
    }

    [TestMethod]
    public void ValueSizeSoftLimitRoundTrips()
    {
        var readOptions = new ReadOptions();

        Assert.AreEqual(ulong.MaxValue, readOptions.GetValueSizeSoftLimit(), "ReadOptions::value_size_soft_limit defaults to uint64 max, i.e. unlimited");

        readOptions.SetValueSizeSoftLimit(64 * 1024);
        Assert.AreEqual(64UL * 1024, readOptions.GetValueSizeSoftLimit());
    }

    [TestMethod]
    public void ValueSizeSoftLimitAbortsMultiGetOnceExceededButAlwaysMakesProgress()
    {
        using var db = OpenTempDb(out var dbPath);
        try
        {
            // Four values of 8 KiB each, so a 12 KiB limit is passed part way through.
            var value = new string('v', 8 * 1024);
            var keys = new[] { "key0", "key1", "key2", "key3" };
            foreach (var key in keys)
                db.Put(key, value);
            db.Flush(new FlushOptions().SetWaitForFlush(true));

            var readOptions = new ReadOptions().SetValueSizeSoftLimit(12 * 1024);
            var results = db.MultiGetWithStatus(keys, readOptions: readOptions);

            Assert.AreEqual(4, results.Length);

            // Always makes progress: the first key is read whatever the limit is.
            Assert.IsTrue(results[0].Succeeded);
            Assert.AreEqual(value, results[0].Value);

            // And the limit does bound the request rather than being ignored: the keys past the
            // point where the values read exceeded it are aborted, not failed and not missing.
            var aborted = results.Where(r => r.WasAborted).ToArray();
            Assert.IsTrue(aborted.Length > 0, "the limit should have aborted at least one of the later keys");
            Assert.IsTrue(aborted.All(r => r.Value is null));
            Assert.IsTrue(results.All(r => r.Succeeded || r.WasAborted), "no key should have failed for any other reason");

            // Reading the aborted keys again without the limit gets their values.
            var retried = db.MultiGetWithStatus(aborted.Select(r => r.Key).ToArray());
            Assert.IsTrue(retried.All(r => r.Succeeded));
            Assert.IsTrue(retried.All(r => r.Value == value));
        }
        finally
        {
            db.Dispose();
            DeleteQuietly(dbPath);
        }
    }

    [TestMethod]
    public async Task GetAsyncReadsWhatWasWritten()
    {
        using var db = OpenTempDb(out var dbPath);
        try
        {
            db.Put("present", "value");

            Assert.AreEqual("value", await db.GetAsync("present"));
            Assert.IsNull(await db.GetAsync("absent"));

            CollectionAssert.AreEqual("value"u8.ToArray(), await db.GetAsync("present"u8.ToArray()));
            Assert.IsNull(await db.GetAsync("absent"u8.ToArray()));
        }
        finally
        {
            db.Dispose();
            DeleteQuietly(dbPath);
        }
    }

    [TestMethod]
    public async Task GetAsyncWithAsyncIoReadOptionReadsFromDisk()
    {
        using var db = OpenTempDb(out var dbPath);
        try
        {
            for (var i = 0; i < 2000; i++)
                db.Put($"key{i:D5}", $"value{i:D5}");

            // Flush so the reads below have to go to an SST rather than the memtable,
            // which is where ReadOptions::async_io has anything to do.
            db.Flush(new FlushOptions().SetWaitForFlush(true));

            var readOptions = new ReadOptions().SetAsyncIO(true);

            for (var i = 0; i < 2000; i += 100)
                Assert.AreEqual($"value{i:D5}", await db.GetAsync($"key{i:D5}", readOptions: readOptions));
        }
        finally
        {
            db.Dispose();
            DeleteQuietly(dbPath);
        }
    }

    [TestMethod]
    public async Task MultiGetAsyncReturnsEveryKeyInOrder()
    {
        using var db = OpenTempDb(out var dbPath);
        try
        {
            for (var i = 0; i < 100; i++)
                db.Put($"key{i:D3}", $"value{i:D3}");
            db.Flush(new FlushOptions().SetWaitForFlush(true));

            var keys = Enumerable.Range(0, 100).Select(i => $"key{i:D3}").Concat(new[] { "missing" }).ToArray();

            var results = await db.MultiGetAsync(keys, readOptions: new ReadOptions().SetAsyncIO(true));

            Assert.AreEqual(101, results.Length);
            for (var i = 0; i < 100; i++)
            {
                Assert.AreEqual($"key{i:D3}", results[i].Key);
                Assert.AreEqual($"value{i:D3}", results[i].Value);
            }
            Assert.AreEqual("missing", results[100].Key);
            Assert.IsNull(results[100].Value);

            var byteResults = await db.MultiGetAsync(new[] { "key000"u8.ToArray(), "key099"u8.ToArray() });
            Assert.AreEqual(2, byteResults.Length);
            CollectionAssert.AreEqual("value000"u8.ToArray(), byteResults[0].Value);
            CollectionAssert.AreEqual("value099"u8.ToArray(), byteResults[1].Value);
        }
        finally
        {
            db.Dispose();
            DeleteQuietly(dbPath);
        }
    }

    [TestMethod]
    public async Task HasKeyAsyncFindsOnlyWhatIsThere()
    {
        using var db = OpenTempDb(out var dbPath);
        try
        {
            db.Put("present", "value");

            Assert.IsTrue(await db.HasKeyAsync("present"u8.ToArray()));
            Assert.IsFalse(await db.HasKeyAsync("absent"u8.ToArray()));
        }
        finally
        {
            db.Dispose();
            DeleteQuietly(dbPath);
        }
    }

    [TestMethod]
    public async Task ManyAsyncReadsRunConcurrently()
    {
        using var db = OpenTempDb(out var dbPath);
        try
        {
            for (var i = 0; i < 500; i++)
                db.Put($"key{i:D3}", $"value{i:D3}");
            db.Flush(new FlushOptions().SetWaitForFlush(true));

            var reads = Enumerable.Range(0, 500).Select(i => db.GetAsync($"key{i:D3}")).ToArray();

            var values = await Task.WhenAll(reads);

            for (var i = 0; i < 500; i++)
                Assert.AreEqual($"value{i:D3}", values[i]);
        }
        finally
        {
            db.Dispose();
            DeleteQuietly(dbPath);
        }
    }

    [TestMethod]
    public async Task AsyncReadsObserveCancellationBeforeTheyStart()
    {
        using var db = OpenTempDb(out var dbPath);
        try
        {
            db.Put("key", "value");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(
                () => db.GetAsync("key", cancellationToken: cancelled.Token));

            await Assert.ThrowsAsync<TaskCanceledException>(
                () => db.MultiGetAsync(new[] { "key" }, cancellationToken: cancelled.Token));

            // An uncancelled read on the same database still works afterwards.
            Assert.AreEqual("value", await db.GetAsync("key"));
        }
        finally
        {
            db.Dispose();
            DeleteQuietly(dbPath);
        }
    }

    [TestMethod]
    public async Task GetAsyncRejectsNullKeys()
    {
        using var db = OpenTempDb(out var dbPath);
        try
        {
            Assert.Throws<ArgumentNullException>(() => db.GetAsync((byte[])null));
            Assert.Throws<ArgumentNullException>(() => db.GetAsync((string)null));
            Assert.Throws<ArgumentNullException>(() => db.MultiGetAsync((string[])null));

            Assert.AreEqual(null, await db.GetAsync("anything"));
        }
        finally
        {
            db.Dispose();
            DeleteQuietly(dbPath);
        }
    }
}
