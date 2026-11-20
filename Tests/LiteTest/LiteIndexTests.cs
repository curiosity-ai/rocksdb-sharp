using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp.Lite;

namespace Tests.Lite;

public class Event
{
    public long Id { get; set; }
    public string Channel { get; set; } = "";
    public DateTime At { get; set; }
    public double Score { get; set; }
    public Guid CorrelationId { get; set; }
    public string? Owner { get; set; }
}

[TestClass]
public class LiteIndexTests
{
    private string _path = "";

    [TestInitialize]
    public void Init() => _path = Path.Combine(Path.GetTempPath(), "litedb-idx-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
    }

    [TestMethod]
    public void StringIndex_RangeScanReturnsSortedSubset()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("channel", e => e.Channel);

        foreach (var c in new[] { "delta", "alpha", "echo", "bravo", "charlie" })
            col.Insert(new Event { Channel = c });

        var slice = col.FindByIndexRange("channel", "b", "d").Select(e => e.Channel).ToArray();
        CollectionAssert.AreEqual(new[] { "bravo", "charlie" }, slice);

        var all = col.FindByIndexRange("channel", null, null).Select(e => e.Channel).ToArray();
        CollectionAssert.AreEqual(new[] { "alpha", "bravo", "charlie", "delta", "echo" }, all);
    }

    [TestMethod]
    public void DateTimeIndex_RangeScanChronological()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("at", e => e.At);

        var t0 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 10; i++)
            col.Insert(new Event { Channel = "x", At = t0.AddDays(i) });

        var window = col.FindByIndexRange("at", t0.AddDays(3), t0.AddDays(6)).Select(e => e.At).ToArray();
        Assert.AreEqual(4, window.Length);
        Assert.AreEqual(t0.AddDays(3), window.First());
        Assert.AreEqual(t0.AddDays(6), window.Last());
    }

    [TestMethod]
    public void DoubleIndex_HandlesNegativesAndZero()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("score", e => e.Score);

        foreach (var s in new[] { 3.14, -0.5, 0.0, -100.0, 1e6, 1e-6 })
            col.Insert(new Event { Score = s });

        var sorted = col.FindByIndexRange("score", null, null).Select(e => e.Score).ToArray();
        var expected = new[] { -100.0, -0.5, 0.0, 1e-6, 3.14, 1e6 };
        CollectionAssert.AreEqual(expected, sorted);
    }

    [TestMethod]
    public void GuidIndex_ExactLookup()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("corr", e => e.CorrelationId);

        var target = Guid.NewGuid();
        for (int i = 0; i < 5; i++) col.Insert(new Event { CorrelationId = Guid.NewGuid() });
        col.Insert(new Event { CorrelationId = target, Channel = "matching" });
        for (int i = 0; i < 5; i++) col.Insert(new Event { CorrelationId = Guid.NewGuid() });

        var found = col.FindByIndex("corr", target).Single();
        Assert.AreEqual("matching", found.Channel);
    }

    [TestMethod]
    public void MultipleIndexesOnSameCollection()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("channel", e => e.Channel);
        col.EnsureIndex("score", e => e.Score);

        col.Insert(new Event { Channel = "alpha", Score = 1.0 });
        col.Insert(new Event { Channel = "bravo", Score = 5.0 });
        col.Insert(new Event { Channel = "alpha", Score = 3.0 });

        Assert.AreEqual(2, col.FindByIndex("channel", "alpha").Count());
        var topScored = col.FindByIndexRange("score", 4.0, null).Single();
        Assert.AreEqual("bravo", topScored.Channel);

        CollectionAssert.AreEquivalent(new[] { "channel", "score" }, col.GetIndexNames().ToArray());
    }

    [TestMethod]
    public void IndexedNullValuesAreSkipped()
    {
        // selector returning null -> document should not appear in the index at all
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("owner", e => e.Owner);

        col.Insert(new Event { Channel = "with-owner", Owner = "alice" });
        col.Insert(new Event { Channel = "no-owner", Owner = null });
        col.Insert(new Event { Channel = "with-owner-2", Owner = "bob" });

        var indexed = col.FindByIndexRange("owner", null, null).Select(e => e.Channel).ToArray();
        CollectionAssert.AreEquivalent(new[] { "with-owner", "with-owner-2" }, indexed);

        // FindAll still sees them all
        Assert.AreEqual(3, col.FindAll().Count());
    }

    [TestMethod]
    public void RangeBoundsAreInclusive()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("score", e => e.Score);

        foreach (var s in new[] { 1.0, 2.0, 3.0, 4.0, 5.0 })
            col.Insert(new Event { Score = s });

        var inclusive = col.FindByIndexRange("score", 2.0, 4.0).Select(e => e.Score).ToArray();
        CollectionAssert.AreEqual(new[] { 2.0, 3.0, 4.0 }, inclusive);
    }

    [TestMethod]
    public void FindByIndex_EmptyResultWhenNoMatch()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("channel", e => e.Channel);
        col.Insert(new Event { Channel = "alpha" });

        var miss = col.FindByIndex("channel", "missing").ToArray();
        Assert.AreEqual(0, miss.Length);
    }

    [TestMethod]
    public void DuplicateIndexValues_ReturnsAllInIdOrder()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("channel", e => e.Channel);
        for (int i = 0; i < 5; i++)
            col.Insert(new Event { Channel = "same" });

        var ids = col.FindByIndex("channel", "same").Select(e => e.Id).ToArray();
        // ids are auto-assigned 1..5 and the suffix on the index key is the encoded id,
        // so iteration order must match id order
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5 }, ids);
    }

    [TestMethod]
    public void EnsureIndex_CalledTwiceReusesExistingIndex()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("channel", e => e.Channel);
        col.Insert(new Event { Channel = "alpha" });

        // second registration should not blow up nor duplicate entries
        col.EnsureIndex("channel", e => e.Channel);
        Assert.AreEqual(1, col.FindByIndex("channel", "alpha").Count());
        Assert.AreEqual(1, col.GetIndexNames().Count);
    }

    [TestMethod]
    public void DropIndex_AllowsRecreate()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("channel", e => e.Channel);
        col.Insert(new Event { Channel = "alpha" });
        col.Insert(new Event { Channel = "bravo" });

        col.DropIndex("channel");
        col.EnsureIndex("channel", e => e.Channel);

        // backfill should rebuild
        Assert.AreEqual(1, col.FindByIndex("channel", "alpha").Count());
        Assert.AreEqual(1, col.FindByIndex("channel", "bravo").Count());
    }

    [TestMethod]
    public void Index_OrderRemainsCorrectAfterUpdates()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("score", e => e.Score);

        var a = new Event { Score = 1.0 };
        var b = new Event { Score = 2.0 };
        var c = new Event { Score = 3.0 };
        col.Insert(a); col.Insert(b); col.Insert(c);

        // reorder by mutating scores
        b.Score = 5.0;
        col.Update(b);

        var sorted = col.FindByIndexRange("score", null, null).Select(e => e.Score).ToArray();
        CollectionAssert.AreEqual(new[] { 1.0, 3.0, 5.0 }, sorted);
    }

    [TestMethod]
    public void Index_DeleteAllClearsIndexEntries()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.EnsureIndex("channel", e => e.Channel);
        for (int i = 0; i < 10; i++) col.Insert(new Event { Channel = "c" + (i % 3) });

        var removed = col.DeleteAll();
        Assert.AreEqual(10, removed);
        Assert.AreEqual(0, col.Count());
        Assert.AreEqual(0, col.FindByIndexRange("channel", null, null).Count());
    }

    [TestMethod]
    public void UnknownIndexLookup_Throws()
    {
        using var db = new LiteDatabase(_path);
        var col = db.GetCollection<Event>("events");
        col.Insert(new Event { Channel = "x" });
        Assert.ThrowsExactly<LiteException>(() => col.FindByIndex("nope", "x").ToArray());
        Assert.ThrowsExactly<LiteException>(() => col.FindByIndexRange("nope", null, null).ToArray());
    }
}
