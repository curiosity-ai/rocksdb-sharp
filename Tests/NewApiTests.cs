using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;

namespace Tests
{
    [TestClass]
    public class NewApiTests
    {
        public TestContext TestContext { get; set; }

        private string GetTempPath()
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        }

        [TestMethod]
        public void SingleDelete_Works()
        {
            var dbPath = GetTempPath();
            try
            {
                var options = new DbOptions().SetCreateIfMissing(true);
                using (var db = RocksDb.Open(options, dbPath))
                {
                    db.Put("key1", "value1");
                    Assert.AreEqual("value1", db.Get("key1"));

                    db.SingleDelete("key1");
                    Assert.IsNull(db.Get("key1"));
                }
            }
            finally
            {
                if (Directory.Exists(dbPath))
                {
                    Directory.Delete(dbPath, true);
                }
            }
        }

        [TestMethod]
        public void DeleteRange_Works()
        {
            var dbPath = GetTempPath();
            try
            {
                var options = new DbOptions().SetCreateIfMissing(true);
                using (var db = RocksDb.Open(options, dbPath))
                {
                    db.Put("a", "1");
                    db.Put("b", "2");
                    db.Put("c", "3");
                    db.Put("d", "4");
                    db.Put("e", "5");

                    // Delete range [b, d) -> deletes b, c. d remains.
                    db.DeleteRange("b", "d");

                    Assert.AreEqual("1", db.Get("a"));
                    Assert.IsNull(db.Get("b"));
                    Assert.IsNull(db.Get("c"));
                    Assert.AreEqual("4", db.Get("d"));
                    Assert.AreEqual("5", db.Get("e"));
                }
            }
            finally
            {
                if (Directory.Exists(dbPath))
                {
                    Directory.Delete(dbPath, true);
                }
            }
        }

        [TestMethod]
        public void SstFileWriter_DeleteRange_Works()
        {
            var dbPath = GetTempPath();
            var sstPath = Path.Combine(GetTempPath(), "test.sst");
            Directory.CreateDirectory(Path.GetDirectoryName(sstPath));

            try
            {
                var options = new DbOptions().SetCreateIfMissing(true);
                using (var db = RocksDb.Open(options, dbPath))
                {
                    db.Put("key1", "value1");
                    db.Put("key2", "value2");
                    db.Put("key3", "value3");

                    // Create SST file with range deletion [key1, key3) -> delete key1, key2
                    var envOptions = new EnvOptions();
                    var ioOptions = new ColumnFamilyOptions();
                    using (var writer = new SstFileWriter(envOptions, ioOptions))
                    {
                        writer.Open(sstPath);
                        writer.DeleteRange(Encoding.UTF8.GetBytes("key1"), Encoding.UTF8.GetBytes("key3"));
                        writer.Finish();
                    }

                    db.IngestExternalFiles(new[] { sstPath }, new IngestExternalFileOptions());

                    Assert.IsNull(db.Get("key1"));
                    Assert.IsNull(db.Get("key2"));
                    Assert.AreEqual("value3", db.Get("key3"));
                }
            }
            finally
            {
                if (Directory.Exists(dbPath))
                {
                    Directory.Delete(dbPath, true);
                }
                if (File.Exists(sstPath))
                {
                    File.Delete(sstPath);
                }
                var sstDir = Path.GetDirectoryName(sstPath);
                if (Directory.Exists(sstDir))
                {
                    Directory.Delete(sstDir, true);
                }
            }
        }
    }
}
