using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Util
{
    public class ZipUtil
    {
        public static void ZipDirectoryToStreamAsync(string directory, Stream targetStream)
        {
            using var archive = new ZipArchive(targetStream, ZipArchiveMode.Create, true);
            AddToArchiveRecursively(archive, directory, "");
        }

        private static void AddToArchiveRecursively(ZipArchive archive, string directory, string baseDirectory)
        {
            foreach (var dir in Directory.GetDirectories(directory))
            {
                AddToArchiveRecursively(archive, dir, $"{baseDirectory}{Path.GetFileName(dir)}/");
            }

            var files = Directory.GetFiles(directory);
            foreach (var file in files)
            {
                archive.CreateEntryFromFile(file, $"{baseDirectory}{Path.GetFileName(file)}");
            }

            if (files.Length == 0 && baseDirectory != "")
            {
                archive.CreateEntry($"{baseDirectory}");
            }
        }
    }
}
