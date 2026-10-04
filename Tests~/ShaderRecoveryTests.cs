using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Waffles.CrateRipper.Tests
{
    internal static class ShaderRecoveryTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Compatible shader aliases preserve material data and SDK script repair", CompatibleAliases));
            tests.Add(new KeyValuePair<string, Action>("Unused shader aliases do not generate replacement warnings", UnusedAlias));
        }

        private const string PrefabGuid = "11111111111111111111111111111111";
        private const string MaterialGuid = "22222222222222222222222222222222";
        private const string ShaderGuid = "33333333333333333333333333333333";
        private const string TextureGuid = "44444444444444444444444444444444";
        private const string ScriptGuid = "55555555555555555555555555555555";
        private const string TargetShaderGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string TargetScriptGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string TargetName = "SLZ/LitMAS/LitMAS Standard";

        private const string KeywordBlock = "  m_ValidKeywords:\n  - _DETAILS_ON\n  - _NO_SSR\n  m_InvalidKeywords:\n  - _NORMALS_ON\n";
        private const string PropertyBlock =
            "  m_SavedProperties:\n    serializedVersion: 3\n    m_TexEnvs:\n" +
            "    - _BaseMap:\n        m_Texture: {fileID: 2800000, guid: " + TextureGuid + ", type: 3}\n" +
            "        m_Scale: {x: 4, y: 2}\n        m_Offset: {x: 0.25, y: -0.1}\n" +
            "    - _BumpMap:\n        m_Texture: {fileID: 2800000, guid: " + TextureGuid + ", type: 3}\n" +
            "        m_Scale: {x: 1, y: 1}\n        m_Offset: {x: 0, y: 0}\n" +
            "    m_Floats:\n    - _Normals: 1\n    - _Details: 1\n    - _Emission: 1\n    - _SSROff: 1\n" +
            "    - _SSRTemporalMul: 0.7\n    - _AlphaClipThreshhold: 0.2\n    - _BakedMutiplier: 0.8\n" +
            "    m_Colors:\n    - _BaseColor: {r: 0.4, g: 0.6, b: 0.8, a: 1}\n" +
            "    - _EmissionColor: {r: 2, g: 1, b: 0.5, a: 1}\n";

        private static string Build(Fixture fixture, string shaderName)
        {
            fixture.Asset("export/Assets/Selected.prefab", PrefabGuid,
                "%YAML 1.1\n--- !u!114 &123\nMonoBehaviour:\n" +
                "  m_Script: {fileID: 11500000, guid: " + ScriptGuid + ", type: 3}\n" +
                "  m_EditorClassIdentifier: FixtureSdk::FixtureSdk.Grip\n  health: 32\n" +
                "  material: {fileID: 2100000, guid: " + MaterialGuid + ", type: 2}\n");
            fixture.Asset("export/Assets/Skin.mat", MaterialGuid,
                "%YAML 1.1\n--- !u!21 &2100000\nMaterial:\n  m_Name: Skin\n" +
                "  m_Shader: {fileID: 4800000, guid: " + ShaderGuid + ", type: 3}\n" + KeywordBlock + PropertyBlock);
            fixture.Asset("export/Assets/Legacy.shader", ShaderGuid,
                "Shader \"" + shaderName + "\" { //DummyShaderTextExporter\n SubShader { Pass { } } }\n");
            fixture.WriteBytes("export/Assets/Surface.png", Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jQXcAAAAASUVORK5CYII="));
            fixture.Write("export/Assets/Surface.png.meta", "fileFormatVersion: 2\nguid: " + TextureGuid + "\nTextureImporter:\n  sRGBTexture: 0\n");
            return fixture.PathFor("export/Assets");
        }

        private static RecoveryReport Import(Fixture fixture, string source, IDictionary<string, ShaderBinding> shaders)
        {
            var previous = Directory.GetCurrentDirectory();
            var project = fixture.PathFor("project");
            Directory.CreateDirectory(Path.Combine(project, "Assets"));
            try
            {
                Directory.SetCurrentDirectory(project);
                return RecoveryPipeline.Import(source, "Assets/Recovered", "Selected.prefab", false,
                    new Dictionary<string, ScriptBinding>
                    {
                        ["FixtureSdk.Grip"] = new ScriptBinding { FullTypeName = "FixtureSdk.Grip", Guid = TargetScriptGuid, FileId = 321987 }
                    }, shaders);
            }
            finally { Directory.SetCurrentDirectory(previous); }
        }

        private static void CompatibleAliases()
        {
            foreach (var name in new[] { "SLZ/LitMAS/LitMAS MonoSH", "Shader Graphs/BakeryURPMonoSHSpecGraphMAS" })
            using (var fixture = new Fixture("shader-alias"))
            {
                var source = Build(fixture, name);
                var report = Import(fixture, source, new Dictionary<string, ShaderBinding>
                {
                    [name] = new ShaderBinding { Name = name, ReplacementName = TargetName, Guid = TargetShaderGuid, FileId = 654321 }
                });
                var output = fixture.PathFor("project/Assets/Recovered");
                var material = File.ReadAllText(Path.Combine(output, "Skin.mat"));
                var textureMeta = File.ReadAllText(Path.Combine(output, "Surface.png.meta"));
                var guid = Regex.Match(textureMeta, @"(?m)^guid:\s*([a-f0-9]{32})").Groups[1].Value;
                Check.True(guid.Length == 32 && guid != TextureGuid, "Texture dependencies must still receive fresh GUIDs.");
                Check.True(material.Contains("m_Shader: {fileID: 654321, guid: " + TargetShaderGuid), "An alias must use the installed replacement shader's actual GUID and local file ID.");
                Check.True(material.Contains(KeywordBlock), "Shader alias recovery must preserve valid and invalid keyword lists for later editor validation.");
                Check.True(material.EndsWith(PropertyBlock.Replace(TextureGuid, guid), StringComparison.Ordinal), "All texture references, tiling, offsets, floats and colors must remain unchanged except recovered dependency GUIDs.");
                var prefab = File.ReadAllText(Path.Combine(output, "Selected.prefab"));
                Check.True(prefab.Contains("m_Script: {fileID: 321987, guid: " + TargetScriptGuid) && prefab.Contains("  health: 32"), "Shader aliases must not regress exact SDK script repair or serialized fields.");
                Check.Equal(1, report.repairedScriptReferences, "The existing script repair path must remain active.");
                Check.Equal(1, report.reusedShaders, "A compatible shader binding must be counted as reused.");
                Check.True(report.warnings.Any(warning => warning.Contains(name + " -> " + TargetName) && warning.Contains("Baked MonoSH/Bakery lighting is not reconstructed")), "The report must disclose the actual replacement and missing original baked lighting.");
                Check.SameBytes(Path.Combine(source, "Legacy.shader"), Path.Combine(output, "_RecoveredSource/Legacy.shader.txt"), "The original exported shader must remain available byte for byte as inert text.");
                Check.True(!Directory.GetFiles(output, "*.shader", SearchOption.AllDirectories).Any(), "A reused placeholder must not remain as a compiling shader.");
            }
        }

        private static void UnusedAlias()
        {
            using (var fixture = new Fixture("shader-unused-alias"))
            {
                var source = Build(fixture, TargetName);
                const string unusedName = "SLZ/LitMAS/LitMAS MonoSH";
                var report = Import(fixture, source, new Dictionary<string, ShaderBinding>
                {
                    [TargetName] = new ShaderBinding { Name = TargetName, Guid = TargetShaderGuid, FileId = 4800000 },
                    [unusedName] = new ShaderBinding { Name = unusedName, ReplacementName = TargetName, Guid = TargetShaderGuid, FileId = 4800000 }
                });
                Check.True(!report.warnings.Any(warning => warning.Contains("Shader compatibility replacement")), "Only selected, actually used aliases should be reported as replacements.");
            }
        }
    }
}
