using System;
using System.Collections.Generic;
using System.IO;
using File = Waffles.CrateRipper.LongPathFile;
using Directory = Waffles.CrateRipper.LongPathDirectory;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Waffles.CrateRipper
{
    [Serializable]
    public sealed class CrateAssetEntry
    {
        public string Guid;
        public string OriginalAssetPath;
        public string LocalAssetPath;
    }

    public enum CrateKindFilter { All, Spawnables, Maps, Avatars }

    [Serializable]
    public sealed class CrateEntry
    {
        public string Name;
        public string Barcode;
        public string Kind;
        public string MainAssetGuid;
        public string PalletName;
        public string PalletBarcode;
        public string SourceFolder;
        public string SourceRoot;
        public string OriginalAssetPath;
        public string LocalPrefabPath;
        public List<CrateAssetEntry> AdditionalAssets = new List<CrateAssetEntry>();
        public override string ToString() { return Name + " (" + PalletName + ")"; }
    }

    public static class SourceCatalog
    {
        public static Dictionary<string, List<string>> ReadPalletFolders(string folder)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(folder, "*.pallet.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var document = JObject.Parse(File.ReadAllText(path));
                var rootRef = (string)document["root"]?["ref"];
                var pallet = rootRef == null ? null : document["objects"]?[rootRef];
                var barcode = (string)pallet?["barcode"];
                if (string.IsNullOrEmpty(barcode)) continue;
                if (!result.TryGetValue(barcode, out var folders)) result[barcode] = folders = new List<string>();
                var full = Path.GetFullPath(Path.GetDirectoryName(path));
                if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase)) folders.Add(full);
            }
            return result;
        }

        public static List<CrateEntry> ReadPallets(string folder)
        {
            var result = new List<CrateEntry>();
            foreach (var path in Directory.EnumerateFiles(folder, "*.pallet.json", SearchOption.AllDirectories))
            {
                var document = JObject.Parse(File.ReadAllText(path));
                var objects = document["objects"] as JObject;
                var rootRef = (string)document["root"]?["ref"];
                if (objects == null || rootRef == null || objects[rootRef] == null)
                    throw new InvalidDataException("Unsupported pallet format: " + path);
                var pallet = objects[rootRef];
                foreach (var reference in pallet["crates"] as JArray ?? new JArray())
                {
                    var crate = objects[(string)reference["ref"] ?? ""];
                    if (crate == null) continue;
                    var kind = (string)reference["type"] ?? (string)crate["isa"]?["type"] ?? "unknown";
                    result.Add(new CrateEntry {
                        Name = (string)crate["title"] ?? (string)crate["barcode"] ?? "Unnamed crate",
                        Barcode = (string)crate["barcode"] ?? "",
                        Kind = kind, MainAssetGuid = (string)crate["mainAsset"] ?? "",
                        PalletName = (string)pallet["title"] ?? Path.GetFileName(Path.GetDirectoryName(path)),
                        PalletBarcode = (string)pallet["barcode"] ?? "",
                        SourceFolder = Path.GetDirectoryName(path), SourceRoot = Path.GetFullPath(folder),
                        AdditionalAssets = ReadPackedAssets(crate)
                    });
                }
            }
            return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<CrateAssetEntry> ReadPackedAssets(JToken crate)
        {
            var guids = new List<string>();
            foreach (var packed in crate["packedAssets"] as JArray ?? new JArray())
            {
                var guid = (string)packed["guid"];
                if (!string.IsNullOrEmpty(guid)) guids.Add(guid);
                foreach (var subAsset in packed["subAssets"] as JArray ?? new JArray())
                {
                    var subGuid = subAsset.Type == JTokenType.String ? (string)subAsset : (string)subAsset["guid"];
                    if (!string.IsNullOrEmpty(subGuid)) guids.Add(subGuid);
                }
            }
            var mainGuid = (string)crate["mainAsset"];
            return guids.Where(g => Regex.IsMatch(g, "^[0-9a-fA-F]{32}$") && !string.Equals(g, mainGuid, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(g => new CrateAssetEntry { Guid = g }).ToList();
        }

        public static bool IsMap(CrateEntry crate)
        { return (crate?.Kind ?? "").IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0; }

        public static bool MatchesKind(CrateEntry crate, CrateKindFilter filter)
        {
            var kind = crate?.Kind ?? "";
            switch (filter)
            {
                case CrateKindFilter.Maps: return IsMap(crate);
                case CrateKindFilter.Avatars: return kind.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0;
                case CrateKindFilter.Spawnables: return kind.IndexOf("spawnable", StringComparison.OrdinalIgnoreCase) >= 0;
                default: return true;
            }
        }

        // Catalog roots retain their asset type. A level's similarly named prefab
        // is never substituted for its actual scene, nor is a prop substituted for an avatar.
        public static string MatchMainAsset(string exportedAssets, CrateEntry crate)
        {
            if (crate == null) throw new ArgumentNullException(nameof(crate));
            return MatchExportedAsset(exportedAssets, crate.MainAssetGuid, crate.OriginalAssetPath,
                IsMap(crate) ? ".unity" : ".prefab", crate.Name);
        }

        public static string MatchExportedAsset(string exportedAssets, string guid, string originalAssetPath,
            string extension = null, string fallbackName = null)
        {
            exportedAssets = Path.GetFullPath(exportedAssets);
            var candidates = Directory.GetFiles(exportedAssets, "*", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(extension) || Path.GetExtension(p).Equals(extension, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (!string.IsNullOrEmpty(guid))
            {
                var exactGuid = candidates.Where(p => File.Exists(p + ".meta") &&
                    Regex.IsMatch(File.ReadAllText(p + ".meta"), @"(?m)^guid:\s*" + Regex.Escape(guid) + @"\s*$", RegexOptions.IgnoreCase)).ToArray();
                if (exactGuid.Length == 1) return ExportRelative(exportedAssets, exactGuid[0]);
            }
            if (!string.IsNullOrEmpty(originalAssetPath))
            {
                var original = originalAssetPath.Replace('\\', '/');
                if (original.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) original = original.Substring(7);
                var exact = candidates.Where(p => ExportRelative(exportedAssets, p).Equals(original, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length == 1) return ExportRelative(exportedAssets, exact[0]);
                var named = candidates.Where(p => Path.GetFileName(p).Equals(Path.GetFileName(original), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (named.Length == 1) return ExportRelative(exportedAssets, named[0]);
            }
            if (!string.IsNullOrEmpty(fallbackName))
            {
                var named = candidates.Where(p => Path.GetFileNameWithoutExtension(p).Equals(fallbackName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (named.Length == 1) return ExportRelative(exportedAssets, named[0]);
            }
            return null;
        }

        private static string ExportRelative(string root, string path)
        { return path.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1).Replace('\\', '/'); }

        public static string FolderName(string name)
        {
            var cleaned = Regex.Replace(name ?? "", "[\\\\/:*?\"<>|\\x00-\\x1f]", "_").Trim().TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Unnamed crate";
            if (Regex.IsMatch(cleaned, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\.|$)", RegexOptions.IgnoreCase)) cleaned = "_" + cleaned;
            return cleaned.Length > 100 ? cleaned.Substring(0, 100).TrimEnd('.', ' ') : cleaned;
        }

        // ZIP content is data, never an executable or a project package manifest.
        public static void ExtractZip(string zipPath, string emptyDestination, CancellationToken cancellation,
            IProgress<string> progress = null, long maximumBytes = 40L * 1024 * 1024 * 1024, int maximumEntries = 100000)
        {
            var root = Path.GetFullPath(emptyDestination);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException("ZIP staging folder must be empty.");
            using (var archive = ZipFile.OpenRead(LongPathIO.ForIO(zipPath)))
            {
                if (archive.Entries.Count > maximumEntries) throw new InvalidDataException("ZIP has too many entries.");
                long total = 0;
                var planned = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrEmpty(name)) continue;
                    var directory = name.EndsWith("/", StringComparison.Ordinal);
                    if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains(":") || name.Contains("\0") ||
                        name.Split('/').Any(p => p == ".." || p == "."))
                        throw new InvalidDataException("Unsafe ZIP path: " + entry.FullName);
                    var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                    if (unixType == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
                        throw new InvalidDataException("ZIP links are not supported: " + entry.FullName);
                    var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("ZIP entry leaves the staging folder.");
                    if (planned.ContainsKey(path)) throw new InvalidDataException("Duplicate ZIP path: " + entry.FullName);
                    planned.Add(path, directory);
                    try { total = checked(total + entry.Length); }
                    catch (OverflowException) { throw new InvalidDataException("ZIP size exceeds the extraction limit."); }
                    if (total > maximumBytes) throw new InvalidDataException("ZIP exceeds the 40 GB extraction limit.");
                }
                foreach (var item in planned)
                {
                    var parent = Path.GetDirectoryName(item.Key);
                    while (parent != null && parent.Length > root.Length)
                    {
                        if (planned.TryGetValue(parent, out var directory) && !directory)
                            throw new InvalidDataException("ZIP file is also used as a folder: " + parent);
                        parent = Path.GetDirectoryName(parent);
                    }
                }
                Directory.CreateDirectory(root);
                var buffer = new byte[128 * 1024];
                foreach (var entry in archive.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.FullName)) continue;
                    var name = entry.FullName.Replace('\\', '/');
                    var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (name.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    progress?.Report("Unpacking " + name);
                    using (var input = entry.Open())
                    using (var output = new FileStream(LongPathIO.ForIO(path), FileMode.CreateNew, FileAccess.Write))
                    {
                        long written = 0;
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            written = checked(written + read);
                            if (written > entry.Length) throw new InvalidDataException("ZIP entry length mismatch: " + name);
                            output.Write(buffer, 0, read);
                        }
                        if (written != entry.Length) throw new InvalidDataException("ZIP entry is truncated: " + name);
                    }
                }
            }
        }
    }
}
