using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;

namespace Tests;

[TestClass]
public class FlushOptionsTests
{
    [TestMethod]
    public void FlushOptionsWrapsAFlushOptionsObject()
    {
        var options = new FlushOptions();

        // rocksdb defaults FlushOptions::wait to true. The class used to hand out the
        // rocksdb_options_t its base class created, whose first byte is
        // DBOptions::create_if_missing, so the default read back as false here and
        // every set/get went to a field of an unrelated struct.
        Assert.AreEqual(1, Native.Instance.rocksdb_flushoptions_get_wait(options.Handle),
            "a new FlushOptions should start out waiting, as rocksdb does");

        options.SetWaitForFlush(false);
        Assert.AreEqual(0, Native.Instance.rocksdb_flushoptions_get_wait(options.Handle));

        options.SetWaitForFlush(true);
        Assert.AreEqual(1, Native.Instance.rocksdb_flushoptions_get_wait(options.Handle));
    }

    [TestMethod]
    public void FlushWaitsForTheMemtableToReachDisk()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), dbPath);

            db.Put("key", "value");
            Assert.AreNotEqual("0", db.GetProperty("rocksdb.num-entries-active-mem-table"),
                "the write should be sitting in the memtable before the flush");

            db.Flush(new FlushOptions().SetWaitForFlush(true));

            // Both of these only hold once the flush has run to completion, which is
            // what SetWaitForFlush(true) asks for.
            Assert.AreEqual("0", db.GetProperty("rocksdb.num-entries-active-mem-table"),
                "the memtable should be empty once the flush has returned");
            Assert.AreNotEqual("0", db.GetProperty("rocksdb.num-files-at-level0"),
                "the flush should have produced a table file");
        }
        finally
        {
            if (Directory.Exists(dbPath))
            {
                Directory.Delete(dbPath, recursive: true);
            }
        }
    }
}
