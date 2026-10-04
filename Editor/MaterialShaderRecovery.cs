using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Waffles.CrateRipper
{
    internal static class MaterialShaderRecovery
    {
        private const string LitMas = "SLZ/LitMAS/LitMAS Standard";
        private static readonly string[] CompatibleNames = {
            "SLZ/LitMAS/LitMAS MonoSH", "Shader Graphs/BakeryURPMonoSHSpecGraphMAS"
        };

        internal static void AddCompatibleBindings(Dictionary<string, ShaderBinding> bindings)
        {
            if (!bindings.TryGetValue(LitMas, out var standard)) return;
            foreach (var name in CompatibleNames)
                if (!bindings.ContainsKey(name)) bindings[name] = new ShaderBinding {
                    Name = name, ReplacementName = LitMas, Guid = standard.Guid, FileId = standard.FileId
                };
        }

        [MenuItem("Tools/Waffles/Repair Recovered Shaders")]
        private static void RepairDefaultFolder()
        {
            var repaired = RepairFolder("Assets/Ripped Crates");
            Debug.Log("Waffles Crate Ripper: reconnected " + repaired + " recovered material shaders. See Assets/Ripped Crates/shader-repair-report.txt.");
        }

        // Shared property names retain the exported texture references, UVs,
        // colors and scalar values when Unity changes the shader reference.
        internal static int RepairFolder(string assetFolder)
        {
            assetFolder = (assetFolder ?? "").Replace('\\', '/').TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(assetFolder) || !assetFolder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("Choose an existing recovered asset folder inside Assets.");
            var bindings = UnityBindings.Shaders();
            var notes = new List<string>();
            int repaired = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { assetFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null || material.shader == null || !bindings.TryGetValue(material.shader.name, out var binding)) continue;
                var target = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(binding.Guid));
                if (target == null || target == material.shader) continue;
                var original = material.shader.name;
                Undo.RecordObject(material, "Reconnect recovered material shader");
                material.shader = target;
                EditorUtility.SetDirty(material);
                repaired++;
                notes.Add(path + ": " + original + " -> " + target.name);
                if (!string.IsNullOrEmpty(binding.ReplacementName))
                    notes.Add("Compatible LitMAS replacement preserves shared material properties. Original baked MonoSH/Bakery lighting is not reconstructed.");
            }
            AssetDatabase.SaveAssets();
            notes.Insert(0, "Reconnected " + repaired + " material shaders.");
            File.WriteAllLines(Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetFolder, "shader-repair-report.txt"), notes);
            AssetDatabase.Refresh();
            return repaired;
        }
    }
}
