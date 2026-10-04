using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
            if (obj == null || !obj.GetType().Name.EndsWith("Crate", StringComparison.Ordinal)) return null;
            var mainAsset = Member(obj, "MainAsset");
            var guid = Member(mainAsset, "AssetGUID") as string;
            var title = Member(obj, "Title") as string ?? obj.name;
            var path = string.IsNullOrEmpty(guid) ? "" : AssetDatabase.GUIDToAssetPath(guid);
            return new CrateEntry {
                Name = title, Barcode = Member(obj, "Barcode")?.ToString() ?? "", MainAssetGuid = guid ?? "",
                Kind = "project-" + obj.GetType().Name, PalletName = "Project",
                LocalPrefabPath = path, OriginalAssetPath = path
            };
        }

        internal static List<CrateEntry> ProjectCrates()
        {
            return AssetDatabase.FindAssets("t:SpawnableCrate").Select(guid => Crate(AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid))))
                .Where(c => c != null).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static void ResolveCatalogPaths(IEnumerable<CrateEntry> crates, Action<string> status)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.AddressableAssets.ResourceLocators.ContentCatalogData", false)).FirstOrDefault(t => t != null);
            if (type == null) { status("Addressables is not installed; exported prefab names will be used for matching."); return; }
            foreach (var group in crates.GroupBy(c => c.SourceFolder))
            foreach (var path in Directory.EnumerateFiles(group.Key, "catalog*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var data = JsonUtility.FromJson(File.ReadAllText(path), type);
                    var create = type.GetMethod("CreateLocator", new[] { typeof(string) });
                    var locator = create?.Invoke(data, new object[] { null });
                    if (locator == null) continue;
                    var locate = locator.GetType().GetMethods().First(m => m.Name == "Locate" && m.GetParameters().Length == 3);
                    foreach (var crate in group)
                    {
                        var args = new object[] { crate.MainAssetGuid, null, null };
                        if (!(bool)locate.Invoke(locator, args)) continue;
                        foreach (var location in (IEnumerable)args[2])
                        {
                            var internalId = Member(location, "InternalId") as string;
                            if (!string.IsNullOrEmpty(internalId) && internalId.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                            { crate.OriginalAssetPath = internalId.Replace('\\', '/'); break; }
                        }
                    }
                }
                catch (Exception ex) { status("Catalog mapping unavailable for " + Path.GetFileName(path) + ": " + ex.GetBaseException().Message); }
            }
        }

        internal static string StageProjectCrate(CrateEntry crate, string jobFolder)
        {
            if (string.IsNullOrEmpty(crate.LocalPrefabPath))
                throw new InvalidOperationException("This crate's prefab is not in the project. Load its packed mod folder or ZIP instead.");
            var stage = Path.Combine(jobFolder, "ProjectSource");
            Directory.CreateDirectory(stage);
            foreach (var path in AssetDatabase.GetDependencies(crate.LocalPrefabPath, true))
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

        internal static string MatchPrefab(string exportedAssets, CrateEntry crate)
        {
            var prefabs = Directory.GetFiles(exportedAssets, "*.prefab", SearchOption.AllDirectories);
            if (!string.IsNullOrEmpty(crate.OriginalAssetPath))
            {
                var original = crate.OriginalAssetPath.Replace('\\', '/');
                if (original.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) original = original.Substring(7);
                var exact = prefabs.Where(p => Relative(exportedAssets, p).Equals(original, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length == 1) return Relative(exportedAssets, exact[0]);
                var fileName = Path.GetFileNameWithoutExtension(original);
                var named = prefabs.Where(p => Path.GetFileNameWithoutExtension(p).Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (named.Length == 1) return Relative(exportedAssets, named[0]);
            }
            var matches = prefabs.Where(p => Path.GetFileNameWithoutExtension(p).Equals(crate.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? Relative(exportedAssets, matches[0]) : null;
        }

        internal static string Relative(string root, string path)
        { return path.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/'); }
    }
}
