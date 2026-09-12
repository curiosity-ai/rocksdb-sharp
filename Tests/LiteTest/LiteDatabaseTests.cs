using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp.Lite;

namespace Tests.Lite;

public class User
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public int Age { get; set; }
}

public class GuidDoc
{
    [LiteId]
    public Guid Key { get; set; }
    public string Value { get; set; } = "";
}

[TestClass]
public class LiteDatabaseTests
{
    private string _path = "";

    [TestInitialize]
    public void Init() => _path = Path.Combine(Path.GetTempPath(), "litedb-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
    }

    [TestMethod]
    public void Insert_AutoAssignsLongId()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");

        var alice = new User { Name = "Alice", Email = "alice@example.com", Age = 30 };
        var id1 = col.Insert(alice);
        var bob = new User { Name = "Bob", Email = "bob@example.com", Age = 22 };
        var id2 = col.Insert(bob);

        Assert.AreEqual(1L, id1);
        Assert.AreEqual(2L, id2);
        Assert.AreEqual(1L, alice.Id);
        Assert.AreEqual(2L, bob.Id);
    }

    [TestMethod]
    public void FindById_ReturnsInsertedDocument()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");

        var alice = new User { Name = "Alice", Email = "alice@example.com", Age = 30 };
        col.Insert(alice);

        var fetched = col.FindById(alice.Id);
        Assert.IsNotNull(fetched);
        Assert.AreEqual("Alice", fetched!.Name);
        Assert.AreEqual("alice@example.com", fetched.Email);
        Assert.AreEqual(30, fetched.Age);
    }

    [TestMethod]
    public void FindById_ReturnsNullForMissing()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        Assert.IsNull(col.FindById(999L));
    }

    [TestMethod]
    public void Update_ReplacesExistingDocument()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");

        var u = new User { Name = "Alice", Age = 30 };
        col.Insert(u);
        u.Age = 31;
        Assert.IsTrue(col.Update(u));

        var refetched = col.FindById(u.Id);
        Assert.AreEqual(31, refetched!.Age);
    }

    [TestMethod]
    public void Update_ReturnsFalseWhenMissing()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        Assert.IsFalse(col.Update(new User { Id = 42, Name = "Ghost" }));
    }

    [TestMethod]
    public void Upsert_CreatesOrReplaces()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");

        col.Upsert(new User { Id = 5, Name = "Five" });
        col.Upsert(new User { Id = 5, Name = "Five-replaced" });
        Assert.AreEqual("Five-replaced", col.FindById(5L)!.Name);
    }

    [TestMethod]
    public void Delete_RemovesAndReturnsFalseOnSecondCall()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        var u = new User { Name = "X" };
        col.Insert(u);
        Assert.IsTrue(col.Delete(u.Id));
        Assert.IsFalse(col.Delete(u.Id));
        Assert.IsNull(col.FindById(u.Id));
    }

    [TestMethod]
    public void Count_AndContains()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        col.Insert(new User { Name = "a" });
        col.Insert(new User { Name = "b" });
        col.Insert(new User { Name = "c" });
        Assert.AreEqual(3, col.Count());
        Assert.IsTrue(col.Contains(2L));
        Assert.IsFalse(col.Contains(999L));
    }

    [TestMethod]
    public void FindAll_IteratesInIdOrder()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        for (int i = 0; i < 10; i++) col.Insert(new User { Name = "u" + i });
        var ids = col.FindAll().Select(u => u.Id).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1, 10).Select(i => (long)i).ToArray(), ids);
    }

    [TestMethod]
    public void Find_FiltersInMemory()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        col.Insert(new User { Name = "Alice", Age = 30 });
        col.Insert(new User { Name = "Bob", Age = 22 });
        col.Insert(new User { Name = "Charlie", Age = 40 });
        var adults = col.Find(u => u.Age >= 30).Select(u => u.Name).OrderBy(n => n).ToArray();
        CollectionAssert.AreEqual(new[] { "Alice", "Charlie" }, adults);
    }

    [TestMethod]
    public void EnsureIndex_PopulatesAndAllowsExactLookup()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        col.Insert(new User { Name = "Alice", Email = "alice@example.com" });
        col.Insert(new User { Name = "Bob", Email = "bob@example.com" });
        col.Insert(new User { Name = "Eve", Email = "eve@example.com" });

        col.EnsureIndex("email", u => u.Email);

        var bob = col.FindByIndex("email", "bob@example.com").Single();
        Assert.AreEqual("Bob", bob.Name);
    }

    [TestMethod]
    public void Index_MaintainedAcrossInsertsUpdatesDeletes()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        col.EnsureIndex("email", u => u.Email);

        var u = new User { Name = "Alice", Email = "old@example.com" };
        col.Insert(u);
        Assert.AreEqual(1, col.FindByIndex("email", "old@example.com").Count());

        u.Email = "new@example.com";
        col.Update(u);
        Assert.AreEqual(0, col.FindByIndex("email", "old@example.com").Count());
        Assert.AreEqual(1, col.FindByIndex("email", "new@example.com").Count());

        col.Delete(u.Id);
        Assert.AreEqual(0, col.FindByIndex("email", "new@example.com").Count());
    }

    [TestMethod]
    public void FindByIndexRange_OnNumericReturnsInOrder()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        col.EnsureIndex("age", u => u.Age);
        col.Insert(new User { Name = "a", Age = 10 });
        col.Insert(new User { Name = "b", Age = 25 });
        col.Insert(new User { Name = "c", Age = 40 });
        col.Insert(new User { Name = "d", Age = 55 });
        col.Insert(new User { Name = "negative", Age = -5 });

        var inRange = col.FindByIndexRange("age", 0, 30).Select(u => u.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "a", "b" }, inRange);

        var withNegatives = col.FindByIndexRange("age", -100, 100).Select(u => u.Age).ToArray();
        CollectionAssert.AreEqual(new[] { -5, 10, 25, 40, 55 }, withNegatives);
    }

    [TestMethod]
    public void FindByIndexRange_OpenBounds()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        col.EnsureIndex("age", u => u.Age);
        col.Insert(new User { Name = "a", Age = 10 });
        col.Insert(new User { Name = "b", Age = 25 });
        col.Insert(new User { Name = "c", Age = 40 });

        var lowerOpen = col.FindByIndexRange("age", null, 30).Select(u => u.Age).ToArray();
        CollectionAssert.AreEqual(new[] { 10, 25 }, lowerOpen);

        var upperOpen = col.FindByIndexRange("age", 20, null).Select(u => u.Age).ToArray();
        CollectionAssert.AreEqual(new[] { 25, 40 }, upperOpen);
    }

    [TestMethod]
    public void IndexBackfill_OnExistingData()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        col.Insert(new User { Name = "Alice", Age = 30 });
        col.Insert(new User { Name = "Bob", Age = 22 });

        // index created AFTER data was inserted should backfill
        col.EnsureIndex("age", u => u.Age);
        var young = col.FindByIndexRange("age", 0, 25).Single();
        Assert.AreEqual("Bob", young.Name);
    }

    [TestMethod]
    public void DropIndex_RemovesIndex()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        col.EnsureIndex("age", u => u.Age);
        col.Insert(new User { Name = "Alice", Age = 30 });
        Assert.AreEqual(1, col.GetIndexNames().Count);
        col.DropIndex("age");
        Assert.AreEqual(0, col.GetIndexNames().Count);
        Assert.ThrowsExactly<LiteException>(() => col.FindByIndex("age", 30).ToArray());
    }

    [TestMethod]
    public void IndexPersistsAcrossReopen()
    {
        using (var db = new LiteDatabase(_path))
        {
            var col = db.GetCollection<User>("users");
            col.EnsureIndex("email", u => u.Email);
            col.Insert(new User { Name = "Alice", Email = "alice@example.com" });
            col.Insert(new User { Name = "Bob", Email = "bob@example.com" });
        }

        using (var db = new LiteDatabase(_path))
        {
            CollectionAssert.Contains(db.GetCollectionNames().ToArray(), "users");
            var col = db.GetCollection<User>("users");

            // re-register selector so future writes maintain it; existing entries still queryable
            col.EnsureIndex("email", u => u.Email);
            var bob = col.FindByIndex("email", "bob@example.com").Single();
            Assert.AreEqual("Bob", bob.Name);
        }
    }

    [TestMethod]
    public void DropCollection_RemovesEverything()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        col.EnsureIndex("email", u => u.Email);
        col.Insert(new User { Name = "Alice", Email = "a@x" });

        Assert.IsTrue(db.CollectionExists("users"));
        db.DropCollection("users");
        Assert.IsFalse(db.CollectionExists("users"));

        // autoincrement counter is reset
        var col2 = db.GetCollection<User>("users");
        var id = col2.Insert(new User { Name = "Fresh" });
        Assert.AreEqual(1L, id);
    }

    [TestMethod]
    public void GuidIdsAreAutoAssigned()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<GuidDoc>("guids");
        var d = new GuidDoc { Value = "hello" };
        var id = col.Insert(d);
        Assert.IsInstanceOfType(id, typeof(Guid));
        Assert.AreNotEqual(Guid.Empty, (Guid)id);
        Assert.AreEqual(d.Key, (Guid)id);
        Assert.AreEqual("hello", col.FindById(d.Key)!.Value);
    }

    [TestMethod]
    public void DuplicateInsertThrows()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<User>("users");
        col.Upsert(new User { Id = 7, Name = "first" });
        Assert.ThrowsExactly<LiteException>(() => col.Insert(new User { Id = 7, Name = "second" }));
    }

    [TestMethod]
    public void MultipleCollectionsAreIndependent()
    {
        using var db = new LiteDatabase(_path);
        var users = db.GetCollection<User>("users");
        var admins = db.GetCollection<User>("admins");
        users.Insert(new User { Name = "u1" });
        admins.Insert(new User { Name = "a1" });
        admins.Insert(new User { Name = "a2" });
        Assert.AreEqual(1, users.Count());
        Assert.AreEqual(2, admins.Count());
    }

    [TestMethod]
    public void Checkpoint_CreatesUsableSnapshot()
    {
        var cpPath = Path.Combine(Path.GetTempPath(), "litedb-cp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var db = new LiteDatabase(_path))
            {
                var col = db.GetCollection<User>("users");
                col.Insert(new User { Name = "snap" });
                db.Checkpoint(cpPath);
            }

            using (var db = new LiteDatabase(cpPath))
            {
                var col = db.GetCollection<User>("users");
                Assert.AreEqual(1, col.Count());
                Assert.AreEqual("snap", col.FindAll().Single().Name);
            }
        }
        finally
        {
            if (Directory.Exists(cpPath)) Directory.Delete(cpPath, recursive: true);
        }
    }
}
