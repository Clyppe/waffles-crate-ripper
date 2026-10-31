using System;
using System.Collections.Generic;
using System.IO;
using File = Waffles.CrateRipper.LongPathFile;
using Directory = Waffles.CrateRipper.LongPathDirectory;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace Waffles.CrateRipper
{
    /// <summary>Copies an explicit bundle dependency set without scanning its directory.</summary>
    public static class BundleStaging
    {
        private static readonly string[] OwnSidecars = { ".resS", ".resource", ".resources" };

        private sealed class Entry
        {
            public string sourcePath;
            public string filename;
            public long bytes;
        }

        public static string Stage(IEnumerable<string> files, string freshFolder, CancellationToken cancellation,
            IProgress<string> progress = null)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            if (string.IsNullOrWhiteSpace(freshFolder)) throw new ArgumentException("A fresh staging folder is required.", nameof(freshFolder));
            cancellation.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(freshFolder);
            AssertSafeParents(destination);
            if (File.Exists(destination) || (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()))
                throw new IOException("The staging folder already contains files: " + destination);

            var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entriesByPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var selected in files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(selected)) throw new ArgumentException("A selected bundle path is empty.", nameof(files));
                var full = Path.GetFullPath(selected);
                AddFile(full, uniquePaths, entriesByPath);
                foreach (var extension in OwnSidecars)
                {
                    var companion = full + extension;
                    if (File.Exists(companion)) AddFile(companion, uniquePaths, entriesByPath);
                }
            }
            if (entriesByPath.Count == 0) throw new ArgumentException("Select at least one bundle or dependency file.", nameof(files));
            AssignStagedPaths(entriesByPath.Values);

            var temporary = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".staging-" + Guid.NewGuid().ToString("N");
            if (entriesByPath.Values.Any(entry => IsInside(temporary, entry.sourcePath) || IsInside(destination, entry.sourcePath)))
                throw new IOException("Selected source files cannot be inside the staging output.");
            AssertSafeParents(temporary);
            Directory.CreateDirectory(temporary);
            try
            {
                var entries = entriesByPath.Values.OrderBy(entry => entry.filename, StringComparer.Ordinal).ToList();
                foreach (var entry in entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    progress?.Report("Staging " + entry.filename);
                    var stagedPath = Path.Combine(temporary, entry.filename);
                    Directory.CreateDirectory(Path.GetDirectoryName(stagedPath));
                    // Chunked copies make Cancel responsive even for large bundles.
                    var buffer = new byte[128 * 1024];
                    using (var input = new FileStream(LongPathIO.ForIO(entry.sourcePath), FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(LongPathIO.ForIO(stagedPath), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, count);
                        }
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                File.WriteAllText(Path.Combine(temporary, "selection-manifest.json"), JsonConvert.SerializeObject(new
                {
                    copiedFiles = entries.Count,
                    copiedBytes = entries.Sum(entry => entry.bytes),
                    files = entries
                }, Formatting.Indented));
                AssertSafeParents(destination);
                if (Directory.Exists(destination))
                {
                    if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("The staging output changed during copying.");
                    Directory.Delete(destination, false);
                }
                cancellation.ThrowIfCancellationRequested();
                Directory.Move(temporary, destination);
                return freshFolder;
            }
            finally
            {
                // Only this invocation's random staging directory is removed.
                if (Directory.Exists(temporary))
                {
                    AssertSafeParents(temporary);
                    Directory.Delete(temporary, true);
                }
            }
        }

        private static void AddFile(string source, HashSet<string> uniquePaths, Dictionary<string, Entry> entriesByPath)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("Selected dependency file does not exist.", source);
            AssertSafeParents(source);
            if (!uniquePaths.Add(source)) return;
            var filename = Path.GetFileName(source);
            if (string.Equals(filename, "selection-manifest.json", StringComparison.OrdinalIgnoreCase))
                throw new IOException("A dependency uses the reserved staging manifest filename.");
            entriesByPath.Add(source, new Entry { sourcePath = source, filename = filename, bytes = new FileInfo(LongPathIO.ForIO(source)).Length });
        }

        private static void AssignStagedPaths(IEnumerable<Entry> sourceEntries)
        {
            var entries = sourceEntries.ToList();
            var originalNames = new HashSet<string>(entries.Select(entry => entry.filename), StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var collision in entries.GroupBy(entry => entry.filename, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                foreach (var entry in collision) directories.Add(Path.GetDirectoryName(entry.sourcePath));
            var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedPrefixes = new HashSet<string>(originalNames, StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                string hash;
                using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()))).Replace("-", "").ToLowerInvariant();
                var prefix = "_bundle-" + hash.Substring(0, 16);
                var unique = prefix;
                int suffix = 2;
                while (!usedPrefixes.Add(unique)) unique = prefix + "-" + suffix++;
                prefixes.Add(directory, unique);
            }
            foreach (var entry in entries)
            {
                string prefix;
                // Keep a colliding source directory's bundles and resources together.
                // AssetRipper discovers these subfolders recursively and resolves
                // cross-bundle references by serialized CAB identities, so original
                // basenames and same-directory resource paths remain intact.
                if (prefixes.TryGetValue(Path.GetDirectoryName(entry.sourcePath), out prefix))
                    entry.filename = prefix + "/" + entry.filename;
            }
        }

        private static void AssertSafeParents(string path)
        {
            var current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Staging does not accept symbolic links or junctions: " + current);
                current = Path.GetDirectoryName(current);
            }
        }

        private static bool IsInside(string parent, string child)
        {
            return string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
                child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
