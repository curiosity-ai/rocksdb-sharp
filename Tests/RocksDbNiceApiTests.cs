using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Tests
{
    [TestClass]
    public class RocksDbNiceApiTests
    {
        private string _tempPath;

        [TestInitialize]
        public void Initialize()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), "RocksDbTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempPath);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempPath))
            {
                try
                {
                    Directory.Delete(_tempPath, true);
                }
                catch { /* Ignore cleanup errors */ }
            }
        }

        [TestMethod]
        public void TestLifecycle()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDb.Open(options, _tempPath))
            {
                Assert.IsNotNull(db);
            }
        }

        [TestMethod]
        public void TestBasicCrudStrings()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDb.Open(options, _tempPath))
            {
                db.Put("key1", "value1");
                var val = db.Get("key1");
                Assert.AreEqual("value1", val);

                db.Remove("key1");
                var val2 = db.Get("key1");
                Assert.IsNull(val2);
            }
        }

        [TestMethod]
        public void TestBasicCrudBytes()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDb.Open(options, _tempPath))
            {
                byte[] key = Encoding.UTF8.GetBytes("key1");
                byte[] value = Encoding.UTF8.GetBytes("value1");

                db.Put(key, value);
                byte[] res = db.Get(key);
                CollectionAssert.AreEqual(value, res);

                db.Remove(key);
                byte[] res2 = db.Get(key);
                Assert.IsNull(res2);
            }
        }

        [TestMethod]
        public void TestWriteBatch()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDb.Open(options, _tempPath))
            {
                using (var batch = new WriteBatch())
                {
                    batch.Put("key1", "value1");
                    batch.Put("key2", "value2");
                    batch.Delete(Encoding.UTF8.GetBytes("key1"));

                    db.Write(batch);
                }

                Assert.IsNull(db.Get("key1"));
                Assert.AreEqual("value2", db.Get("key2"));
            }
        }

        [TestMethod]
        public void TestIteration()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDb.Open(options, _tempPath))
            {
                for (int i = 0; i < 5; i++)
                {
                    db.Put($"key{i}", $"value{i}");
                }

                using (var iterator = db.NewIterator())
                {
                    iterator.SeekToFirst();
                    Assert.IsTrue(iterator.Valid());

                    int count = 0;
                    while (iterator.Valid())
                    {
                        var key = iterator.StringKey();
                        var value = iterator.StringValue();
                        Assert.AreEqual($"key{count}", key); // keys should be sorted lexicographically
                        Assert.AreEqual($"value{count}", value);

                        iterator.Next();
                        count++;
                    }
                    Assert.AreEqual(5, count);
                }
            }
        }

        [TestMethod]
        public void TestColumnFamilies()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            // Open DB initially
            using (var db = RocksDb.Open(options, _tempPath))
            {
                var cf = db.CreateColumnFamily(new ColumnFamilyOptions(), "cf1");
                db.Put("key1", "value1", cf);
                db.Put("key1", "valueDefault"); // Default CF

                Assert.AreEqual("value1", db.Get("key1", cf));
                Assert.AreEqual("valueDefault", db.Get("key1")); // Default CF
            }

            // Reopen with Column Families
            var cfOptions = new ColumnFamilyOptions();
            var columnFamilies = new ColumnFamilies();
            columnFamilies.Add("cf1", cfOptions);

            using (var db = RocksDb.Open(options, _tempPath, columnFamilies))
            {
                var cf1 = db.GetColumnFamily("cf1");
                Assert.AreEqual("value1", db.Get("key1", cf1));
                Assert.AreEqual("valueDefault", db.Get("key1"));

                db.DropColumnFamily("cf1");
            }
        }

        [TestMethod]
        public void TestSnapshots()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDb.Open(options, _tempPath))
            {
                db.Put("key1", "value1");

                using (var snapshot = db.CreateSnapshot())
                {
                    var readOptions = new ReadOptions().SetSnapshot(snapshot);

                    db.Put("key1", "value2");

                    Assert.AreEqual("value2", db.Get("key1"));
                    Assert.AreEqual("value1", db.Get("key1", null, readOptions));
                }
            }
        }

        [TestMethod]
        public void TestMultiGet()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDb.Open(options, _tempPath))
            {
                db.Put("key1", "value1");
                db.Put("key2", "value2");

                var result = db.MultiGet(new string[] { "key1", "key2", "key3" });

                Assert.AreEqual(3, result.Length);
                Assert.AreEqual("key1", result[0].Key);
                Assert.AreEqual("value1", result[0].Value);
                Assert.AreEqual("key2", result[1].Key);
                Assert.AreEqual("value2", result[1].Value);
                Assert.AreEqual("key3", result[2].Key);
                Assert.IsNull(result[2].Value);
            }
        }

        [TestMethod]
        public void TestMetadata()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDb.Open(options, _tempPath))
            {
                db.Put("key1", "value1");
                db.Flush(new FlushOptions().SetWaitForFlush(true));

                var liveFiles = db.GetLiveFileNames();
                Assert.IsTrue(liveFiles.Count > 0);

                var metadata = db.GetLiveFilesMetadata();
                Assert.IsTrue(metadata.Count > 0);
                Assert.IsNotNull(metadata[0].FileMetadata.FileName);
            }
        }

        [TestMethod]
        public void TestProperties()
        {
             var options = new DbOptions().SetCreateIfMissing(true);
             using (var db = RocksDb.Open(options, _tempPath))
             {
                 var stats = db.GetProperty("rocksdb.stats");
                 Assert.IsNotNull(stats);
             }
        }

        [TestMethod]
        public void TestCompactRange()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDb.Open(options, _tempPath))
            {
                db.Put("key1", "value1");
                db.Put("key2", "value2");

                db.CompactRange((string)null, (string)null); // Compact entire range

                Assert.AreEqual("value1", db.Get("key1"));
                Assert.AreEqual("value2", db.Get("key2"));
            }
        }

        [TestMethod]
        public void TestCheckpoint()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDb.Open(options, _tempPath))
            {
                db.Put("key1", "value1");

                string checkpointPath = Path.Combine(_tempPath, "checkpoint");
                using (var cp = db.Checkpoint())
                {
                    cp.Save(checkpointPath);
                }

                // Verify checkpoint exists and can be opened
                using (var dbCheck = RocksDb.Open(options, checkpointPath))
                {
                    Assert.AreEqual("value1", dbCheck.Get("key1"));
                }
            }
        }
    }
}
