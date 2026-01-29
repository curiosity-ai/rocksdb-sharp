using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;

namespace RocksDb.Tests;

[TestClass]
public class CustomMergeOperatorTests
{
    [TestMethod]
    public void CustomMergeOperatorAppendsMultipleValues()
    {
        var mergeOp = MergeOperators.Create(
            "StringAppend",
            (ReadOnlySpan<byte> key, MergeOperators.OperandsEnumerator operands, out bool success) =>
            {
                var result = new List<byte>();
                for (int i = 0; i < operands.Count; i++)
                {
                    result.AddRange(operands.Get(i).ToArray());
                }
                success = true;
                return result.ToArray();
            },
            (ReadOnlySpan<byte> key, bool hasExisting, ReadOnlySpan<byte> existing,
                MergeOperators.OperandsEnumerator operands, out bool success) =>
            {
                var result = hasExisting ? existing.ToArray().ToList() : new List<byte>();
                for (int i = 0; i < operands.Count; i++)
                {
                    result.AddRange(operands.Get(i).ToArray());
                }
                success = true;
                return result.ToArray();
            });

        var opts = new ColumnFamilyOptions().SetMergeOperator(mergeOp);
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            using var db = RocksDbSharp.RocksDb.Open(new DbOptions().SetCreateIfMissing(),
                dbPath,
                new ColumnFamilies(opts));

            db.Merge("key"u8.ToArray(), "hello"u8.ToArray());
            db.Merge("key"u8.ToArray(), "world"u8.ToArray());

            var value = db.Get("key"u8.ToArray());

            Assert.IsNotNull(value);
            Assert.AreEqual("helloworld", Encoding.UTF8.GetString(value));
        }
        finally
        {
            if (Directory.Exists(dbPath))
            {
                Directory.Delete(dbPath, recursive: true);
            }
        }
    }
}