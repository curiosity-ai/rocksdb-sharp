using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using RocksDbSharp;
using System.Threading;

Console.WriteLine("Hello World");

string tempRoot = Path.Combine(Path.GetTempPath(), "RocksDbMergeTest");

if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);

Directory.CreateDirectory(tempRoot);

var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(true).SetDisableAutoCompactions(1), tempRoot);

foreach (var i in Enumerable.Range(0, 10_000_00))
{
    db.Put(i.ToString(), i.ToString());
    if(i % 1000 == 0) Console.WriteLine(db.GetProperty("rocksdb.estimate-pending-compaction-bytes"));
    if (i % 10000 == 0) db.CompactRange(null, null, null);
}

db.Flush(new FlushOptions().SetWaitForFlush(true));

var cts = new CancellationTokenSource();
cts.CancelAfter(TimeSpan.FromSeconds(15));

while (!cts.IsCancellationRequested)
{
    Console.Write('.');
    using (var it = db.NewIterator())
    {
        it.SeekToFirst();
        while(it.Valid())
        {
            it.Next();
        }
    }
}

Console.WriteLine("Done!");
