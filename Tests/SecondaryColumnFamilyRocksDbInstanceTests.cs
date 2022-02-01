using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;

namespace Tests
{
    [TestClass]
    public class SecondaryColumnFamilyRocksDbInstanceTests
    {
        private const string PRIMARY_DB_NAME = "test-primary";
        private const string SECONDARY_DB_NAME = "test-secondary";
        private RocksDb _primaryDb;
        private RocksDb _secondaryDb;
        private ColumnFamilyHandle _columnFamilyHandle;
        private ColumnFamilyHandle _columnFamilyHandleSecondary;

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
            var columnFamilies = new ColumnFamilies();
            columnFamilies.Add("TEST_COLUMN_FAMILY", new ColumnFamilyOptions());
            _primaryDb = RocksDb.Open(options, PRIMARY_DB_NAME, columnFamilies);
            _columnFamilyHandle = _primaryDb.GetColumnFamily("TEST_COLUMN_FAMILY");
            _primaryDb.Put("one", "uno", _columnFamilyHandle);
            _secondaryDb = RocksDb.OpenAsSecondary(options, PRIMARY_DB_NAME, SECONDARY_DB_NAME, columnFamilies);
            _columnFamilyHandleSecondary = _secondaryDb.GetColumnFamily("TEST_COLUMN_FAMILY");
        }

        [TestMethod]
        public void TestCatchUp()
        {
            Assert.AreEqual("uno", _secondaryDb.Get("one", _columnFamilyHandleSecondary));
            _primaryDb.Put("two", "dos", _columnFamilyHandle);
            Assert.IsNull(_secondaryDb.Get("two", _columnFamilyHandleSecondary));
            _secondaryDb.TryCatchUpWithPrimary();
            Assert.AreEqual("dos", _secondaryDb.Get("two", _columnFamilyHandleSecondary));
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