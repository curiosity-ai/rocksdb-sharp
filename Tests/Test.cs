using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using RocksDbSharp;

Console.WriteLine("Hello World");
var path = "test";
Directory.CreateDirectory(path);
var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(true).SetDisableAutoCompactions(1), "test");

foreach (var i in Enumerable.Range(0, 10_000_00))
{
    db.Put(i.ToString(), i.ToString());
    if(i % 1000 == 0) Console.WriteLine(db.GetProperty("rocksdb.estimate-pending-compaction-bytes"));
    if (i % 10000 == 0) db.CompactRange(null, null, null);
}


db.Flush(new FlushOptions().SetWaitForFlush(true));

while (true)
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