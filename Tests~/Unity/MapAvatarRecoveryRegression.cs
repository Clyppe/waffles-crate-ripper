// Run in the isolated warehouse fixture project, with the fake SDK/catalog fixtures.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using SLZ.Marrow.Warehouse;
using SyntheticFixture;
using Waffles.CrateRipper;
using Object = UnityEngine.Object;
using ResourceLocation = SyntheticFixture.ResourceLocation;

public static class MapAvatarRecoveryRegression
{
    private static int passed;
    public static void Run()
    {
        passed = 0;
        VerifyMapCatalogScope();
        VerifyAuthoredAvatarRig();
        Debug.Log("WAFFLES_MAP_AVATAR_REGRESSION_OK checks=" + passed);
    }

    private static void VerifyMapCatalogScope()
    {
        var folder = Path.GetFullPath("MapCatalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var sceneBundle = Write(folder, "selected-scene.bundle", "selected map");
        var chunkBundle = Write(folder, "selected-chunk.bundle", "selected chunk");
        var sharedBundle = Write(folder, "shared-material.bundle", "selected dependencies");
        var otherBundle = Write(folder, "unrelated-map.bundle", "not selected");
        var main = Asset("Assets/Maps/Selected.unity"); main.Dependencies.Add(Bundle(sceneBundle));
        var chunk = Asset("Assets/Maps/Chunk.unity"); chunk.Dependencies.Add(Bundle(chunkBundle));
        chunk.Dependencies.Add(Bundle(sharedBundle));
        var other = Asset("Assets/Maps/Other.unity"); other.Dependencies.Add(Bundle(otherBundle));
        var catalog = new FakeCatalog();
        catalog.Locations["11111111111111111111111111111111"] = new[] { main };
        catalog.Locations["22222222222222222222222222222222"] = new[] { chunk };
        catalog.Locations["33333333333333333333333333333333"] = new[] { other };
        var warehouse = new AssetWarehouse();
        warehouse.Manifests.Add(new PalletManifest { PalletBarcode = "fixture.maps", PalletPath = folder, Catalog = catalog });
        AssetWarehouse.Instance = warehouse;
        var crate = new CrateEntry { Name = "Selected", Kind = "LevelCrate", MainAssetGuid = "11111111111111111111111111111111",
            PalletBarcode = "fixture.maps", SourceFolder = folder, SourceRoot = folder,
            AdditionalAssets = new List<CrateAssetEntry> { new CrateAssetEntry { Guid = "22222222222222222222222222222222" } } };
        var files = ((IEnumerable)Invoke("BundleFiles", crate)).Cast<string>().ToArray();
        Require(files.Length == 3 && files.Contains(sceneBundle) && files.Contains(chunkBundle) && files.Contains(sharedBundle), "Map includes its main scene, explicit chunk, and shared dependencies");
        Require(!files.Contains(otherBundle), "A different level sharing the catalog stays excluded");
        Require(crate.OriginalAssetPath == "Assets/Maps/Selected.unity" && crate.AdditionalAssets[0].OriginalAssetPath == "Assets/Maps/Chunk.unity", "Catalog scene identities survive for root matching");
        var export = Path.Combine(folder, "Export");
        Write(export, "Maps/Selected.unity", "%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n");
        Write(export, "Maps/Chunk.unity", "%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n");
        Write(export, "Props/Selected.prefab", "%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n");
        Require((string)Invoke("MatchPrefab", export, crate) == "Maps/Selected.unity", "Scene main root wins over a same-named prop");
        var extraArgs = new object[] { export, crate, null };
        var extras = ((IEnumerable)InvokeArgs("MatchAdditionalAssets", extraArgs)).Cast<string>().ToArray();
        Require(extras.SequenceEqual(new[] { "Maps/Chunk.unity" }), "Explicit chunk is matched as an additional import root");
        AssetWarehouse.Instance = null;
    }

    private static void VerifyAuthoredAvatarRig()
    {
        var source = "Assets/AvatarSource-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(source); AssetDatabase.Refresh();
        var root = new GameObject("Avatar source");
        var hips = new GameObject("Hips"); hips.transform.SetParent(root.transform, false);
        var head = new GameObject("Head"); head.transform.SetParent(hips.transform, false); head.transform.localPosition = Vector3.up;
        var mesh = new Mesh { name = "Bound avatar mesh" };
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
        mesh.triangles = new[] { 0, 1, 2 };
        mesh.bindposes = new[] { hips.transform.worldToLocalMatrix * root.transform.localToWorldMatrix };
        mesh.boneWeights = Enumerable.Range(0, 3).Select(i => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
        AssetDatabase.CreateAsset(mesh, source + "/RigMesh.asset");
        var skin = root.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.rootBone = hips.transform; skin.bones = new[] { hips.transform };
        var avatar = AvatarBuilder.BuildGenericAvatar(root, "Hips");
        AssetDatabase.CreateAsset(avatar, source + "/OriginalRig.asset");
        root.AddComponent<Animator>().avatar = avatar;
        var prefabPath = source + "/Avatar.prefab";
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath); Object.DestroyImmediate(root); AssetDatabase.SaveAssets();
        var crate = new CrateEntry { Name = "Avatar", Kind = "AvatarCrate", LocalPrefabPath = prefabPath, OriginalAssetPath = prefabPath, MainAssetGuid = AssetDatabase.AssetPathToGUID(prefabPath) };
        var job = Path.GetFullPath("AuthoredAvatarJob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        var staged = (string)Invoke("StageProjectCrate", crate, job);
        Require(File.Exists(Path.Combine(staged, source, "RigMesh.asset")) && File.Exists(Path.Combine(staged, source, "OriginalRig.asset")), "Staging includes the skinned mesh and original Avatar rig asset");
        var destination = "Assets/RecoveredAvatar-" + Guid.NewGuid().ToString("N");
        var report = RecoveryPipeline.Import(staged, destination, prefabPath, false, new Dictionary<string, ScriptBinding>());
        AssetDatabase.Refresh();
        var recovered = AssetDatabase.LoadAssetAtPath<GameObject>(destination + "/" + prefabPath);
        Require(recovered != null, "Recovered avatar prefab imports in Unity");
        var renderer = recovered.GetComponent<SkinnedMeshRenderer>();
        Require(renderer != null && renderer.sharedMesh != null && renderer.bones.Length == 1 && renderer.bones[0] == recovered.transform.Find("Hips"), "Skinned renderer references the recovered hierarchy and mesh");
        Require(recovered.GetComponent<Animator>().avatar != null && recovered.transform.Find("Hips/Head") != null, "Original Animator Avatar reference and child bone hierarchy survive GUID remapping");
        Require(report.missingAssetReferences.Count == 0, "Original avatar rig closure has no missing asset references");
    }

    private static Type Bindings { get { return Type.GetType("Waffles.CrateRipper.UnityBindings, Waffles.CrateRipper.Editor", true); } }
    private static object Invoke(string method, params object[] args) { return InvokeArgs(method, args); }
    private static object InvokeArgs(string method, object[] args) { return Bindings.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, args); }
    private static ResourceLocation Asset(string id) { return new ResourceLocation { InternalId = id, ProviderId = "BundledAssetProvider", ResourceType = typeof(GameObject) }; }
    private static ResourceLocation Bundle(string id) { return new ResourceLocation { InternalId = id, ProviderId = "AssetBundleProvider", ResourceType = typeof(IAssetBundleResource) }; }
    private static string Write(string folder, string relative, string text) { var path = Path.GetFullPath(Path.Combine(folder, relative)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text); return path; }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); passed++; Debug.Log("MAP_AVATAR_PASS " + message); }
}
