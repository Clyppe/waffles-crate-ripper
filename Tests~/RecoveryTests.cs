using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Waffles.CrateRipper.Tests
{
    internal static class RecoveryTests
    {
        private const string PrefabGuid = "11111111111111111111111111111111";
        private const string MaterialGuid = "22222222222222222222222222222222";
        private const string TextureGuid = "33333333333333333333333333333333";
        private const string MeshGuid = "44444444444444444444444444444444";
        private const string ShaderGuid = "55555555555555555555555555555555";
        private const string ScriptGuid = "66666666666666666666666666666666";
        private const string MissingGuid = "77777777777777777777777777777777";
        private const string UnrelatedGuid = "88888888888888888888888888888888";
        private const string InstalledScriptGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string InstalledShaderGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Crate dependency closure remaps GUIDs and preserves texture bytes", DependencyClosure));
            tests.Add(new KeyValuePair<string, Action>("SDK script repair preserves serialized component fields", ScriptRepair));
            tests.Add(new KeyValuePair<string, Action>("Whole mod retains source safely and copies binary sidecars", WholeMod));
            tests.Add(new KeyValuePair<string, Action>("Recovery rejects overwrite and prefab traversal", DestinationSafety));
            tests.Add(new KeyValuePair<string, Action>("Empty editor class identifiers do not consume following fields", EmptyClassIdentifier));
            tests.Add(new KeyValuePair<string, Action>("Metadata dependencies and inert TextAsset bytes survive import", MetadataAndText));
            tests.Add(new KeyValuePair<string, Action>("External editor type identities repair exact SDK references", ExternalScriptIdentity));
            tests.Add(new KeyValuePair<string, Action>("Duplicate asset GUIDs abort without importing", DuplicateGuids));
            tests.Add(new KeyValuePair<string, Action>("Canceled recovery leaves the destination untouched", RecoveryCancellation));
            tests.Add(new KeyValuePair<string, Action>("Selected crate retains only its linked streaming resources", SelectedSidecarIsolation));
            tests.Add(new KeyValuePair<string, Action>("Missing streaming resources are reported without unrelated payloads", MissingStreamingResource));
            tests.Add(new KeyValuePair<string, Action>("Bundle staging copies only explicit dependencies and own sidecars", BundleIsolation));
            tests.Add(new KeyValuePair<string, Action>("Bundle staging preserves colliding filenames and colocates sidecars", BundleFilenameCollisions));
            tests.Add(new KeyValuePair<string, Action>("Bundle staging rejects missing inputs and overwrites", BundleDestinationSafety));
            tests.Add(new KeyValuePair<string, Action>("Canceled bundle staging removes only its temporary output", BundleCancellation));
            tests.Add(new KeyValuePair<string, Action>("Pallet folder lookup preserves platform alternatives and zero-crate dependencies", PalletFolderCandidates));
        }

        private static string BuildAssets(Fixture fixture)
        {
            var root = fixture.PathFor("export/Assets");
            fixture.Asset("export/Assets/Prefabs/Fixture Crate.prefab", PrefabGuid,
                "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &100\nGameObject:\n  m_Name: Fixture Crate\n" +
                "--- !u!114 &200\nMonoBehaviour:\n  m_GameObject: {fileID: 100}\n  m_Script: {fileID: 11500000, guid: " + ScriptGuid + ", type: 3}\n" +
                "  health: 32\n  nestedSettings:\n    speedMultiplier: 2.5\n    offsets: [1, 2, 3]\n" +
                "  material: {fileID: 2100000, guid: " + MaterialGuid + ", type: 2}\n  mesh: {fileID: 4300000, guid: " + MeshGuid + ", type: 2}\n" +
                "--- !u!114 &201\nMonoBehaviour:\n  m_GameObject: {fileID: 100}\n  m_Script: {fileID: 11500000, guid: " + MissingGuid + ", type: 3}\n  serializedCustomValue: 987\n");
            fixture.Asset("export/Assets/Materials/Skin.mat", MaterialGuid,
                "%YAML 1.1\n--- !u!21 &2100000\nMaterial:\n  m_Name: Skin\n" +
                "  m_Shader: {fileID: 4800000, guid: " + ShaderGuid + ", type: 3}\n  m_SavedProperties:\n    m_TexEnvs:\n    - _MainTex:\n        m_Texture: {fileID: 2800000, guid: " + TextureGuid + ", type: 3}\n        m_Scale: {x: 1, y: 1}\n");
            fixture.Asset("export/Assets/Meshes/Shape.asset", MeshGuid,
                "%YAML 1.1\n--- !u!43 &4300000\nMesh:\n  m_Name: Shape\n  m_VertexCount: 3\n");
            fixture.Asset("export/Assets/Shaders/Skin.shader", ShaderGuid,
                "Shader \"Fixture/Skin\" { Properties { _MainTex (\"Albedo\", 2D) = \"white\" {} } SubShader { Pass { } } }\n");
            fixture.Asset("export/Assets/Scripts/FixtureSdk/Grip.cs", ScriptGuid,
                "namespace FixtureSdk { public class Grip { public float health; } }\n");
            fixture.Asset("export/Assets/Scripts/FixtureSdk/Missing.cs", MissingGuid,
                "namespace FixtureSdk { public class Missing { public int serializedCustomValue; } }\n");
            fixture.WriteBytes("export/Assets/Textures/Albedo.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jQXcAAAAASUVORK5CYII="));
            fixture.Write("export/Assets/Textures/Albedo.png.meta", "fileFormatVersion: 2\nguid: " + TextureGuid + "\nTextureImporter:\n  sRGBTexture: 1\n");
            fixture.Asset("export/Assets/Materials/Unrelated.mat", UnrelatedGuid,
                "%YAML 1.1\n--- !u!21 &2100000\nMaterial:\n  m_Name: Unrelated\n");
            return root;
        }

        private static Dictionary<string, ScriptBinding> Scripts()
        {
            return new Dictionary<string, ScriptBinding> {
                ["FixtureSdk.Grip"] = new ScriptBinding { FullTypeName = "FixtureSdk.Grip", Guid = InstalledScriptGuid, FileId = 321987 }
            };
        }

        private static Dictionary<string, ShaderBinding> Shaders()
        {
            return new Dictionary<string, ShaderBinding> {
                ["Fixture/Skin"] = new ShaderBinding { Name = "Fixture/Skin", Guid = InstalledShaderGuid, FileId = 876543 }
            };
        }

        private static RecoveryReport ImportInProject(Fixture fixture, string source, string destination, string prefab, bool whole,
            IDictionary<string, ScriptBinding> scripts, IDictionary<string, ShaderBinding> shaders = null, CancellationToken cancellation = default(CancellationToken))
        {
            var project = fixture.PathFor("project");
            Directory.CreateDirectory(Path.Combine(project, "Assets"));
            var previous = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(project);
                return RecoveryPipeline.Import(source, destination, prefab, whole, scripts, shaders, cancellation);
            }
            finally { Directory.SetCurrentDirectory(previous); }
        }

        private static string ImportedRoot(Fixture fixture, string name)
        {
            return fixture.PathFor("project/Assets/" + name);
        }

        private static string OnlyAsset(string root, string pattern)
        {
            var matches = Directory.GetFiles(root, pattern, SearchOption.AllDirectories).Where(p => !p.Contains("_RecoveredSource")).ToArray();
            Check.Equal(1, matches.Length, "Expected one imported asset matching " + pattern);
            return matches[0];
        }

        private static string MetaGuid(string path)
        {
            var match = Regex.Match(File.ReadAllText(path + ".meta"), @"(?m)^guid:\s*([a-fA-F0-9]{32})\s*$");
            Check.True(match.Success, "Imported assets require a Unity metadata GUID: " + path);
            return match.Groups[1].Value;
        }

        private static void DependencyClosure()
        {
            using (var fixture = new Fixture("recovery-closure"))
            {
                var source = BuildAssets(fixture);
                var report = ImportInProject(fixture, source, "Assets/Fixture Crate", "Prefabs/Fixture Crate.prefab", false, Scripts(), Shaders());
                var output = ImportedRoot(fixture, "Fixture Crate");
                var prefab = OnlyAsset(output, "Fixture Crate.prefab");
                var material = OnlyAsset(output, "Skin.mat");
                var texture = OnlyAsset(output, "Albedo.png");
                var mesh = OnlyAsset(output, "Shape.asset");
                Check.True(!Directory.GetFiles(output, "Unrelated.mat", SearchOption.AllDirectories).Any(), "Single crate imports must exclude unrelated assets.");
                Check.SameBytes(fixture.PathFor("export/Assets/Textures/Albedo.png"), texture, "Binary texture bytes must survive recovery unchanged.");
                var importedGuids = new[] { MetaGuid(prefab), MetaGuid(material), MetaGuid(texture), MetaGuid(mesh) };
                Check.Equal(importedGuids.Length, importedGuids.Distinct().Count(), "Recovered assets must have distinct GUIDs.");
                Check.True(!importedGuids.Intersect(new[] { PrefabGuid, MaterialGuid, TextureGuid, MeshGuid }).Any(), "Imported assets must receive fresh GUIDs.");
                var prefabText = File.ReadAllText(prefab);
                var materialText = File.ReadAllText(material);
                Check.True(prefabText.Contains("guid: " + MetaGuid(material)) && prefabText.Contains("guid: " + MetaGuid(mesh)), "Prefab dependencies must point to the recovered assets.");
                Check.True(materialText.Contains("guid: " + MetaGuid(texture)), "Material texture references must point to the recovered texture.");
                Check.True(File.ReadAllText(texture + ".meta").Contains("sRGBTexture: 1"), "Importer metadata beyond GUIDs must survive recovery.");
                Check.True(report.copiedFiles > 0 && report.prefabs.Count > 0, "Recovery should report copied content and prefabs.");
            }
        }

        private static void ScriptRepair()
        {
            using (var fixture = new Fixture("recovery-scripts"))
            {
                var source = BuildAssets(fixture);
                var report = ImportInProject(fixture, source, "Assets/Fixture Crate", "Prefabs/Fixture Crate.prefab", false, Scripts(), Shaders());
                var output = ImportedRoot(fixture, "Fixture Crate");
                var prefab = File.ReadAllText(OnlyAsset(output, "Fixture Crate.prefab"));
                Check.True(prefab.Contains("m_Script: {fileID: 321987, guid: " + InstalledScriptGuid), "Exact installed script type should replace both script GUID and local file ID.");
                Check.True(prefab.Contains("  health: 32\n  nestedSettings:\n    speedMultiplier: 2.5\n    offsets: [1, 2, 3]"), "Script rebinding must preserve serialized nested fields.");
                Check.True(prefab.Contains("  serializedCustomValue: 987"), "Unresolved component fields must be preserved.");
                Check.True(report.repairedScriptReferences == 1, "Only the installed exact script type should be rebound.");
                Check.True(report.missingScripts.Any(p => p.Contains("FixtureSdk.Missing")), "Unknown script types must be reported for manual repair.");
                Check.True(!Directory.GetFiles(output, "*.cs", SearchOption.AllDirectories).Any(), "Recovered script source must not compile automatically.");
                Check.True(Directory.GetFiles(output, "*.cs.txt", SearchOption.AllDirectories).Length >= 1, "Recovered script source should remain available as inert text.");
                var material = File.ReadAllText(OnlyAsset(output, "Skin.mat"));
                Check.True(material.Contains("m_Shader: {fileID: 876543, guid: " + InstalledShaderGuid), "Installed shaders should replace both GUID and local file ID.");
                Check.Equal(1, report.reusedShaders, "Reused shader references should be counted.");
            }
        }

        private static void WholeMod()
        {
            using (var fixture = new Fixture("recovery-whole"))
            {
                var source = BuildAssets(fixture);
                var sidecar = fixture.WriteBytes("export/Assets/Textures/Albedo.png.resS", new byte[] { 16, 0, 0, 42, 255, 254 });
                fixture.WriteBytes("export/Assets/Scripts/Recovered.dll", new byte[] { 77, 90, 0, 0, 42 });
                fixture.Write("export/Assets/Scripts/Recovered.asmdef", "{\"name\":\"Recovered.Untrusted\"}");
                fixture.WriteBytes("export/Assets/Serialized/Binary.asset", new byte[] { 0, 0, 0, 24, 0, 0, 0, 88, 0, 0, 0, 17, 0, 0, 0, 0 });
                var report = ImportInProject(fixture, source, "Assets/Whole Mod", null, true, Scripts(), Shaders());
                var output = ImportedRoot(fixture, "Whole Mod");
                Check.True(Directory.GetFiles(output, "Unrelated.mat", SearchOption.AllDirectories).Any(), "Whole mod imports must include assets outside one crate's dependency graph.");
                var importedSidecar = Directory.GetFiles(output, "Albedo.png.resS", SearchOption.AllDirectories).Single();
                Check.SameBytes(sidecar, importedSidecar, "Resource sidecars must be copied byte for byte.");
                Check.True(!Directory.GetFiles(output, "*.dll", SearchOption.AllDirectories).Any(), "Recovered assemblies must not load into Unity.");
                Check.True(!Directory.GetFiles(output, "*.asmdef", SearchOption.AllDirectories).Any(), "Recovered assembly definitions must not compile into Unity.");
                Check.True(Directory.GetFiles(output, "*.dll.txt", SearchOption.AllDirectories).Any() && Directory.GetFiles(output, "*.asmdef.txt", SearchOption.AllDirectories).Any(), "Recovered code and assembly definitions should remain available as inert files.");
                Check.True(!Directory.GetFiles(output, "Binary.asset", SearchOption.AllDirectories).Any(), "Unsupported binary Unity serialization must not import as a misleading asset.");
                Check.True(Directory.GetFiles(output, "Binary.asset.bin", SearchOption.AllDirectories).Any(), "Unsupported serialized assets should be preserved for manual inspection.");
                Check.True(report.warnings.Count > 0, "Recovery limitations should be present in the report.");
            }
        }

        private static void DestinationSafety()
        {
            using (var fixture = new Fixture("recovery-destination"))
            {
                var source = BuildAssets(fixture);
                var sentinel = fixture.Write("project/Assets/Existing/keep.txt", "keep");
                Check.Throws<IOException>(() => ImportInProject(fixture, source, "Assets/Existing", "Prefabs/Fixture Crate.prefab", false, Scripts()), "Existing output folders must not be overwritten");
                Check.Equal("keep", File.ReadAllText(sentinel), "Existing output files must survive rejected recovery.");
                Check.Throws<Exception>(() => ImportInProject(fixture, source, "Assets/Traversal", "../escape.prefab", false, Scripts()), "Selected prefabs must remain inside the exported asset tree");
                Check.True(!Directory.Exists(ImportedRoot(fixture, "Traversal")), "Invalid selections must fail before creating an import folder.");
            }
        }

        private static void EmptyClassIdentifier()
        {
            using (var fixture = new Fixture("recovery-empty-identifier"))
            {
                var source = BuildAssets(fixture);
                var prefab = fixture.PathFor("export/Assets/Prefabs/Fixture Crate.prefab");
                File.WriteAllText(prefab, File.ReadAllText(prefab).Replace("  health: 32", "  m_EditorClassIdentifier: \n  health: 32"));
                var report = ImportInProject(fixture, source, "Assets/Empty Identifier", "Prefabs/Fixture Crate.prefab", false, Scripts());
                Check.Equal(1, report.repairedScriptReferences, "An empty class identifier must not override a valid exported script identity.");
                var text = File.ReadAllText(OnlyAsset(ImportedRoot(fixture, "Empty Identifier"), "Fixture Crate.prefab"));
                Check.True(text.Contains("  m_EditorClassIdentifier: \n  health: 32"), "Empty identifiers and the next serialized field must remain unchanged.");
            }
        }

        private static void MetadataAndText()
        {
            using (var fixture = new Fixture("recovery-metadata"))
            {
                var source = BuildAssets(fixture);
                var metaTextureGuid = "cccccccccccccccccccccccccccccccc";
                var noteGuid = "dddddddddddddddddddddddddddddddd";
                var extraTexture = fixture.WriteBytes("export/Assets/Textures/MetaOnly.png", File.ReadAllBytes(fixture.PathFor("export/Assets/Textures/Albedo.png")));
                fixture.Write("export/Assets/Textures/MetaOnly.png.meta", "fileFormatVersion: 2\nguid: " + metaTextureGuid + "\n");
                File.AppendAllText(fixture.PathFor("export/Assets/Meshes/Shape.asset.meta"), "NativeFormatImporter:\n  externalObjects:\n  - reference: {fileID: 2800000, guid: " + metaTextureGuid + ", type: 3}\n  - script: {fileID: 11500000, guid: " + ScriptGuid + ", type: 3}\n");
                var note = fixture.Asset("export/Assets/Notes.txt", noteGuid, "This literal GUID must remain exactly: guid: " + MaterialGuid + "\n");
                File.AppendAllText(fixture.PathFor("export/Assets/Prefabs/Fixture Crate.prefab"), "  notes: {fileID: 4900000, guid: " + noteGuid + ", type: 3}\n");
                ImportInProject(fixture, source, "Assets/Metadata", "Prefabs/Fixture Crate.prefab", false, Scripts());
                var output = ImportedRoot(fixture, "Metadata");
                var importedTexture = OnlyAsset(output, "MetaOnly.png");
                Check.SameBytes(extraTexture, importedTexture, "A dependency mentioned only by importer metadata must be recovered.");
                var mesh = OnlyAsset(output, "Shape.asset");
                var meta = File.ReadAllText(mesh + ".meta");
                Check.True(meta.Contains("guid: " + MetaGuid(importedTexture)), "Metadata references must use the new texture GUID.");
                Check.True(meta.Contains("script: {fileID: 321987, guid: " + InstalledScriptGuid), "Metadata script references must reuse the installed SDK's GUID and local file ID.");
                Check.SameBytes(note, OnlyAsset(output, "Notes.txt"), "Arbitrary TextAsset GUID-like text must not be rewritten.");
            }
        }

        private static void ExternalScriptIdentity()
        {
            using (var fixture = new Fixture("recovery-external-script"))
            {
                var source = BuildAssets(fixture);
                File.Delete(fixture.PathFor("export/Assets/Scripts/FixtureSdk/Grip.cs"));
                File.Delete(fixture.PathFor("export/Assets/Scripts/FixtureSdk/Grip.cs.meta"));
                var path = fixture.PathFor("export/Assets/Prefabs/Fixture Crate.prefab");
                File.WriteAllText(path, File.ReadAllText(path).Replace("  health: 32", "  m_EditorClassIdentifier: Fixture.Assembly::FixtureSdk.Grip\n  health: 32"));
                var report = ImportInProject(fixture, source, "Assets/External Script", "Prefabs/Fixture Crate.prefab", false, Scripts());
                Check.Equal(1, report.repairedScriptReferences, "An exact exported editor type identity should repair an external script without source.");
                Check.True(File.ReadAllText(OnlyAsset(ImportedRoot(fixture, "External Script"), "Fixture Crate.prefab")).Contains("m_Script: {fileID: 321987, guid: " + InstalledScriptGuid), "External script repair must use the installed MonoScript reference.");
            }
        }

        private static void DuplicateGuids()
        {
            using (var fixture = new Fixture("recovery-duplicate-guid"))
            {
                var source = BuildAssets(fixture);
                fixture.Asset("export/Assets/Duplicate.mat", MaterialGuid, "%YAML 1.1\n--- !u!21 &2100000\nMaterial:\n  m_Name: Duplicate\n");
                Check.Throws<InvalidDataException>(() => ImportInProject(fixture, source, "Assets/Duplicate", "Prefabs/Fixture Crate.prefab", false, Scripts()), "Ambiguous asset GUIDs must fail before importing");
                Check.True(!Directory.Exists(ImportedRoot(fixture, "Duplicate")), "A duplicate GUID failure must not leave imported assets.");
            }
        }

        private static void RecoveryCancellation()
        {
            using (var fixture = new Fixture("recovery-cancel"))
            using (var cancellation = new CancellationTokenSource())
            {
                var source = BuildAssets(fixture);
                cancellation.Cancel();
                Check.Throws<OperationCanceledException>(() => ImportInProject(fixture, source, "Assets/Canceled", "Prefabs/Fixture Crate.prefab", false, Scripts(), cancellation: cancellation.Token), "Canceled recovery must stop without importing");
                Check.True(!Directory.Exists(ImportedRoot(fixture, "Canceled")), "Canceled imports must leave the output folder absent.");
                var library = fixture.PathFor("project/Library");
                Check.True(!Directory.Exists(library) || !Directory.GetDirectories(library, "WafflesCrateImport-*").Any(), "Canceled imports must not leave staging folders.");
            }
        }

        private static void SelectedSidecarIsolation()
        {
            using (var fixture = new Fixture("recovery-sidecar-isolation"))
            {
                var source = BuildAssets(fixture);
                var sibling = fixture.WriteBytes("export/Assets/Textures/Albedo.png.resS", new byte[] { 0, 17, 99, 255 });
                var linked = fixture.WriteBytes("export/Assets/Payload/CAB-selected.resS", new byte[] { 0, 28, 67, 255 });
                File.AppendAllText(fixture.PathFor("export/Assets/Meshes/Shape.asset"),
                    "  m_StreamData: {offset: 0, size: 4, path: archive:/CAB-selected/CAB-selected.resS}\n");
                fixture.WriteBytes("export/Assets/Payload/CAB-unrelated.resS", new byte[] { 0, 64, 88 });
                fixture.WriteBytes("export/Assets/Payload/EntireGame.resource", new byte[] { 0, 98, 89 });
                fixture.WriteBytes("export/Assets/Payload/Unrelated.bin", new byte[] { 0, 34, 12 });
                fixture.WriteBytes("export/Assets/Textures/Albedo2.png.resS", new byte[] { 0, 91, 82 });
                var notesGuid = "99999999999999999999999999999999";
                fixture.Asset("export/Assets/Notes.txt", notesGuid,
                    "{\"uri\":\"Payload/Unrelated.bin\"}\npath: Payload/CAB-unrelated.resS\n");
                File.AppendAllText(fixture.PathFor("export/Assets/Prefabs/Fixture Crate.prefab"),
                    "  notes: {fileID: 4900000, guid: " + notesGuid + ", type: 3}\n  debugText: \"path: Payload/CAB-unrelated.resS\"\n");
                fixture.Asset("export/Assets/Prefabs/Unrelated.prefab", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                    "%YAML 1.1\n--- !u!1 &100\nGameObject:\n  m_Name: Unrelated\n");
                var report = ImportInProject(fixture, source, "Assets/Isolated Crate", "Prefabs/Fixture Crate.prefab", false, Scripts());
                var output = ImportedRoot(fixture, "Isolated Crate");
                Check.SameBytes(sibling, OnlyAsset(output, "Albedo.png.resS"), "An exact owning-asset sidecar must be retained.");
                Check.SameBytes(linked, OnlyAsset(output, "CAB-selected.resS"), "An explicitly referenced archive streaming resource must be retained.");
                Check.True(!Directory.GetFiles(output, "CAB-unrelated.resS", SearchOption.AllDirectories).Any(), "Unrelated streaming resources must not be copied.");
                Check.True(!Directory.GetFiles(output, "EntireGame.resource", SearchOption.AllDirectories).Any(), "Unrelated resource files must not be copied.");
                Check.True(!Directory.GetFiles(output, "Unrelated.bin", SearchOption.AllDirectories).Any(), "Unrelated binary payloads must not be copied.");
                Check.True(!Directory.GetFiles(output, "Albedo2.png.resS", SearchOption.AllDirectories).Any(), "Similar sidecar prefixes must not pull other textures.");
                Check.True(!Directory.GetFiles(output, "Unrelated.prefab", SearchOption.AllDirectories).Any(), "Unrelated prefab hierarchies must remain excluded.");
                Check.Equal(1, report.prefabs.Count, "A selected crate must not import other unreferenced prefabs.");
            }
        }

        private static void MissingStreamingResource()
        {
            using (var fixture = new Fixture("recovery-missing-stream"))
            {
                var source = BuildAssets(fixture);
                File.AppendAllText(fixture.PathFor("export/Assets/Meshes/Shape.asset"),
                    "  m_StreamData:\n    offset: 0\n    size: 25\n    path: archive:/CAB-missing/CAB-missing.resS\n");
                fixture.WriteBytes("export/Assets/Payload/CAB-unrelated.resS", new byte[] { 0, 65, 11 });
                var report = ImportInProject(fixture, source, "Assets/Missing Stream", "Prefabs/Fixture Crate.prefab", false, Scripts());
                Check.True(report.missingAssetReferences.Any(value => value.Contains("CAB-missing.resS")), "Missing explicit streaming paths must appear in the report.");
                Check.True(report.warnings.Any(value => value.Contains("Missing streaming resource")), "Missing streaming data must produce a clear warning.");
                Check.True(!Directory.GetFiles(ImportedRoot(fixture, "Missing Stream"), "CAB-unrelated.resS", SearchOption.AllDirectories).Any(), "Unrelated resources cannot substitute for missing data.");
            }
        }

        private static void BundleIsolation()
        {
            using (var fixture = new Fixture("bundle-isolation"))
            {
                var selected = fixture.WriteBytes("bundles/selected.bundle", new byte[] { 0, 22, 1, 9 });
                var sidecar = fixture.WriteBytes("bundles/selected.bundle.resS", new byte[] { 0, 83, 2, 8 });
                var dependency = fixture.WriteBytes("bundles/hash-with-no-extension", new byte[] { 0, 31, 7, 4 });
                fixture.WriteBytes("bundles/whole-game.bundle", new byte[] { 0, 61, 8, 9 });
                fixture.WriteBytes("bundles/whole-game.bundle.resS", new byte[] { 0, 77, 19 });
                var output = fixture.PathFor("selected-stage");
                Check.Equal(output, BundleStaging.Stage(new[] { selected, dependency, selected }, output, CancellationToken.None), "Staging should return the caller's chosen output folder.");
                Check.SameBytes(selected, Path.Combine(output, "selected.bundle"), "Selected bundles must be copied unchanged.");
                Check.SameBytes(sidecar, Path.Combine(output, "selected.bundle.resS"), "A selected bundle's own sidecar must be copied unchanged.");
                Check.SameBytes(dependency, Path.Combine(output, "hash-with-no-extension"), "Explicit extensionless dependencies must be copied unchanged.");
                Check.True(!File.Exists(Path.Combine(output, "whole-game.bundle")) && !File.Exists(Path.Combine(output, "whole-game.bundle.resS")), "Staging must not scan the input directory for unrelated bundles or sidecars.");
                Check.Equal(4, Directory.GetFiles(output).Length, "Repeated selections should deduplicate, with only the three selected files and manifest present.");
                Check.True(File.ReadAllText(Path.Combine(output, "selection-manifest.json")).Contains("\"copiedFiles\": 3"), "The manifest must record exactly the selected dependency set.");
            }
        }

        private static void BundleDestinationSafety()
        {
            using (var fixture = new Fixture("bundle-collisions"))
            {
                var first = fixture.WriteBytes("first/same.bundle", new byte[] { 0, 4, 1 });
                var output = fixture.PathFor("collision-stage");
                Check.Throws<FileNotFoundException>(() => BundleStaging.Stage(new[] { fixture.PathFor("missing.bundle") }, output, CancellationToken.None), "Missing inputs must fail before staging.");
                Check.Throws<ArgumentException>(() => BundleStaging.Stage(new string[0], output, CancellationToken.None), "Empty input sets must fail before staging.");
                var sentinel = fixture.Write("existing-stage/keep.txt", "keep");
                Check.Throws<IOException>(() => BundleStaging.Stage(new[] { first }, fixture.PathFor("existing-stage"), CancellationToken.None), "Existing nonempty staging output must not be overwritten.");
                Check.Equal("keep", File.ReadAllText(sentinel), "Existing staging files must remain untouched.");
            }
        }

        private static void BundleFilenameCollisions()
        {
            using (var fixture = new Fixture("bundle-filename-collisions"))
            {
                var physics = fixture.WriteBytes("physics/polymer.bundle", new byte[] { 0, 4, 1 });
                var physicsSidecar = fixture.WriteBytes("physics/polymer.bundle.resS", new byte[] { 0, 8, 5 });
                var physicsSibling = fixture.WriteBytes("physics/void.bundle", new byte[] { 0, 18, 11 });
                var surface = fixture.WriteBytes("surface/polymer.bundle", new byte[] { 0, 9, 7 });
                var surfaceSidecar = fixture.WriteBytes("surface/polymer.bundle.resS", new byte[] { 0, 27, 12 });
                var unique = fixture.WriteBytes("other/selected.bundle", new byte[] { 0, 17, 3 });
                fixture.WriteBytes("physics/unrelated.bundle", new byte[] { 0, 77, 19 });
                fixture.WriteBytes("surface/unrelated.bundle.resS", new byte[] { 0, 91, 28 });
                var selected = new[] { physics, surface, physicsSibling, unique, physics };
                var output = fixture.PathFor("collision-stage");
                BundleStaging.Stage(selected, output, CancellationToken.None);
                var polymerFiles = Directory.GetFiles(output, "polymer.bundle", SearchOption.AllDirectories);
                Check.Equal(2, polymerFiles.Length, "Different same-named bundles must both retain their original filenames.");
                Check.Equal(2, polymerFiles.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Colliding bundles must occupy separate staging subfolders.");
                var copiedPhysics = polymerFiles.Single(path => File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(physics)));
                var copiedSurface = polymerFiles.Single(path => File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(surface)));
                Check.SameBytes(physicsSidecar, copiedPhysics + ".resS", "Physics resources must stay beside their owning bundle, without swapping bytes.");
                Check.SameBytes(surfaceSidecar, copiedSurface + ".resS", "Surface resources must stay beside their owning bundle, without swapping bytes.");
                Check.SameBytes(physicsSibling, Path.Combine(Path.GetDirectoryName(copiedPhysics), "void.bundle"), "Other explicit files from a relocated source directory must stay together.");
                Check.SameBytes(unique, Path.Combine(output, "selected.bundle"), "Unrelated unique names must retain their existing flat staging paths.");
                Check.True(!Directory.GetFiles(output, "unrelated*", SearchOption.AllDirectories).Any(), "Collision handling must not scan or import unselected source siblings.");
                Check.Equal(7, Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length, "Repeated selections must still deduplicate; expect six selected payloads plus the manifest.");
                var secondOutput = fixture.PathFor("collision-stage-reversed");
                BundleStaging.Stage(selected.Reverse(), secondOutput, CancellationToken.None);
                var firstPaths = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Select(path => path.Substring(output.Length + 1)).OrderBy(path => path).ToArray();
                var secondPaths = Directory.GetFiles(secondOutput, "*", SearchOption.AllDirectories).Select(path => path.Substring(secondOutput.Length + 1)).OrderBy(path => path).ToArray();
                Check.True(firstPaths.SequenceEqual(secondPaths), "Staged paths must remain stable when the dependency enumeration order changes.");
            }
        }

        private sealed class CancelOnProgress : IProgress<string>
        {
            private readonly CancellationTokenSource cancellation;
            public CancelOnProgress(CancellationTokenSource cancellation) { this.cancellation = cancellation; }
            public void Report(string value) { cancellation.Cancel(); }
        }

        private static void BundleCancellation()
        {
            using (var fixture = new Fixture("bundle-cancel"))
            using (var cancellation = new CancellationTokenSource())
            {
                var selected = fixture.WriteBytes("inputs/selected.bundle", new byte[] { 0, 65, 13, 7 });
                var output = fixture.PathFor("canceled-stage");
                Check.Throws<OperationCanceledException>(() => BundleStaging.Stage(new[] { selected }, output, cancellation.Token,
                    new CancelOnProgress(cancellation)), "Cancellation during a staged copy must stop the operation.");
                Check.True(!Directory.Exists(output), "Canceled staging must not leave an output folder.");
                Check.True(!Directory.GetDirectories(Path.GetDirectoryName(output), "canceled-stage.staging-*").Any(), "Canceled staging must remove its generated temporary folder.");
                Check.True(File.Exists(selected), "Canceled staging must preserve the original input bundle.");
            }
        }

        private static void PalletFolderCandidates()
        {
            using (var fixture = new Fixture("pallet-folder-candidates"))
            {
                fixture.Write("Selected/Main.pallet.json", @"{
                    'root': {'ref':'main'},
                    'objects': {
                        'main': {'barcode':'Fixture.Main','title':'Main pallet','crates':[{'ref':'spawn','type':'SpawnableCrate'}]},
                        'spawn': {'title':'Selected prop','barcode':'Fixture.Selected','mainAsset':'11111111111111111111111111111111'}
                    }
                }");
                var emptyDependency = @"{
                    'root': {'ref':'dependency'},
                    'objects': {'dependency': {'barcode':'Fixture.Shared','title':'Shared resources','crates':[]}}
                }";
                fixture.Write("StandaloneWindows64/Shared/Shared.pallet.json", emptyDependency);
                fixture.Write("Android/Shared/Shared.pallet.json", emptyDependency);
                // Two metadata documents in the same physical folder must not
                // manufacture an extra candidate for the identical resource files.
                fixture.Write("StandaloneWindows64/Shared/Shared-copy.pallet.json", emptyDependency);
                fixture.Write("AudioOnly/Audio.pallet.json", @"{
                    'root': {'ref':'audio'},
                    'objects': {'audio': {'barcode':'Fixture.AudioOnly','title':'Audio dependency'}}
                }");
                fixture.WriteBytes("StandaloneWindows64/Shared/required-pc.bundle", new byte[] { 0, 44, 51 });
                fixture.WriteBytes("Android/Shared/required-quest.bundle", new byte[] { 0, 75, 18 });

                var crates = SourceCatalog.ReadPallets(fixture.Root);
                Check.Equal(1, crates.Count, "Resource-only pallets should not invent selectable crates.");
                var mappings = SourceCatalog.ReadPalletFolders(fixture.Root);
                Check.Equal(3, mappings.Count, "All pallet barcodes must remain available for dependency resolution, including pallets without crates.");
                Check.True(mappings.ContainsKey("Fixture.AudioOnly"), "A dependency pallet without a crates field must still be indexed.");
                Check.Equal(fixture.PathFor("AudioOnly"), mappings["Fixture.AudioOnly"].Single(), "Zero-crate dependency lookup must identify its real source folder.");
                var alternatives = mappings["Fixture.Shared"];
                Check.Equal(2, alternatives.Count, "Distinct PC and Android source folders must both survive, while repeated metadata in one folder is deduplicated.");
                Check.True(new HashSet<string>(alternatives, StringComparer.OrdinalIgnoreCase).SetEquals(new[]
                {
                    fixture.PathFor("StandaloneWindows64/Shared"), fixture.PathFor("Android/Shared")
                }), "Duplicate barcodes must not silently choose one platform folder.");
                var required = alternatives.Where(folder => File.Exists(Path.Combine(folder, "required-pc.bundle"))).ToList();
                Check.Equal(1, required.Count, "Preserved alternatives should allow an exact required bundle filename to resolve to one platform.");
                Check.Equal(fixture.PathFor("StandaloneWindows64/Shared"), required[0], "An Android duplicate must not hide the required PC dependency.");
                Check.True(mappings.Values.SelectMany(folders => folders).All(Path.IsPathRooted), "Dependency folder candidates must use absolute paths.");
            }
        }
    }
}
