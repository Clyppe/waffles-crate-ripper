using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using File = Waffles.CrateRipper.LongPathFile;
using Directory = Waffles.CrateRipper.LongPathDirectory;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Waffles.CrateRipper
{
    internal static class UnityBindings
    {
        internal static Dictionary<string, ScriptBinding> Scripts()
        {
            var result = new Dictionary<string, ScriptBinding>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var script in MonoImporter.GetAllRuntimeMonoScripts())
            {
                var type = script.GetClass();
                if (type == null || string.IsNullOrEmpty(type.FullName)) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out string guid, out long fileId)) continue;
                if (result.TryGetValue(type.FullName, out var old) && (old.Guid != guid || old.FileId != fileId))
                { ambiguous.Add(type.FullName); continue; }
                result[type.FullName] = new ScriptBinding { FullTypeName = type.FullName, Guid = guid, FileId = fileId };
            }
            foreach (var name in ambiguous) result.Remove(name);
            return result;
        }

        internal static Dictionary<string, ShaderBinding> Shaders()
        {
            var result = new Dictionary<string, ShaderBinding>(StringComparer.Ordinal);
            var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var guid in AssetDatabase.FindAssets("t:Shader").OrderBy(g => AssetDatabase.GUIDToAssetPath(g), StringComparer.Ordinal))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null || ShaderUtil.ShaderHasError(shader)) continue;
                var physical = PhysicalAssetPath(path);
                if (File.Exists(physical))
                {
                    var source = File.ReadAllText(physical);
                    if (source.IndexOf("DummyShaderTextExporter", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                }
                int priority = path.StartsWith("Packages/", StringComparison.Ordinal) ? 0 : 1;
                if (priorities.TryGetValue(shader.name, out var previous))
                {
                    if (priority > previous) continue;
                    if (priority == previous) { ambiguous.Add(shader.name); continue; }
                    ambiguous.Remove(shader.name);
                }
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(shader, out string actualGuid, out long fileId))
                {
                    result[shader.name] = new ShaderBinding { Name = shader.name, Guid = actualGuid, FileId = fileId };
                    priorities[shader.name] = priority;
                }
            }
            foreach (var name in ambiguous) result.Remove(name);
            MaterialShaderRecovery.AddCompatibleBindings(result);
            return result;
        }

        private static string PhysicalAssetPath(string path)
        {
            var source = Path.Combine(Directory.GetParent(Application.dataPath).FullName, path);
            if (path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                if (package != null)
                {
                    var prefix = "Packages/" + package.name + "/";
                    if (path.StartsWith(prefix, StringComparison.Ordinal)) source = Path.Combine(package.resolvedPath, path.Substring(prefix.Length));
                }
            }
            return source;
        }

        internal static object Member(object instance, string name)
        {
            if (instance == null) return null;
            var type = instance.GetType();
            return type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance)
                ?? type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
        }

        internal static CrateEntry Crate(Object obj)
        {
            if (obj == null || !IsMarrowCrate(obj.GetType())) return null;
            var mainAsset = Member(obj, "MainAsset");
            var guid = Member(mainAsset, "AssetGUID") as string;
            var title = Member(obj, "Title") as string ?? obj.name;
            var path = string.IsNullOrEmpty(guid) ? "" : AssetDatabase.GUIDToAssetPath(guid);
            var entry = new CrateEntry {
                Name = title, Barcode = Member(obj, "Barcode")?.ToString() ?? "", MainAssetGuid = guid ?? "",
                Kind = obj.GetType().Name, PalletName = Member(Member(obj, "Pallet"), "Title") as string ?? "Project",
                PalletBarcode = Member(Member(obj, "Pallet"), "Barcode")?.ToString() ?? "",
                LocalPrefabPath = path, OriginalAssetPath = path
            };
            AddPackedAssets(entry, obj);
            return entry;
        }

        private static void AddPackedAssets(CrateEntry entry, object crate)
        {
            if (Member(crate, "PackedAssets") is IEnumerable packedAssets)
                foreach (var packed in packedAssets)
                {
                    AddAsset(entry, Member(Member(packed, "marrowAsset"), "AssetGUID") as string);
                    if (Member(packed, "subAssets") is IEnumerable subAssets)
                        foreach (var sub in subAssets) AddAsset(entry, Member(Member(sub, "subAsset"), "AssetGUID") as string);
                }
            // Authored level crates do not necessarily have their PackedAssets
            // list populated until pack time. Include only runtime scene roots.
            foreach (var name in new[] { "PersistentScenes", "ChunkScenes" })
                if (Member(crate, name) is IEnumerable scenes)
                    foreach (var scene in scenes) AddAsset(entry, Member(scene, "AssetGUID") as string);
        }

        private static void AddAsset(CrateEntry entry, string guid)
        {
            if (string.IsNullOrEmpty(guid) || string.Equals(guid, entry.MainAssetGuid, StringComparison.OrdinalIgnoreCase)
                || entry.AdditionalAssets.Any(a => string.Equals(a.Guid, guid, StringComparison.OrdinalIgnoreCase))) return;
            var path = AssetDatabase.GUIDToAssetPath(guid);
            entry.AdditionalAssets.Add(new CrateAssetEntry { Guid = guid, LocalAssetPath = path, OriginalAssetPath = path });
        }

        private static string MainExtension(CrateEntry crate) { return SourceCatalog.IsMap(crate) ? ".unity" : ".prefab"; }

        private static bool IsMarrowCrate(Type type)
        {
            for (; type != null; type = type.BaseType)
                if (type.FullName == "SLZ.Marrow.Warehouse.Crate" || type.FullName == "SLZ.Warehouse.Crate") return true;
            return false;
        }

        private static Type WarehouseType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("SLZ.Marrow.Warehouse.AssetWarehouse", false)
                    ?? assembly.GetType("SLZ.Warehouse.AssetWarehouse", false);
                if (type != null) return type;
            }
            return null;
        }

        private static object WarehouseInstance(Type type)
        {
            if (type == null) return null;
            return type.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }

        private static object Query(object target, string method)
        {
            if (target == null) return null;
            var query = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == method && !m.IsGenericMethod && m.GetParameters().Length == 0);
            return query?.Invoke(target, null);
        }

        internal static string WarehouseState()
        {
            var type = WarehouseType();
            var instance = WarehouseInstance(type);
            if (instance == null) return type == null ? "SDK unavailable" : "Warehouse loading";
            var ready = type.GetField("ready", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            try { return "Warehouse:" + ready + ":" + Query(instance, "CrateCount"); }
            catch (Exception) { return "Warehouse loading"; }
        }

        internal static List<CrateEntry> AvailableCrates(Action<string> status = null)
        {
            var entries = new Dictionary<string, CrateEntry>(StringComparer.Ordinal);
            var type = WarehouseType();
            var warehouse = WarehouseInstance(type);
            var manifests = new Dictionary<string, object>(StringComparer.Ordinal);
            if (warehouse != null)
            {
                try
                {
                    if (Query(warehouse, "GetPalletManifests") is IEnumerable palletManifests)
                        foreach (var manifest in palletManifests)
                        {
                            var barcode = Member(manifest, "PalletBarcode")?.ToString()
                                ?? Member(Member(manifest, "Pallet"), "Barcode")?.ToString();
                            if (!string.IsNullOrEmpty(barcode)) manifests[barcode] = manifest;
                        }
                    if (Query(warehouse, "GetCrates") is IEnumerable registeredCrates)
                        foreach (var obj in registeredCrates)
                        {
                            var crate = Crate(obj as Object);
                            if (crate == null) continue;
                            var palletBarcode = Member(Member(obj, "Pallet"), "Barcode")?.ToString();
                            if (palletBarcode != null && manifests.TryGetValue(palletBarcode, out var manifest))
                            {
                                crate.SourceFolder = ManifestFolder(manifest);
                                crate.SourceRoot = crate.SourceFolder;
                                var original = CatalogAsset(Member(manifest, "Catalog"), crate.MainAssetGuid, MainExtension(crate));
                                if (!string.IsNullOrEmpty(original)) crate.OriginalAssetPath = original;
                                foreach (var asset in crate.AdditionalAssets)
                                    asset.OriginalAssetPath = CatalogAsset(Member(manifest, "Catalog"), asset.Guid) ?? asset.OriginalAssetPath;
                            }
                            MergeCrate(entries, crate);
                        }
                }
                catch (Exception ex) { status?.Invoke("Could not finish reading the Marrow warehouse: " + ex.GetBaseException().Message); }
            }
            // Authoring crates may exist before the asynchronous warehouse has loaded.
            foreach (var guid in new[] { "t:Crate", "t:SpawnableCrate", "t:AvatarCrate", "t:LevelCrate" }
                .SelectMany(filter => AssetDatabase.FindAssets(filter)).Distinct())
            {
                var crate = Crate(AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid)));
                if (crate != null) MergeCrate(entries, crate);
            }
            var recovered = entries.Values.Where(c => !string.IsNullOrEmpty(c.SourceFolder) && string.IsNullOrEmpty(c.OriginalAssetPath)).ToList();
            ResolveCatalogPaths(recovered, message => status?.Invoke(message));
            return entries.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.PalletName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Barcode, StringComparer.Ordinal).ToList();
        }

        private static void MergeCrate(Dictionary<string, CrateEntry> entries, CrateEntry entry)
        {
            var key = !string.IsNullOrEmpty(entry.Barcode) ? "barcode:" + entry.Barcode
                : entry.Kind + "|" + entry.MainAssetGuid + "|" + entry.PalletName + "|" + entry.Name;
            if (!entries.TryGetValue(key, out var previous)) { entries[key] = entry; return; }
            var preferred = !string.IsNullOrEmpty(entry.LocalPrefabPath) && string.IsNullOrEmpty(previous.LocalPrefabPath) ? entry : previous;
            var other = preferred == entry ? previous : entry;
            if (string.IsNullOrEmpty(preferred.SourceFolder)) preferred.SourceFolder = other.SourceFolder;
            if (string.IsNullOrEmpty(preferred.SourceRoot)) preferred.SourceRoot = other.SourceRoot;
            if (string.IsNullOrEmpty(preferred.PalletBarcode)) preferred.PalletBarcode = other.PalletBarcode;
            if (string.IsNullOrEmpty(preferred.OriginalAssetPath)) preferred.OriginalAssetPath = other.OriginalAssetPath;
            foreach (var extra in other.AdditionalAssets ?? new List<CrateAssetEntry>())
            {
                var found = preferred.AdditionalAssets.FirstOrDefault(a => string.Equals(a.Guid, extra.Guid, StringComparison.OrdinalIgnoreCase));
                if (found == null) preferred.AdditionalAssets.Add(extra);
                else
                {
                    if (string.IsNullOrEmpty(found.LocalAssetPath)) found.LocalAssetPath = extra.LocalAssetPath;
                    if (string.IsNullOrEmpty(found.OriginalAssetPath)) found.OriginalAssetPath = extra.OriginalAssetPath;
                }
            }
            entries[key] = preferred;
        }

        private static string ManifestFolder(object manifest)
        {
            foreach (var name in new[] { "PalletPath", "CatalogPath" })
            {
                var path = Member(manifest, name) as string;
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    if (Directory.Exists(path)) return Path.GetFullPath(path);
                    var folder = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (Directory.Exists(folder)) return folder;
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            return null;
        }

        private static string CatalogAsset(object locator, string assetGuid, string extension = null)
        {
            if (locator == null || string.IsNullOrEmpty(assetGuid)) return null;
            try
            {
                var locate = locator.GetType().GetMethods().FirstOrDefault(m => m.Name == "Locate" && m.GetParameters().Length == 3);
                if (locate == null) return null;
                var args = new object[] { assetGuid, null, null };
                if (!(bool)locate.Invoke(locator, args) || !(args[2] is IEnumerable locations)) return null;
                foreach (var location in locations)
                {
                    var id = Member(location, "InternalId") as string;
                    if (!string.IsNullOrEmpty(id) && (string.IsNullOrEmpty(extension) || id.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) return id.Replace('\\', '/');
                }
            }
            catch (Exception) { }
            return null;
        }

        private sealed class LocationIdentity : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }

        // Resolve from this crate's root locations only. Never iterate locator.Keys
        // or substitute the entire game's/pallet's directory as extractor input.
        internal static List<string> BundleFiles(CrateEntry crate)
        {
            if (crate == null || string.IsNullOrEmpty(crate.MainAssetGuid))
                throw new InvalidOperationException("The selected crate has no main asset GUID.");
            var sourceFolders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var warehouseLocators = new List<object>();
            var locators = new List<object>();
            var warehouse = WarehouseInstance(WarehouseType());
            if (Query(warehouse, "GetPalletManifests") is IEnumerable manifests)
                foreach (var manifest in manifests)
                {
                    var barcode = Member(manifest, "PalletBarcode")?.ToString()
                        ?? Member(Member(manifest, "Pallet"), "Barcode")?.ToString();
                    var folder = ManifestFolder(manifest);
                    if (!string.IsNullOrEmpty(barcode) && !string.IsNullOrEmpty(folder)) sourceFolders[barcode] = new List<string> { folder };
                    var locator = Member(manifest, "Catalog");
                    if (locator != null && ((!string.IsNullOrEmpty(crate.PalletBarcode) && barcode == crate.PalletBarcode)
                        || (string.IsNullOrEmpty(crate.PalletBarcode) && string.Equals(folder, crate.SourceFolder, StringComparison.OrdinalIgnoreCase))))
                        warehouseLocators.Add(locator);
                }

            // Unregistered ZIP pallets have no warehouse path resolver. Explicit
            // barcode mappings also resolve dependencies between pallets in a ZIP.
            var root = !string.IsNullOrEmpty(crate.SourceRoot) ? crate.SourceRoot : crate.SourceFolder;
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                foreach (var entry in SourceCatalog.ReadPalletFolders(root)) sourceFolders[entry.Key] = entry.Value;
            if (!string.IsNullOrEmpty(crate.PalletBarcode) && !string.IsNullOrEmpty(crate.SourceFolder))
                sourceFolders[crate.PalletBarcode] = new List<string> { crate.SourceFolder };

            var catalogType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEngine.AddressableAssets.ResourceLocators.ContentCatalogData", false)).FirstOrDefault(t => t != null);
            var catalogErrors = new List<string>();
            var catalogPaths = !string.IsNullOrEmpty(crate.SourceFolder) && Directory.Exists(crate.SourceFolder)
                ? Directory.GetFiles(crate.SourceFolder, "catalog*.json", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal).ToArray()
                : new string[0];
            if (catalogType != null)
                foreach (var path in catalogPaths)
                {
                    try
                    {
                        var data = JsonUtility.FromJson(File.ReadAllText(path), catalogType);
                        var locator = catalogType.GetMethod("CreateLocator", new[] { typeof(string) })?.Invoke(data, new object[] { null });
                        if (locator != null) locators.Add(locator);
                    }
                    catch (Exception ex) { catalogErrors.Add(Path.GetFileName(path) + ": " + ex.GetBaseException().Message); }
                }
            // Once this source supplies catalogs, a missing or invalid root must
            // not silently fall back to an older installed copy of the pallet.
            if (catalogPaths.Length == 0) locators.AddRange(warehouseLocators);

            List<object> roots = null;
            foreach (var locator in locators)
            {
                roots = Locate(locator, crate.MainAssetGuid);
                if (roots.Count > 0) break;
            }
            if (roots == null || roots.Count == 0)
                throw new InvalidOperationException("Cannot resolve the selected crate's bundle dependencies from its catalog. " +
                    "Load its complete mod ZIP or pallet folder and ensure Addressables is installed. " + string.Join("; ", catalogErrors));
            var prefab = roots.Select(location => Member(location, "InternalId") as string)
                .FirstOrDefault(id => !string.IsNullOrEmpty(id) && id.EndsWith(MainExtension(crate), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(prefab)) crate.OriginalAssetPath = prefab.Replace('\\', '/');
            foreach (var asset in crate.AdditionalAssets ?? new List<CrateAssetEntry>())
            {
                List<object> extraRoots = null;
                foreach (var locator in locators)
                {
                    extraRoots = Locate(locator, asset.Guid);
                    if (extraRoots.Count > 0) break;
                }
                if (extraRoots == null || extraRoots.Count == 0)
                    throw new InvalidOperationException("Cannot resolve packed asset " + asset.Guid + " belonging to the selected crate. Supply its complete pallet/catalog.");
                asset.OriginalAssetPath = extraRoots.Select(location => Member(location, "InternalId") as string).FirstOrDefault(id => !string.IsNullOrEmpty(id));
                roots.AddRange(extraRoots);
            }

            var pending = new Queue<object>(roots);
            var visited = new HashSet<object>(new LocationIdentity());
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            while (pending.Count > 0)
            {
                var location = pending.Dequeue();
                if (location == null || !visited.Add(location)) continue;
                if (Member(location, "Dependencies") is IEnumerable dependencies)
                    foreach (var dependency in dependencies) if (dependency != null) pending.Enqueue(dependency);
                var id = Member(location, "InternalId") as string;
                var provider = Member(location, "ProviderId") as string ?? "";
                var resourceType = Member(location, "ResourceType") as Type;
                bool isBundle = provider.IndexOf("AssetBundleProvider", StringComparison.OrdinalIgnoreCase) >= 0
                    || resourceType?.Name == "IAssetBundleResource" || resourceType == typeof(AssetBundle)
                    || (!string.IsNullOrEmpty(id) && id.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase));
                if (!isBundle) continue;
                var file = BundleFile(location, id, crate.SourceFolder, sourceFolders);
                if (file == null) missing.Add(id ?? "Unnamed bundle dependency");
                else files.Add(file);
            }
            if (missing.Count > 0)
                throw new FileNotFoundException("Selected crate dependency bundles are missing. Supply those pallet files before extraction:\n" + string.Join("\n", missing.Distinct()));
            if (files.Count == 0)
                throw new InvalidOperationException("The catalog contains no asset bundles for the selected crate. Extraction was stopped.");
            return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<object> Locate(object locator, string guid)
        {
            try
            {
                var locate = locator.GetType().GetMethods().FirstOrDefault(m => m.Name == "Locate" && m.GetParameters().Length == 3);
                if (locate == null) return new List<object>();
                var args = new object[] { guid, null, null };
                if (!(bool)locate.Invoke(locator, args) || !(args[2] is IEnumerable locations)) return new List<object>();
                return locations.Cast<object>().ToList();
            }
            catch (Exception) { return new List<object>(); }
        }

        private static string BundleFile(object location, string id, string selectedFolder, Dictionary<string, List<string>> palletFolders)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith("PALLET_BARCODE:", StringComparison.Ordinal))
            {
                int split = id.IndexOf(':', "PALLET_BARCODE:".Length);
                if (split > 0)
                {
                    var barcode = id.Substring("PALLET_BARCODE:".Length, split - "PALLET_BARCODE:".Length);
                    if (palletFolders.TryGetValue(barcode, out var folders))
                    {
                        var relative = id.Substring(split + 1).TrimStart('/', '\\').Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var folder in folders)
                        {
                            var root = Path.GetFullPath(folder);
                            var path = Path.GetFullPath(Path.Combine(root, relative));
                            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("A bundle dependency leaves its pallet folder: " + id);
                            var mapped = ExistingBundle(path);
                            if (mapped != null) matches.Add(mapped);
                        }
                        if (matches.Count > 1)
                            throw new InvalidDataException("Multiple copies of a required bundle were found. Load the folder for one platform/build: " + id + "\n" + string.Join("\n", matches));
                        // Known source mappings are authoritative. A missing ZIP
                        // file must not be replaced by an older installed bundle.
                        return matches.SingleOrDefault();
                    }
                }
                // The dependency belongs to its own pallet. Never guess that it
                // resides in the selected crate's directory when the mapping is absent.
            }
            else
            {
                if (Path.IsPathRooted(id) || Uri.TryCreate(id, UriKind.Absolute, out _))
                {
                    var direct = ExistingBundle(id);
                    if (direct != null) return direct;
                }
                else if (!string.IsNullOrEmpty(selectedFolder) && id.IndexOf('{') < 0 && id.IndexOf('[') < 0)
                {
                    var root = Path.GetFullPath(selectedFolder);
                    var path = Path.GetFullPath(Path.Combine(root, id.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A bundle dependency leaves its selected source folder: " + id);
                    return ExistingBundle(path);
                }
            }
            // Marrow's transformer handles cross-pallet game paths and other
            // Addressables runtime substitutions using the dependency's manifest.
            try
            {
                var addressables = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.AddressableAssets.Addressables", false)).FirstOrDefault(t => t != null);
                var manager = addressables?.GetProperty("ResourceManager", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var transform = manager?.GetType().GetMethods().FirstOrDefault(m => m.Name == "TransformInternalId" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(location));
                var transformed = transform?.Invoke(manager, new[] { location }) as string;
                return ExistingBundle(transformed);
            }
            catch (Exception) { return null; }
        }

        private static string ExistingBundle(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
                {
                    if (!uri.IsFile) return null;
                    path = uri.LocalPath;
                }
                return File.Exists(path) ? Path.GetFullPath(path) : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        internal static void ResolveCatalogPaths(IEnumerable<CrateEntry> crates, Action<string> status)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.AddressableAssets.ResourceLocators.ContentCatalogData", false)).FirstOrDefault(t => t != null);
            if (type == null) { status("Addressables is not installed; exported scene/prefab names will be used for matching."); return; }
            foreach (var group in crates.Where(c => !string.IsNullOrEmpty(c.SourceFolder) && Directory.Exists(c.SourceFolder)).GroupBy(c => c.SourceFolder))
            foreach (var path in Directory.EnumerateFiles(group.Key, "catalog*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var data = JsonUtility.FromJson(File.ReadAllText(path), type);
                    var create = type.GetMethod("CreateLocator", new[] { typeof(string) });
                    var locator = create?.Invoke(data, new object[] { null });
                    if (locator == null) continue;
                    foreach (var crate in group)
                    {
                        crate.OriginalAssetPath = CatalogAsset(locator, crate.MainAssetGuid, MainExtension(crate)) ?? crate.OriginalAssetPath;
                        foreach (var asset in crate.AdditionalAssets ?? new List<CrateAssetEntry>())
                            asset.OriginalAssetPath = CatalogAsset(locator, asset.Guid) ?? asset.OriginalAssetPath;
                    }
                }
                catch (Exception ex) { status("Catalog mapping unavailable for " + Path.GetFileName(path) + ": " + ex.GetBaseException().Message); }
            }
        }

        internal static string StageProjectCrate(CrateEntry crate, string jobFolder)
        {
            if (string.IsNullOrEmpty(crate.LocalPrefabPath))
                throw new InvalidOperationException("This crate's main scene/prefab is not in the project. Load its packed mod folder or ZIP instead.");
            var stage = Path.Combine(jobFolder, "ProjectSource");
            Directory.CreateDirectory(stage);
            var roots = new[] { crate.LocalPrefabPath }.Concat((crate.AdditionalAssets ?? new List<CrateAssetEntry>())
                .Select(a => a.LocalAssetPath).Where(p => !string.IsNullOrEmpty(p))).Distinct().ToArray();
            foreach (var path in AssetDatabase.GetDependencies(roots, true))
            {
                if (!(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))) continue;
                var source = PhysicalAssetPath(path);
                if (!File.Exists(source)) continue;
                var output = Path.Combine(stage, path);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.Copy(source, output, false);
                if (File.Exists(source + ".meta")) File.Copy(source + ".meta", output + ".meta", false);
            }
            return stage;
        }

        // Kept for existing callers; this now resolves the crate's main asset,
        // including LevelCrate scenes, rather than assuming every root is a prefab.
        internal static string MatchPrefab(string exportedAssets, CrateEntry crate)
        { return SourceCatalog.MatchMainAsset(exportedAssets, crate); }

        internal static List<string> MatchAdditionalAssets(string exportedAssets, CrateEntry crate, out List<string> warnings)
        {
            warnings = new List<string>();
            var result = new List<string>();
            foreach (var asset in crate.AdditionalAssets ?? new List<CrateAssetEntry>())
            {
                var path = SourceCatalog.MatchExportedAsset(exportedAssets, asset.Guid, asset.OriginalAssetPath,
                    fallbackName: string.IsNullOrEmpty(asset.OriginalAssetPath) ? null : Path.GetFileNameWithoutExtension(asset.OriginalAssetPath));
                if (!string.IsNullOrEmpty(path)) result.Add(path);
                else
                {
                    var message = "Packed crate asset could not be identified in the export: " + asset.Guid + " " + asset.OriginalAssetPath;
                    // A missing chunk would produce an incomplete map. Do not
                    // silently replace it with a similarly named prop or other level.
                    if ((asset.OriginalAssetPath ?? "").EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(message);
                    warnings.Add(message);
                }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static string Relative(string root, string path)
        { return path.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/'); }
    }
}
