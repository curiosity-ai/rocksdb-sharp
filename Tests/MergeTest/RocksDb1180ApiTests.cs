using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;

namespace Tests;

/// <summary>
/// Covers the API this binding gained for RocksDB 11.8.0. Every option here is read back
/// through its getter, which is what proves the native library actually carries the new C
/// API entry points rather than an older one that happened to be on the search path.
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
}
