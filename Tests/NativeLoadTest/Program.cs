// Regression test for the native library being loaded into a process started through an apphost.
//
// The Linux librocksdb.so links libstdc++ statically. Up to 11.12.0.7385 it also re-exported every std:: symbol
// of that static copy. Under the .NET 11 apphost the system libstdc++.so.6 is not in the global symbol scope
// when librocksdb is loaded (the .NET 10 apphost and the `dotnet` muxer do put it there), so the dynamic linker
// binds librocksdb's std:: references partly to its own copy and partly to the system one, locale facet lookups
// fail, and every numeric insertion into a C++ stream throws std::bad_cast inside librocksdb. Visible effects:
// BackupEngine writes empty meta/<id> files, and the numbers vanish from the EventLogger lines of the info LOG.
//
// Usage: NativeLoadTest [work dir]. Exit code 0 when everything checks out, 1 otherwise.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RocksDbSharp;

var root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "rocksdb-native-load-" + Guid.NewGuid().ToString("N"));
var dbPath = Path.Combine(root, "db");
var backupPath = Path.Combine(root, "backup");
Directory.CreateDirectory(root);

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
    if (!ok) failures++;
}

try
{
    Console.WriteLine($"runtime   : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})");
    Console.WriteLine($"process   : {Environment.ProcessPath}");

    var options = new DbOptions().SetCreateIfMissing(true);
    using (var db = RocksDb.Open(options, dbPath))
    {
        Console.WriteLine($"native lib: {NativeImport.Auto.LoadedPath}");

        for (int i = 0; i < 1000; i++)
            db.Put(Encoding.UTF8.GetBytes($"key{i:D6}"), Encoding.UTF8.GetBytes($"value{i}"));
        db.Flush(new FlushOptions().SetWaitForFlush(true));

        var backupOptions = Native.Instance.rocksdb_options_create();
        var engine = Native.Instance.rocksdb_backup_engine_open(backupOptions, backupPath);
        try
        {
            Native.Instance.rocksdb_backup_engine_create_new_backup(engine, db.Handle);
        }
        finally
        {
            Native.Instance.rocksdb_backup_engine_close(engine);
            Native.Instance.rocksdb_options_destroy(backupOptions);
        }
    }

    // BackupEngine::BackupMeta::StoreToFile formats the meta file with an ostringstream.
    var metaFiles = Directory.Exists(Path.Combine(backupPath, "meta"))
        ? Directory.GetFiles(Path.Combine(backupPath, "meta")).Where(f => !f.EndsWith(".tmp")).ToArray()
        : Array.Empty<string>();
    Check(metaFiles.Length == 1, $"one backup meta file ({metaFiles.Length} found)");
    foreach (var meta in metaFiles)
    {
        var text = File.ReadAllText(meta);
        Check(text.Length > 0, $"backup meta {Path.GetFileName(meta)} is non-empty ({text.Length} bytes)");
        Check(Regex.IsMatch(text, @"^\d+\n\d+\n"), "backup meta starts with timestamp and sequence number");
    }

    // The native side must be able to read its own backup back.
    var verifyOptions = Native.Instance.rocksdb_options_create();
    var verifyEngine = Native.Instance.rocksdb_backup_engine_open(verifyOptions, backupPath);
    try
    {
        var info = Native.Instance.rocksdb_backup_engine_get_backup_info(verifyEngine);
        int count = Native.Instance.rocksdb_backup_engine_info_count(info);
        Native.Instance.rocksdb_backup_engine_info_destroy(info);
        Check(count == 1, $"native backup engine lists the backup ({count} found)");
    }
    finally
    {
        Native.Instance.rocksdb_backup_engine_close(verifyEngine);
        Native.Instance.rocksdb_options_destroy(verifyOptions);
    }

    var optionsFile = Directory.GetFiles(dbPath, "OPTIONS-*").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
    Check(optionsFile is not null, "OPTIONS file written");
    if (optionsFile is not null)
    {
        var text = File.ReadAllText(optionsFile);
        Check(Regex.IsMatch(text, @"write_buffer_size=\d+"), "OPTIONS file contains write_buffer_size=<number>");
        Check(Regex.IsMatch(text, @"max_open_files=-?\d+"), "OPTIONS file contains max_open_files=<number>");
    }

    // EventLogger (JSONWriter over a std::ostringstream) is the part of the LOG that streams numbers.
    var log = File.ReadAllText(Path.Combine(dbPath, "LOG"));
    Check(Regex.IsMatch(log, "\"event\": \"flush_started\""), "LOG contains the flush_started event");
    Check(Regex.IsMatch(log, "\"num_memtables\": \\d+"), "LOG event lines contain numbers (num_memtables)");
    Check(Regex.IsMatch(log, "\"file_number\": \\d+"), "LOG event lines contain numbers (file_number)");
}
catch (Exception e)
{
    Console.WriteLine($"FAIL: {e}");
    failures++;
}
finally
{
    if (args.Length == 0)
    {
        try { Directory.Delete(root, true); } catch { /* best effort */ }
    }
}

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
