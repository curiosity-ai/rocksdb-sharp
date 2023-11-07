namespace RocksDbSharp.Tests;

[TestClass]
public class TransactionTests
{
    private string? _path;
    private TransactionDb? _db;

    [TestInitialize]
    public void TestInitialize()
    {
        _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var dbOptions = new DbOptions().SetCreateIfMissing();
        var transactionDbOptions = new TransactionDbOptions();
        _db = TransactionDb.Open(dbOptions, transactionDbOptions, _path);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _db?.Dispose();
        _db = null;

        if (Directory.Exists(_path))
            Directory.Delete(_path, true);
    }

    [TestMethod]
    public void key_should_be_visible_inside_transaction_while_uncommitted()
    {
        // Arrange
        const string key = "key1";
        const string value = "value1";

        using var tran = _db!.BeginTransaction();

        // Act
        tran.Put(key, value);
        var exists = tran.HasKey(key);

        // Assert
        Assert.IsTrue(exists);
    }

    [TestMethod]
    public void key_should_not_be_visible_outside_transaction_while_uncommitted()
    {
        // Arrange
        const string key = "key1";
        const string value = "value1";

        using var tran = _db!.BeginTransaction();

        // Act
        tran.Put(key, value);
        var exists = _db.HasKey(key);

        // Assert
        Assert.IsFalse(exists);
    }

    [TestMethod]
    public void key_should_be_visible_outside_transaction_after_commit()
    {
        // Arrange
        const string key = "key1";
        const string value = "value1";

        using var tran = _db!.BeginTransaction();

        // Act
        tran.Put(key, value);
        tran.Commit();
        var exists = _db.HasKey(key);

        // Assert
        Assert.IsTrue(exists);
    }

    [TestMethod]
    public void key_should_not_be_iterable_outside_transaction_while_uncommitted()
    {
        // Arrange
        const string key = "key1";
        const string value = "value1";

        using var tran = _db!.BeginTransaction();

        // Act
        tran.Put(key, value);
        using var iterator = _db.NewIterator().SeekToFirst();

        // Assert
        Assert.AreNotEqual(key, iterator.StringKey());
    }

    [TestMethod]
    public void key_should_be_iterable_outside_transaction_after_commit()
    {
        // Arrange
        const string key = "key1";
        const string value = "value1";

        using var tran = _db!.BeginTransaction();

        // Act
        tran.Put(key, value);
        tran.Commit();
        using var iterator = _db.NewIterator().SeekToFirst();

        // Assert
        Assert.AreEqual(key, iterator.StringKey());
    }

    [TestMethod]
    public void old_value_should_be_visible_outside_transaction_while_uncommitted()
    {
        // Arrange
        const string key = "key";
        const string value1 = "value1";
        const string value2 = "value2";
        _db!.Put(key, value1);

        using var tran = _db.BeginTransaction();

        // Act
        tran.Put(key, value2);
        var result = _db.Get(key);

        // Assert
        Assert.AreEqual(value1, result);
    }

    [TestMethod]
    public void new_value_should_be_visible_outside_transaction_after_commit()
    {
        // Arrange
        const string key = "key";
        const string value1 = "value1";
        const string value2 = "value2";
        _db!.Put(key, value1);

        using var tran = _db.BeginTransaction();

        // Act
        tran.Put(key, value2);
        tran.Commit();
        var result = _db.Get(key);

        // Assert
        Assert.AreEqual(value2, result);
    }
}