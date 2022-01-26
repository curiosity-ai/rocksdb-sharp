using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RocksDbSharp.Tests
{
    [TestClass]
    public class SecondaryRocksDbInstanceTests
    {
        private const string PRIMARY_DB_NAME = "test-primary";
        private const string SECONDARY_DB_NAME = "test-secondary";
        private RocksDb _primaryDb;
        private RocksDb _secondaryDb;


        [TestInitialize]
        public void InitializeTest()
        {
            if (Directory.Exists(PRIMARY_DB_NAME))
            {
                Directory.Delete(PRIMARY_DB_NAME, true);
            }

            if (Directory.Exists(SECONDARY_DB_NAME))
            {
                Directory.Delete(SECONDARY_DB_NAME, true);
            }

            var options = new DbOptions().SetCreateIfMissing().SetCreateMissingColumnFamilies();
            _primaryDb = RocksDb.Open(options, PRIMARY_DB_NAME);
            _primaryDb.Put("one", "uno");
            _secondaryDb = RocksDb.OpenAsSecondary(options, PRIMARY_DB_NAME, SECONDARY_DB_NAME);
        }

        [TestMethod]
        public void TestCatchUp()
        {
            Assert.AreEqual("uno", _secondaryDb.Get("one"));
            _primaryDb.Put("two", "dos");
            Assert.IsNull(_secondaryDb.Get("two"));
            _secondaryDb.TryCatchUpWithPrimary();
            Assert.AreEqual("dos", _secondaryDb.Get("two"));
        }

        [TestCleanup]
        public void CleanUpTest()
        {
            _primaryDb.Dispose();
            _secondaryDb.Dispose();
            if (Directory.Exists(PRIMARY_DB_NAME))
            {
                Directory.Delete(PRIMARY_DB_NAME, true);
            }

            if (Directory.Exists(SECONDARY_DB_NAME))
            {
                Directory.Delete(SECONDARY_DB_NAME, true);
            }
        }
    }
}