using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RocksDb.Tests
{
    [TestClass]
    public class RocksDbNiceApiTests
    {
        private string _tempPath;

        [AssemblyInitialize]
        public static async Task AssemblyInit(TestContext context)
        {
            await DownloadAndExtractNativeLibrary();
        }

        private static async Task DownloadAndExtractNativeLibrary()
        {
            string rid = null;
            string libName = null;

            if (OperatingSystem.IsLinux())
            {
                rid = "linux-x64";
                libName = "librocksdb.so";
            }
            else if (OperatingSystem.IsWindows())
            {
                rid = "win-x64";
                libName = "rocksdb.dll";
            }
            else if (OperatingSystem.IsMacOS())
            {
                rid = "osx-x64"; // or osx-arm64 depending on arch
                libName = "librocksdb.dylib";
            }
            else
            {
                Console.WriteLine("Unknown OS platform, skipping native lib download.");
                return;
            }

            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var libPath = Path.Combine(currentDir, libName);

            if (File.Exists(libPath))
            {
                Console.WriteLine($"Native library already exists at {libPath}");
                return;
            }

            string version = await GetLatestRocksDbVersion();
            if (string.IsNullOrEmpty(version))
            {
                // Fallback version if API fetch fails
                version = "10.4.2.63147";
                Console.WriteLine($"Failed to fetch latest version, using fallback: {version}");
            }

            string url = $"https://www.nuget.org/api/v2/package/RocksDb/{version}";

            Console.WriteLine($"Downloading RocksDB package version {version} from {url}...");

            using (var client = new HttpClient())
            {
                using (var response = await client.GetAsync(url))
                {
                    response.EnsureSuccessStatusCode();
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var archive = new ZipArchive(stream))
                    {
                        var entryPath = $"runtimes/{rid}/native/{libName}";
                        var entry = archive.GetEntry(entryPath);

                        if (entry != null)
                        {
                            Console.WriteLine($"Extracting {entryPath} to {libPath}...");
                            entry.ExtractToFile(libPath, overwrite: true);

                            // Set executable permissions on Linux/macOS
                            if (!OperatingSystem.IsWindows())
                            {
                                try
                                {
                                    File.SetUnixFileMode(libPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Failed to set unix file mode: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Could not find {entryPath} in the NuGet package.");
                        }
                    }
                }
            }
        }

        private static async Task<string> GetLatestRocksDbVersion()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    // Using a simpler query that parses the raw HTML or a lightweight API is better if we don't want to add JSON parsing libs
                    // But for robustness in a modern .NET app, we can use System.Text.Json
                    // Given the constraints, let's try the registration API which returns JSON.
                    // We need to parse it. Let's use basic string manipulation to avoid complex dependencies if simpler
                    // Or just use System.Text.Json since we are on .NET 10.

                    var response = await client.GetStringAsync("https://api.nuget.org/v3/registration5-semver1/rocksdb/index.json");
                    // Quick and dirty JSON parsing to find the latest version.
                    // The JSON structure has "items" -> "upper" (version). The last item in the list is usually the latest.
                    // However, sorting properly is hard without a library.
                    // Let's stick to a known working query if possible, or use a regex on the response.

                    // Regex to find all versions: "version":"(.*?)"
                    // Then sort and pick latest.

                    var versions = new List<Version>();
                    var matches = System.Text.RegularExpressions.Regex.Matches(response, "\"version\":\"([^\"]+)\"");
                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        if (Version.TryParse(match.Groups[1].Value, out var v))
                        {
                            versions.Add(v);
                        }
                    }

                    if (versions.Any())
                    {
                         // Sort descending
                         versions.Sort();
                         var latest = versions.Last();
                         // We need to find the original string representation because Version might normalize (e.g. 1.0 -> 1.0.0.0)
                         // effectively losing the NuGet specific string if it had non-standard parts, but RocksDbSharp uses standard 4-part versions.
                         // Let's iterate matches again to find the one that corresponds to the max version.

                         return latest.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching latest version: {ex.Message}");
            }
            return null;
        }

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
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
            {
                Assert.IsNotNull(db);
            }
        }

        [TestMethod]
        public void TestBasicCrudStrings()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
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
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
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
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
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
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
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
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
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

            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath, columnFamilies))
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
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
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
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
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
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
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
             using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
             {
                 var stats = db.GetProperty("rocksdb.stats");
                 Assert.IsNotNull(stats);
             }
        }

        [TestMethod]
        public void TestCompactRange()
        {
            var options = new DbOptions().SetCreateIfMissing(true);
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
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
            using (var db = RocksDbSharp.RocksDb.Open(options, _tempPath))
            {
                db.Put("key1", "value1");

                string checkpointPath = Path.Combine(_tempPath, "checkpoint");
                using (var cp = db.Checkpoint())
                {
                    cp.Save(checkpointPath);
                }

                // Verify checkpoint exists and can be opened
                using (var dbCheck = RocksDbSharp.RocksDb.Open(options, checkpointPath))
                {
                    Assert.AreEqual("value1", dbCheck.Get("key1"));
                }
            }
        }
    }
}
