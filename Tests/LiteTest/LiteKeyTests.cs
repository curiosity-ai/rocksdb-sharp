using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp.Lite;

namespace Tests.Lite;

[TestClass]
public class LiteKeyTests
{
    private static int LexCompare(byte[] a, byte[] b)
    {
        int min = Math.Min(a.Length, b.Length);
        for (int i = 0; i < min; i++)
        {
            int c = a[i] - b[i];
            if (c != 0) return c;
        }
        return a.Length - b.Length;
    }

    private static void AssertOrdered(IEnumerable<object?> values)
    {
        var encoded = values.Select(LiteKey.Encode).ToArray();
        for (int i = 1; i < encoded.Length; i++)
        {
            Assert.IsTrue(
                LexCompare(encoded[i - 1], encoded[i]) < 0,
                $"encoded[{i - 1}] should sort before encoded[{i}] for value {values.ElementAt(i - 1)} vs {values.ElementAt(i)}");
        }
    }

    [TestMethod]
    public void Encode_LongValuesSortNumerically()
    {
        var values = new object?[] { long.MinValue, -10000L, -1L, 0L, 1L, 10000L, long.MaxValue };
        AssertOrdered(values);
    }

    [TestMethod]
    public void Encode_IntValuesSortNumerically()
    {
        var values = new object?[] { int.MinValue, -1000, -1, 0, 1, 1000, int.MaxValue };
        AssertOrdered(values);
    }

    [TestMethod]
    public void Encode_DoubleValuesSortNumerically()
    {
        var values = new object?[] { double.NegativeInfinity, -1e10, -1.5, -0.0001, 0.0, 0.0001, 1.5, 1e10, double.PositiveInfinity };
        AssertOrdered(values);
    }

    [TestMethod]
    public void Encode_StringValuesSortLexicographically()
    {
        var values = new object?[] { "", "a", "aa", "ab", "b", "ba", "z" };
        AssertOrdered(values);
    }

    [TestMethod]
    public void Encode_DateTimeValuesSortChronologically()
    {
        var values = new object?[]
        {
            new DateTime(1900, 1, 1),
            new DateTime(2000, 1, 1),
            DateTime.UtcNow.Date,
            new DateTime(2100, 1, 1),
        };
        AssertOrdered(values);
    }

    [TestMethod]
    public void Encode_NullProducesSingleByte()
    {
        var bytes = LiteKey.Encode(null);
        Assert.AreEqual(1, bytes.Length);
    }

    [TestMethod]
    public void Encode_BoolsAreOrderedFalseThenTrue()
    {
        AssertOrdered(new object?[] { false, true });
    }

    [TestMethod]
    public void Encode_TypeTagsOrderConsistently()
    {
        // tags in ascending order: Null < False/True < Long < Double < DateTime < Guid < String < Bytes
        var values = new object?[]
        {
            null,
            false,
            true,
            42L,
            3.14,
            new DateTime(2024, 1, 1),
            Guid.NewGuid(),
            "hello",
            new byte[] { 1, 2, 3 },
        };
        AssertOrdered(values);
    }

    [TestMethod]
    public void Encode_StringsAreDistinctFromTheirPrefix()
    {
        // "foo" should not be a byte-prefix of "foobar" once encoded (due to NUL terminator)
        var foo = LiteKey.Encode("foo");
        var foobar = LiteKey.Encode("foobar");
        // they share at most the leading "foo" portion before "foo"'s terminator differs from foobar's 'b'
        bool fullPrefix = true;
        for (int i = 0; i < foo.Length && fullPrefix; i++)
            if (i >= foobar.Length || foo[i] != foobar[i]) fullPrefix = false;
        Assert.IsFalse(fullPrefix, "Encoded 'foo' must not be a strict prefix of encoded 'foobar' or range scans would misclassify.");
    }

    [TestMethod]
    public void Encode_StringWithEmbeddedNulIsRejected()
    {
        Assert.ThrowsExactly<LiteException>(() => LiteKey.Encode("a\0b"));
    }

    [TestMethod]
    public void Encode_GuidIsSeventeenBytes()
    {
        var bytes = LiteKey.Encode(Guid.NewGuid());
        Assert.AreEqual(17, bytes.Length);
    }

    [TestMethod]
    public void Encode_ByteArrayRoundTripsLengthAndContent()
    {
        var payload = new byte[] { 0x00, 0xFF, 0x10, 0x00, 0xAB };
        var bytes = LiteKey.Encode(payload);
        Assert.AreEqual(1 + 4 + payload.Length, bytes.Length);
    }

    [TestMethod]
    public void Encode_UnsupportedTypeFallsBackToString()
    {
        var bytes = LiteKey.Encode(TimeSpan.FromHours(1));
        // first byte must be the string tag (0x70)
        Assert.AreEqual(0x70, bytes[0]);
    }
}
