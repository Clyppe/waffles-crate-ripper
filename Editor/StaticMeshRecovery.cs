using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using File = Waffles.CrateRipper.LongPathFile;
using Directory = Waffles.CrateRipper.LongPathDirectory;
using Object = UnityEngine.Object;

namespace Waffles.CrateRipper
{
    public sealed class StaticMeshRepairReport
    {
        public int repairedRenderers;
        public List<string> warnings = new List<string>();
        public List<string> repairedAssets = new List<string>();
    }

    // Build-time static batches contain shared geometry in batch-root space.
    // Clearing the static flag alone leaves that geometry at its baked position.
    internal static class StaticMeshRecovery
    {
        public static StaticMeshRepairReport RepairFolder(string outputFolder, CancellationToken cancellation = default(CancellationToken))
        {
            var normalized = (outputFolder ?? "").Replace('\\', '/').TrimEnd('/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) || normalized.Split('/').Any(p => p == ".." || p == "." || p.Length == 0))
                throw new ArgumentException("Select a recovered output folder inside Assets.");
            if (!Directory.Exists(normalized)) throw new DirectoryNotFoundException(normalized);
            var report = new StaticMeshRepairReport();
            var paths = Directory.EnumerateFiles(normalized, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar("Recover editable map meshes", paths[i], (float)i / Math.Max(1, paths.Length)))
                        throw new OperationCanceledException();
                    try
                    {
                        if (paths[i].EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) RepairScene(paths[i], normalized, report, cancellation);
                        else RepairPrefab(paths[i], normalized, report, cancellation);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { report.warnings.Add(paths[i] + ": " + ex.GetBaseException().Message); }
                }
                AssetDatabase.SaveAssets();
            }
            finally
            {
                if (!Application.isBatchMode) EditorUtility.ClearProgressBar();
                File.WriteAllText(normalized + "/static-mesh-repair-report.json", JsonConvert.SerializeObject(report, Formatting.Indented) + "\n");
                AssetDatabase.Refresh();
            }
            return report;
        }

        private static void RepairScene(string path, string output, StaticMeshRepairReport report, CancellationToken cancellation)
        {
            var scene = SceneManager.GetSceneByPath(path);
            bool opened = !scene.IsValid() || !scene.isLoaded;
            if (!opened && scene.isDirty)
            {
                report.warnings.Add(path + ": save your scene edits before repairing its static meshes.");
                return;
            }
            if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            int repairedBefore = report.repairedRenderers;
            try
            {
                RepairRoots(scene.GetRootGameObjects(), output, report, cancellation);
            }
            finally
            {
                try
                {
                    // Keep completed repairs if the user cancels halfway through
                    // a large scene; a later run can finish the remaining objects.
                    if (report.repairedRenderers > repairedBefore)
                    {
                        EditorSceneManager.MarkSceneDirty(scene);
                        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Unity could not save the repaired scene.");
                        report.repairedAssets.Add(path);
                    }
                }
                finally { if (opened && scene.IsValid()) EditorSceneManager.CloseScene(scene, true); }
            }
        }

        private static void RepairPrefab(string path, string output, StaticMeshRepairReport report, CancellationToken cancellation)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            int repairedBefore = report.repairedRenderers;
            try
            {
                RepairRoots(new[] { root }, output, report, cancellation);
            }
            finally
            {
                try
                {
                    if (report.repairedRenderers > repairedBefore)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
                        if (!success) throw new IOException("Unity could not save the repaired prefab.");
                        report.repairedAssets.Add(path);
                    }
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
        }

        private static bool RepairRoots(IEnumerable<GameObject> roots, string output, StaticMeshRepairReport report, CancellationToken cancellation)
        {
            bool changed = false;
            var sources = new Dictionary<Mesh, MeshChannels>();
            foreach (var renderer in roots.SelectMany(root => root.GetComponentsInChildren<MeshRenderer>(true)))
            {
                cancellation.ThrowIfCancellationRequested();
                var serialized = new SerializedObject(renderer);
                var info = serialized.FindProperty("m_StaticBatchInfo");
                int first = info?.FindPropertyRelative("firstSubMesh")?.intValue ?? 0;
                int count = info?.FindPropertyRelative("subMeshCount")?.intValue ?? 0;
                if (count == 0) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                var source = filter != null ? filter.sharedMesh : null;
                if (source == null || first < 0 || count < 1 || first + count > source.subMeshCount)
                {
                    report.warnings.Add(renderer.name + ": static batch has no valid mesh/submesh range; original data retained.");
                    continue;
                }
                try
                {
                    var batchRoot = serialized.FindProperty("m_StaticBatchRoot")?.objectReferenceValue as Transform;
                    var toLocal = renderer.transform.worldToLocalMatrix * (batchRoot != null ? batchRoot.localToWorldMatrix : Matrix4x4.identity);
                    if (Mathf.Abs(toLocal.determinant) < 1e-10f) throw new InvalidDataException("Static batch transform has zero scale.");
                    if (!sources.TryGetValue(source, out var channels)) sources.Add(source, channels = new MeshChannels(source));
                    var mesh = Extract(source, channels, first, count, toLocal);
                    mesh.name = renderer.name + " (editable)";
                    var meshFolder = output + "/_EditableMeshes";
                    if (!AssetDatabase.IsValidFolder(meshFolder)) AssetDatabase.CreateFolder(output, "_EditableMeshes");
                    var meshPath = AssetDatabase.GenerateUniqueAssetPath(meshFolder + "/Mesh.asset");
                    AssetDatabase.CreateAsset(mesh, meshPath);
                    // Assigning a different mesh resets Unity's native static batch state.
                    filter.sharedMesh = mesh;
                    foreach (var collider in renderer.GetComponents<MeshCollider>())
                        if (collider.sharedMesh == source) collider.sharedMesh = mesh;
                    serialized.Update();
                    info = serialized.FindProperty("m_StaticBatchInfo");
                    if (info != null)
                    {
                        info.FindPropertyRelative("firstSubMesh").intValue = 0;
                        info.FindPropertyRelative("subMeshCount").intValue = 0;
                    }
                    var rootProperty = serialized.FindProperty("m_StaticBatchRoot");
                    if (rootProperty != null) rootProperty.objectReferenceValue = null;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    // The compiler has already applied these transforms to UV1/UV2.
                    renderer.lightmapScaleOffset = new Vector4(1, 1, 0, 0);
                    renderer.realtimeLightmapScaleOffset = new Vector4(1, 1, 0, 0);
                    GameObjectUtility.SetStaticEditorFlags(renderer.gameObject,
                        GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & ~StaticEditorFlags.BatchingStatic);
                    EditorUtility.SetDirty(filter);
                    EditorUtility.SetDirty(renderer);
                    report.repairedRenderers++;
                    changed = true;
                }
                catch (Exception ex) { report.warnings.Add(renderer.name + ": " + ex.GetBaseException().Message); }
            }
            return changed;
        }

        private sealed class MeshChannels
        {
            public readonly Vector3[] positions, normals;
            public readonly Vector4[] tangents;
            public readonly Color[] colors;
            public readonly List<Vector4>[] uv = new List<Vector4>[8];
            public readonly int[] uvDimensions = new int[8];

            public MeshChannels(Mesh source)
            {
                // Editor access works on imported read-only mesh assets too.
                positions = source.vertices;
                normals = source.normals;
                tangents = source.tangents;
                colors = source.colors;
                for (int i = 0; i < 8; i++)
                {
                    uv[i] = new List<Vector4>();
                    source.GetUVs(i, uv[i]);
                    var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + i);
                    uvDimensions[i] = source.HasVertexAttribute(attribute) ? source.GetVertexAttributeDimension(attribute) : 0;
                }
            }
        }

        private static Mesh Extract(Mesh source, MeshChannels channels, int first, int count, Matrix4x4 toLocal)
        {
            var oldIndices = new List<int>();
            var mapping = new Dictionary<int, int>();
            var subsets = new int[count][];
            for (int sub = 0; sub < count; sub++)
            {
                var indices = source.GetIndices(first + sub, true);
                for (int i = 0; i < indices.Length; i++)
                {
                    int old = indices[i];
                    if (old < 0 || old >= channels.positions.Length) throw new InvalidDataException("Static batch contains an invalid vertex index.");
                    if (!mapping.TryGetValue(old, out int remapped))
                    {
                        remapped = oldIndices.Count;
                        oldIndices.Add(old);
                        mapping.Add(old, remapped);
                    }
                    indices[i] = remapped;
                }
                subsets[sub] = indices;
            }
            if (oldIndices.Count == 0) throw new InvalidDataException("Static batch has no indexed geometry.");
            var mesh = new Mesh { indexFormat = oldIndices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                mesh.SetVertices(oldIndices.Select(i => toLocal.MultiplyPoint3x4(channels.positions[i])).ToList());
                var normalMatrix = toLocal.inverse.transpose;
                if (channels.normals.Length == channels.positions.Length)
                    mesh.SetNormals(oldIndices.Select(i => normalMatrix.MultiplyVector(channels.normals[i]).normalized).ToList());
                if (channels.tangents.Length == channels.positions.Length)
                {
                    float sign = toLocal.determinant < 0 ? -1 : 1;
                    mesh.SetTangents(oldIndices.Select(i =>
                    {
                        var t = channels.tangents[i];
                        var direction = toLocal.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                        return new Vector4(direction.x, direction.y, direction.z, t.w * sign);
                    }).ToList());
                }
                if (channels.colors.Length == channels.positions.Length) mesh.SetColors(oldIndices.Select(i => channels.colors[i]).ToList());
                for (int channel = 0; channel < 8; channel++)
                {
                    var values = channels.uv[channel];
                    if (values.Count != channels.positions.Length) continue;
                    if (channels.uvDimensions[channel] == 2) mesh.SetUVs(channel, oldIndices.Select(i => (Vector2)values[i]).ToList());
                    else if (channels.uvDimensions[channel] == 3) mesh.SetUVs(channel, oldIndices.Select(i => (Vector3)values[i]).ToList());
                    else mesh.SetUVs(channel, oldIndices.Select(i => values[i]).ToList());
                }
                mesh.subMeshCount = count;
                for (int sub = 0; sub < count; sub++)
                {
                    var topology = source.GetTopology(first + sub);
                    // Mirrored transforms reverse face winding when baking/unbaking.
                    if (toLocal.determinant < 0)
                    {
                        int faceSize = topology == MeshTopology.Triangles ? 3 : topology == MeshTopology.Quads ? 4 : 0;
                        if (faceSize != 0)
                            for (int face = 0; face < subsets[sub].Length; face += faceSize)
                                Array.Reverse(subsets[sub], face + 1, faceSize - 1);
                    }
                    mesh.SetIndices(subsets[sub], topology, sub, false);
                }
                mesh.RecalculateBounds();
                return mesh;
            }
            catch { Object.DestroyImmediate(mesh); throw; }
        }
    }
}
