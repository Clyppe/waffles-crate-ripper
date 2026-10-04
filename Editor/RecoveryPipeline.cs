using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;

namespace Waffles.CrateRipper
{
    /// <summary>
    /// Imports an AssetRipper Assets directory without compiling recovered game code.
    /// This class deliberately has no Unity API dependency so its file transformations
    /// can be validated outside the editor.
    /// </summary>
    public static class RecoveryPipeline
    {
        private static readonly Regex GuidField = new Regex(@"\bguid\s*:\s*([0-9a-fA-F]{32})\b", RegexOptions.Compiled);
        private static readonly Regex FileIdField = new Regex(@"\bfileID\s*:\s*(-?\d+)", RegexOptions.Compiled);
        private static readonly Regex ScriptField = new Regex(@"\bm_Script\s*:\s*\{[^}]*\}", RegexOptions.Compiled);
        private static readonly Regex ShaderField = new Regex(@"\bm_Shader\s*:\s*\{[^}]*\}", RegexOptions.Compiled);
        private static readonly Regex ObjectReference = new Regex(@"\{[^{}]*\}", RegexOptions.Compiled);
        private static readonly Regex ClassIdentifier = new Regex(@"^[ \t]*m_EditorClassIdentifier[ \t]*:[ \t]*([^\r\n]*)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex DocumentStart = new Regex(@"^---\s*!u!", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex ShaderName = new Regex("\\bShader\\s+\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly HashSet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".dll", ".exe", ".asmdef", ".asmref", ".rsp", ".pdb", ".mdb", ".winmd", ".so", ".dylib", ".a"
        };
        private static readonly HashSet<string> SerializedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".asset", ".prefab", ".unity", ".mat", ".anim", ".controller", ".overridecontroller", ".playable",
            ".physicmaterial", ".physicsmaterial2d", ".terrainlayer", ".rendertexture", ".cubemap", ".mesh", ".lighting"
        };
        private static readonly HashSet<string> SidecarExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ress", ".resource", ".resources", ".streamingresource", ".bin"
        };

        private sealed class TextData
        {
            public string Value;
            public Encoding Encoding;
        }

        private sealed class SourceAsset
        {
            public string Relative;
            public string FullPath;
            public string MetaPath;
            public string Guid;
            public TextData Text;
            public TextData Meta;
            public string ScriptType;
            public string ShaderName;
            public bool IsCode;
            public bool IsBinarySerialized;
            public bool IsFolder;
        }

        public static RecoveryReport Import(string exportedAssetsFolder, string destinationAssetFolder,
            string selectedPrefabRelativePath, bool wholeMod, IDictionary<string, ScriptBinding> installedScripts,
            IDictionary<string, ShaderBinding> installedShaders = null, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(exportedAssetsFolder)) throw new ArgumentException("An exported Assets folder is required.");
            var sourceRoot = Path.GetFullPath(exportedAssetsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
            var destinationRelative = ValidateDestination(destinationAssetFolder);
            var destinationRoot = Path.GetFullPath(destinationRelative);
            if (ContainsPath(sourceRoot, destinationRoot) || ContainsPath(destinationRoot, sourceRoot))
                throw new ArgumentException("Source and destination must be separate, non-overlapping folders.");
            if (File.Exists(destinationRoot) || (Directory.Exists(destinationRoot) && Directory.EnumerateFileSystemEntries(destinationRoot).Any()))
                throw new IOException("The destination already contains files. Choose a new output folder.");
            AssertNoReparsePoints(sourceRoot, cancellation);
            AssertDestinationParentsSafe(destinationRoot);

            var report = new RecoveryReport { outputFolder = Slash(destinationRelative) };
            var assets = Inventory(sourceRoot, report, cancellation);
            var byPath = assets.ToDictionary(a => a.Relative, StringComparer.OrdinalIgnoreCase);
            var byGuid = new Dictionary<string, SourceAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets.Where(a => !string.IsNullOrEmpty(a.Guid)))
            {
                cancellation.ThrowIfCancellationRequested();
                SourceAsset existing;
                if (byGuid.TryGetValue(asset.Guid, out existing))
                    throw new InvalidDataException("Duplicate exported GUID " + asset.Guid + ": " + existing.Relative + " and " + asset.Relative);
                byGuid.Add(asset.Guid, asset);
            }

            var scriptIndex = ScriptIndex(installedScripts, report);
            var shaderIndex = ShaderIndex(installedShaders, report);
            var scriptTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets.Where(a => a.IsCode && !string.IsNullOrEmpty(a.Guid) && !string.IsNullOrEmpty(a.ScriptType)))
                scriptTypes[asset.Guid] = asset.ScriptType;
            // Component identifiers are more useful than filenames when source export
            // is absent, and allow recovery of external MonoScripts referenced by a crate.
            foreach (var asset in assets.Where(a => a.Text != null && IsYaml(a.Text.Value)))
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var block in SplitDocuments(asset.Text.Value))
                {
                    var script = ScriptField.Match(block);
                    var identifier = ClassIdentifier.Match(block);
                    if (!script.Success || !identifier.Success) continue;
                    var guid = GuidField.Match(script.Value);
                    var type = NormalizeTypeIdentifier(identifier.Groups[1].Value);
                    if (!guid.Success || type.Length == 0) continue;
                    string previous;
                    if (scriptTypes.TryGetValue(guid.Groups[1].Value, out previous) && !string.Equals(previous, type, StringComparison.Ordinal))
                        throw new InvalidDataException("Conflicting script identities for GUID " + guid.Groups[1].Value + ": " + previous + " and " + type);
                    scriptTypes[guid.Groups[1].Value] = type;
                }
            }

            var chosen = SelectAssets(assets, byPath, byGuid, selectedPrefabRelativePath, wholeMod, report, cancellation);
            var sourceFolder = ReservedFolder("_RecoveredSource", assets);
            var quarantineFolder = ReservedFolder("_QuarantinedAssets", assets);
            var freshGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var shaderBindings = new Dictionary<string, ShaderBinding>(StringComparer.OrdinalIgnoreCase);
            var scriptBindings = new Dictionary<string, ScriptBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in scriptTypes)
            {
                ScriptBinding binding;
                if (scriptIndex.TryGetValue(pair.Value, out binding)) scriptBindings[pair.Key] = binding;
            }
            foreach (var asset in chosen)
            {
                cancellation.ThrowIfCancellationRequested();
                ShaderBinding shader;
                if (!string.IsNullOrEmpty(asset.ShaderName) && !string.IsNullOrEmpty(asset.Guid) && shaderIndex.TryGetValue(asset.ShaderName, out shader))
                {
                    shaderBindings[asset.Guid] = shader;
                    report.reusedShaders++;
                    if (!string.IsNullOrEmpty(shader.ReplacementName) && !string.Equals(asset.ShaderName, shader.ReplacementName, StringComparison.Ordinal))
                    {
                        var limitation = asset.ShaderName == "SLZ/LitMAS/LitMAS MonoSH" || asset.ShaderName == "Shader Graphs/BakeryURPMonoSHSpecGraphMAS"
                            ? " Baked MonoSH/Bakery lighting is not reconstructed by this compatibility replacement."
                            : " The replacement preserves material properties but does not reconstruct the original shader implementation.";
                        AddUnique(report.warnings, "Shader compatibility replacement: " + asset.ShaderName + " -> " + shader.ReplacementName +
                            ". Exported shader source is retained as text; serialized textures and material properties are preserved." + limitation);
                    }
                }
                else if (!asset.IsCode && !asset.IsBinarySerialized && !string.IsNullOrEmpty(asset.Guid))
                    freshGuids[asset.Guid] = System.Guid.NewGuid().ToString("N");
            }

            ReportMissingAssetReferences(chosen, byGuid, scriptTypes, scriptIndex, shaderIndex, report, cancellation);
            // Library is outside Unity's import path and on the same volume as Assets,
            // so the final directory move is atomic even for projects on a second disk.
            var temporary = Path.GetFullPath(Path.Combine("Library", "WafflesCrateImport-" + System.Guid.NewGuid().ToString("N")));
            if (ContainsPath(sourceRoot, temporary)) throw new ArgumentException("The export cannot contain the project's import staging directory.");
            Directory.CreateDirectory(temporary);
            try
            {
                foreach (var asset in chosen.OrderBy(a => a.Relative, StringComparer.Ordinal))
                {
                    cancellation.ThrowIfCancellationRequested();
                    ShaderBinding reused;
                    if (asset.IsCode || (!string.IsNullOrEmpty(asset.Guid) && shaderBindings.TryGetValue(asset.Guid, out reused)))
                    {
                        var recovered = sourceFolder + "/" + asset.Relative + ".txt";
                        CopyFile(asset.FullPath, PathInside(temporary, recovered));
                        report.recoveredSourceFiles.Add(asset.Relative + " -> " + recovered);
                        if (asset.Meta != null) CopyFile(asset.MetaPath, PathInside(temporary, recovered + ".meta.txt"));
                        report.copiedFiles++;
                        continue;
                    }
                    if (asset.IsBinarySerialized)
                    {
                        var quarantined = quarantineFolder + "/" + asset.Relative + ".bin";
                        CopyFile(asset.FullPath, PathInside(temporary, quarantined));
                        if (asset.Meta != null) CopyFile(asset.MetaPath, PathInside(temporary, quarantined + ".meta.txt"));
                        report.quarantinedFiles.Add(asset.Relative + " -> " + quarantined);
                        AddUnique(report.warnings, "Binary Unity serialized asset cannot be safely remapped: " + asset.Relative + ". It is preserved in " + quarantineFolder + "; export it as Unity YAML first.");
                        report.copiedFiles++;
                        continue;
                    }

                    var target = PathInside(temporary, asset.Relative);
                    if (asset.IsFolder) Directory.CreateDirectory(target);
                    else if (asset.Text != null)
                    {
                        var value = asset.Text.Value;
                        if (IsYaml(value))
                        {
                            value = RepairScriptReferences(value, asset.Relative, scriptTypes, scriptIndex, report);
                            value = RepairShaderReferences(value, shaderBindings);
                            value = RewriteGuids(value, freshGuids, shaderBindings, scriptBindings);
                        }
                        WriteText(target, value, asset.Text.Encoding);
                        report.copiedFiles++;
                    }
                    else
                    {
                        CopyFile(asset.FullPath, target);
                        report.copiedFiles++;
                    }
                    string newGuid;
                    if (asset.Meta != null)
                    {
                        var value = RewriteGuids(asset.Meta.Value, freshGuids, shaderBindings, scriptBindings);
                        WriteText(target + ".meta", value, asset.Meta.Encoding);
                    }
                    else if (!asset.IsFolder)
                    {
                        newGuid = System.Guid.NewGuid().ToString("N");
                        WriteText(target + ".meta", "fileFormatVersion: 2\nguid: " + newGuid + "\n", new UTF8Encoding(false));
                    }
                    if (string.Equals(Path.GetExtension(asset.Relative), ".prefab", StringComparison.OrdinalIgnoreCase))
                        report.prefabs.Add(report.outputFolder + "/" + asset.Relative);
                    if (!string.IsNullOrEmpty(asset.ShaderName))
                        AddUnique(report.warnings, "Exported shader retained: " + asset.ShaderName + ". Compiled shaders do not contain their original authoring source; an AssetRipper placeholder may need replacement.");
                }

                report.missingScripts.Sort(StringComparer.Ordinal);
                report.missingAssetReferences.Sort(StringComparer.Ordinal);
                report.warnings.Sort(StringComparer.Ordinal);
                WriteText(Path.Combine(temporary, "recovery-report.json"), JsonConvert.SerializeObject(report, Formatting.Indented) + "\n", new UTF8Encoding(false));
                WriteText(Path.Combine(temporary, "RECOVERY-README.txt"), Readme(report, sourceFolder, quarantineFolder), new UTF8Encoding(false));
                // No project files are touched until the complete export is ready.
                AssertDestinationParentsSafe(destinationRoot);
                cancellation.ThrowIfCancellationRequested();
                if (Directory.Exists(destinationRoot))
                {
                    if (Directory.EnumerateFileSystemEntries(destinationRoot).Any())
                        throw new IOException("The destination changed while importing; it now contains files.");
                    Directory.Delete(destinationRoot, false);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destinationRoot));
                Directory.Move(temporary, destinationRoot);
                return report;
            }
            finally
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
        }

        private static string ValidateDestination(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
                throw new ArgumentException("The output must be a relative folder under Assets, for example Assets/Recovered/My Crate.");
            var normalized = Slash(value).TrimEnd('/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) || normalized.Split('/').Any(s => s == ".." || s == "." || s.Length == 0))
                throw new ArgumentException("The output must be a new folder inside Assets, without traversal segments.");
            var full = Path.GetFullPath(normalized);
            if (!ContainsPath(Path.GetFullPath("Assets"), full) || string.Equals(full, Path.GetFullPath("Assets"), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output must be inside the project's Assets folder.");
            return normalized;
        }

        private static List<SourceAsset> Inventory(string root, RecoveryReport report, CancellationToken cancellation)
        {
            var result = new List<SourceAsset>();
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    var owner = path.Substring(0, path.Length - 5);
                    if (File.Exists(owner) || Directory.Exists(owner)) continue;
                    result.Add(new SourceAsset { Relative = Relative(root, path), FullPath = path, IsCode = true });
                    AddUnique(report.warnings, "Orphaned metadata retained as text: " + Relative(root, path));
                    continue;
                }
                var relative = Relative(root, path);
                var extension = Path.GetExtension(path);
                var text = TryReadText(path);
                var asset = new SourceAsset
                {
                    Relative = relative, FullPath = path, Text = text, MetaPath = path + ".meta",
                    IsCode = CodeExtensions.Contains(extension),
                    IsBinarySerialized = (SerializedExtensions.Contains(extension) && (text == null || !IsYaml(text.Value))) || IsSerializedBinary(path)
                };
                ReadMeta(asset);
                if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) && text != null) asset.ScriptType = ScriptType(text.Value, Path.GetFileNameWithoutExtension(path));
                if (extension.Equals(".shader", StringComparison.OrdinalIgnoreCase) && text != null)
                {
                    var match = ShaderName.Match(text.Value);
                    if (match.Success) asset.ShaderName = match.Groups[1].Value;
                }
                result.Add(asset);
            }
            foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!File.Exists(path + ".meta")) continue;
                var folder = new SourceAsset { Relative = Relative(root, path), FullPath = path, MetaPath = path + ".meta", IsFolder = true };
                ReadMeta(folder);
                result.Add(folder);
            }
            if (result.Count == 0) throw new InvalidDataException("The exported Assets folder is empty.");
            return result;
        }

        private static void ReadMeta(SourceAsset asset)
        {
            if (!File.Exists(asset.MetaPath)) return;
            asset.Meta = TryReadText(asset.MetaPath);
            if (asset.Meta == null) throw new InvalidDataException("Asset metadata is not text: " + asset.Relative + ".meta");
            var match = GuidField.Match(asset.Meta.Value);
            if (!match.Success) throw new InvalidDataException("Asset metadata has no GUID: " + asset.Relative + ".meta");
            asset.Guid = match.Groups[1].Value.ToLowerInvariant();
        }

        private static HashSet<SourceAsset> SelectAssets(List<SourceAsset> all, Dictionary<string, SourceAsset> byPath,
            Dictionary<string, SourceAsset> byGuid, string selected, bool wholeMod, RecoveryReport report, CancellationToken cancellation)
        {
            if (wholeMod) return new HashSet<SourceAsset>(all);
            if (string.IsNullOrWhiteSpace(selected) || Path.IsPathRooted(selected)) throw new ArgumentException("Choose a prefab inside the exported Assets folder.");
            selected = Slash(selected).TrimStart('/');
            if (selected.Split('/').Any(p => p == ".." || p == "." || p.Length == 0)) throw new ArgumentException("The selected prefab path contains invalid traversal segments.");
            SourceAsset first;
            if (!byPath.TryGetValue(selected, out first) || first.IsFolder) throw new FileNotFoundException("The selected prefab does not exist in the export.", selected);
            var chosen = new HashSet<SourceAsset>();
            var queue = new Queue<SourceAsset>();
            queue.Enqueue(first);
            while (queue.Count != 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var asset = queue.Dequeue();
                if (!chosen.Add(asset)) continue;
                foreach (var value in new[] { asset.Text == null || !IsYaml(asset.Text.Value) ? null : asset.Text.Value, asset.Meta == null ? null : asset.Meta.Value })
                {
                    if (value == null) continue;
                    foreach (Match match in GuidField.Matches(value))
                    {
                        SourceAsset dependency;
                        if (byGuid.TryGetValue(match.Groups[1].Value, out dependency)) queue.Enqueue(dependency);
                    }
                }
                AddRelativeSidecars(asset, byPath, queue, report);
            }
            // Keep importer folder settings along the selected dependency paths.
            foreach (var asset in chosen.ToList())
            {
                cancellation.ThrowIfCancellationRequested();
                var parent = Slash(Path.GetDirectoryName(asset.Relative) ?? "");
                while (parent.Length != 0)
                {
                    SourceAsset folder;
                    if (byPath.TryGetValue(parent, out folder) && folder.IsFolder) chosen.Add(folder);
                    parent = Slash(Path.GetDirectoryName(parent) ?? "");
                }
            }
            return chosen;
        }

        private static void AddRelativeSidecars(SourceAsset asset, Dictionary<string, SourceAsset> byPath, Queue<SourceAsset> queue,
            RecoveryReport report)
        {
            if (asset.IsFolder) return;
            // A binary payload is a dependency of its owning asset, not of every
            // crate in an export. Prefix matching is limited to the exact sibling
            // name (Albedo.png.resS), never an entire directory or extension.
            foreach (var extension in SidecarExtensions)
            {
                SourceAsset companion;
                if (byPath.TryGetValue(asset.Relative + extension, out companion)) queue.Enqueue(companion);
            }
            // glTF buffers/textures and shader include files are paths rather than GUIDs.
            // These grammars must not be applied to arbitrary TextAsset content.
            var assetExtension = Path.GetExtension(asset.Relative);
            var expressions = new List<string>();
            if (assetExtension.Equals(".gltf", StringComparison.OrdinalIgnoreCase)) expressions.Add(@"""uri""\s*:\s*""([^""]+)""");
            if (new[] { ".shader", ".hlsl", ".cginc", ".compute", ".glsl" }.Contains(assetExtension, StringComparer.OrdinalIgnoreCase))
                expressions.Add(@"#\s*include(?:_with_pragmas)?\s*""([^""]+)""");
            foreach (var expression in expressions)
            {
                if (asset.Text == null) continue;
                foreach (Match match in Regex.Matches(asset.Text.Value, expression))
                {
                    SourceAsset dependency;
                    if (TryRelativeDependency(asset.Relative, match.Groups[1].Value, byPath, out dependency)) queue.Enqueue(dependency);
                }
            }
            // Texture, mesh and audio StreamingInfo fields may use a plain path
            // rather than a GUID, including archive:/CAB-name/payload.resS.
            foreach (var contents in new[] { asset.Text == null || !IsYaml(asset.Text.Value) ? null : asset.Text.Value,
                asset.Meta == null ? null : asset.Meta.Value })
            {
                if (contents == null) continue;
                foreach (var linked in StreamingPaths(contents))
                {
                    if (linked.Length == 0 || !SidecarExtensions.Contains(Path.GetExtension(linked))) continue;
                    SourceAsset dependency;
                    if (TryStreamingDependency(asset.Relative, linked, byPath, out dependency)) queue.Enqueue(dependency);
                    else
                    {
                        AddUnique(report.missingAssetReferences, "Streaming resource " + linked + " — " + asset.Relative);
                        AddUnique(report.warnings, "Missing streaming resource " + linked + " referenced by " + asset.Relative + ". Unrelated export payloads were not substituted.");
                    }
                }
            }
        }

        private static IEnumerable<string> StreamingPaths(string contents)
        {
            var pathExpression = @"\b(?:path|m_Path|m_Source|source|m_ResourcePath)[ \t]*:[ \t]*(""(?:[^""\\]|\\.)*""|'(?:[^']|'')*'|[^,}\r\n]*)";
            var lines = contents.Replace("\r\n", "\n").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var header = Regex.Match(lines[index], @"^([ \t]*)(?:m_StreamData|m_StreamingInfo|_streamingInfo|streamingInfo|StreamingInfo|m_Resource|m_StreamingResource|m_StreamingData)[ \t]*:[ \t]*(.*)$");
                if (!header.Success) continue;
                var body = new StringBuilder(header.Groups[2].Value);
                if (header.Groups[2].Value.Trim().Length == 0)
                {
                    var indent = header.Groups[1].Value.Length;
                    for (var next = index + 1; next < lines.Length; next++)
                    {
                        if (lines[next].Trim().Length == 0) continue;
                        var leading = lines[next].TakeWhile(c => c == ' ' || c == '\t').Count();
                        if (leading <= indent) break;
                        body.Append('\n').Append(lines[next]);
                    }
                }
                foreach (Match path in Regex.Matches(body.ToString(), pathExpression))
                    yield return path.Groups[1].Value.Trim().Trim('"', '\'');
            }
            // Legacy AudioClip YAML can put the resource source directly on the
            // component instead of nesting it under m_Resource.
            foreach (var block in SplitDocuments(contents))
            {
                if (!Regex.IsMatch(block, @"(?m)^AudioClip:[ \t]*$")) continue;
                foreach (Match source in Regex.Matches(block, @"(?m)^[ \t]+m_Source[ \t]*:[ \t]*([^\r\n]*)"))
                    yield return source.Groups[1].Value.Trim().Trim('"', '\'');
            }
        }

        private static bool TryRelativeDependency(string owner, string linked, Dictionary<string, SourceAsset> byPath,
            out SourceAsset dependency)
        {
            dependency = null;
            if (linked.IndexOf(':') >= 0 || Slash(linked).StartsWith("/", StringComparison.Ordinal)) return false;
            var segments = (Slash(Path.GetDirectoryName(owner) ?? "") + "/" + Slash(linked)).Split('/');
            var stack = new List<string>();
            foreach (var segment in segments)
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..") { if (stack.Count == 0) return false; stack.RemoveAt(stack.Count - 1); }
                else stack.Add(segment);
            }
            return byPath.TryGetValue(string.Join("/", stack), out dependency) && !dependency.IsFolder;
        }

        private static bool TryStreamingDependency(string owner, string linked, Dictionary<string, SourceAsset> byPath,
            out SourceAsset dependency)
        {
            if (TryRelativeDependency(owner, linked, byPath, out dependency)) return true;
            var normalized = Slash(linked);
            if (normalized.StartsWith("archive:/", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring("archive:/".Length).TrimStart('/');
            else if (normalized.StartsWith("Assets/", StringComparison.Ordinal)) normalized = normalized.Substring("Assets/".Length);
            else if (normalized.IndexOf(':') >= 0 || normalized.StartsWith("/", StringComparison.Ordinal)) return false;
            if (normalized.Split('/').Any(segment => segment == ".." || segment == "." || segment.Length == 0)) return false;
            if (byPath.TryGetValue(normalized, out dependency) && !dependency.IsFolder) return true;
            // Exporters sometimes flatten archive virtual paths. Use the basename
            // only if it identifies one file; never select every possible match.
            var fileName = Path.GetFileName(normalized);
            var matches = byPath.Values.Where(a => !a.IsFolder && string.Equals(Path.GetFileName(a.Relative), fileName, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            dependency = matches.Count == 1 ? matches[0] : null;
            return dependency != null;
        }

        private static Dictionary<string, ScriptBinding> ScriptIndex(IDictionary<string, ScriptBinding> values, RecoveryReport report)
        {
            var result = new Dictionary<string, ScriptBinding>(StringComparer.Ordinal);
            if (values == null) return result;
            foreach (var pair in values)
            {
                var binding = pair.Value;
                if (binding == null) continue;
                var name = string.IsNullOrWhiteSpace(binding.FullTypeName) ? pair.Key : binding.FullTypeName;
                if (string.IsNullOrWhiteSpace(name) || !ValidGuid(binding.Guid) || binding.FileId == 0)
                { AddUnique(report.warnings, "Ignored invalid installed script binding: " + pair.Key); continue; }
                ScriptBinding previous;
                if (result.TryGetValue(name, out previous) && (!string.Equals(previous.Guid, binding.Guid, StringComparison.OrdinalIgnoreCase) || previous.FileId != binding.FileId))
                    throw new InvalidDataException("Multiple installed scripts have the same full type name: " + name);
                result[name] = binding;
            }
            return result;
        }

        private static Dictionary<string, ShaderBinding> ShaderIndex(IDictionary<string, ShaderBinding> values, RecoveryReport report)
        {
            var result = new Dictionary<string, ShaderBinding>(StringComparer.Ordinal);
            if (values == null) return result;
            foreach (var pair in values)
            {
                var binding = pair.Value;
                if (binding == null) continue;
                var name = string.IsNullOrEmpty(binding.Name) ? pair.Key : binding.Name;
                if (string.IsNullOrEmpty(name) || !ValidGuid(binding.Guid) || binding.FileId == 0)
                { AddUnique(report.warnings, "Ignored invalid installed shader binding: " + pair.Key); continue; }
                ShaderBinding previous;
                if (result.TryGetValue(name, out previous) && (!string.Equals(previous.Guid, binding.Guid, StringComparison.OrdinalIgnoreCase) || previous.FileId != binding.FileId))
                    throw new InvalidDataException("Multiple installed shaders have the same name: " + name);
                result[name] = binding;
            }
            return result;
        }

        private static void ReportMissingAssetReferences(HashSet<SourceAsset> chosen, Dictionary<string, SourceAsset> byGuid,
            Dictionary<string, string> scriptTypes, Dictionary<string, ScriptBinding> scripts, Dictionary<string, ShaderBinding> shaders,
            RecoveryReport report, CancellationToken cancellation)
        {
            var installed = new HashSet<string>(scripts.Values.Select(v => v.Guid).Concat(shaders.Values.Select(v => v.Guid)), StringComparer.OrdinalIgnoreCase);
            foreach (var asset in chosen)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var value in new[] { asset.Text == null || !IsYaml(asset.Text.Value) ? null : asset.Text.Value, asset.Meta == null ? null : asset.Meta.Value })
                {
                    if (value == null) continue;
                    var scriptGuids = new HashSet<string>(ScriptField.Matches(value).Cast<Match>()
                        .Select(m => GuidField.Match(m.Value)).Where(m => m.Success).Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
                    foreach (Match match in GuidField.Matches(value))
                    {
                        var guid = match.Groups[1].Value;
                        // Unity reserves zero-prefixed built-in resource GUIDs. They
                        // intentionally refer to the editor/player's builtin assets.
                        if (guid.StartsWith("0000000000000000", StringComparison.Ordinal) || byGuid.ContainsKey(guid) || installed.Contains(guid) || scriptGuids.Contains(guid) || scriptTypes.ContainsKey(guid)) continue;
                        AddUnique(report.missingAssetReferences, guid.ToLowerInvariant() + " — " + asset.Relative);
                    }
                }
            }
            if (report.missingAssetReferences.Count != 0)
                AddUnique(report.warnings, report.missingAssetReferences.Count + " external asset references are not present in this export or the supplied installed SDK bindings. See missingAssetReferences in recovery-report.json.");
        }

        private static string RepairScriptReferences(string text, string relative, Dictionary<string, string> types,
            Dictionary<string, ScriptBinding> installed, RecoveryReport report)
        {
            return ScriptField.Replace(text, match =>
            {
                var guid = GuidField.Match(match.Value);
                var fileId = FileIdField.Match(match.Value);
                if (!guid.Success || !fileId.Success || fileId.Groups[1].Value == "0") return match.Value;
                var originalGuid = guid.Groups[1].Value;
                string name;
                ScriptBinding binding;
                if (types.TryGetValue(originalGuid, out name) && installed.TryGetValue(name, out binding))
                {
                    report.repairedScriptReferences++;
                    return RewriteObjectReference(match.Value, binding.Guid, binding.FileId);
                }
                // An already valid installed reference needs no repair, even when
                // the export has no C# source or editor class identifier.
                if (installed.Values.Any(v => string.Equals(v.Guid, originalGuid, StringComparison.OrdinalIgnoreCase) && v.FileId.ToString(CultureInfo.InvariantCulture) == fileId.Groups[1].Value)) return match.Value;
                AddUnique(report.missingScripts, (name ?? "Unknown MonoScript (GUID " + originalGuid + ")") + " — " + relative);
                return match.Value;
            });
        }

        private static string RepairShaderReferences(string text, Dictionary<string, ShaderBinding> bindings)
        {
            return ShaderField.Replace(text, match =>
            {
                var guid = GuidField.Match(match.Value);
                ShaderBinding binding;
                return guid.Success && bindings.TryGetValue(guid.Groups[1].Value, out binding)
                    ? RewriteObjectReference(match.Value, binding.Guid, binding.FileId) : match.Value;
            });
        }

        private static string RewriteObjectReference(string value, string guid, long fileId)
        {
            value = GuidField.Replace(value, match => match.Value.Substring(0, match.Groups[1].Index - match.Index) + guid.ToLowerInvariant());
            return FileIdField.Replace(value, match => match.Value.Substring(0, match.Groups[1].Index - match.Index) + fileId.ToString(CultureInfo.InvariantCulture));
        }

        private static string RewriteGuids(string value, Dictionary<string, string> remap, Dictionary<string, ShaderBinding> shaders,
            Dictionary<string, ScriptBinding> scripts)
        {
            // References outside m_Script/m_Shader can also point directly at
            // MonoScript or Shader assets (for example importer external objects).
            value = ObjectReference.Replace(value, match =>
            {
                var guid = GuidField.Match(match.Value);
                if (!guid.Success || !FileIdField.IsMatch(match.Value)) return match.Value;
                ScriptBinding script;
                ShaderBinding shader;
                if (scripts.TryGetValue(guid.Groups[1].Value, out script)) return RewriteObjectReference(match.Value, script.Guid, script.FileId);
                if (shaders.TryGetValue(guid.Groups[1].Value, out shader)) return RewriteObjectReference(match.Value, shader.Guid, shader.FileId);
                return match.Value;
            });
            return GuidField.Replace(value, match =>
            {
                var old = match.Groups[1].Value;
                string replacement;
                ShaderBinding shader;
                if (!remap.TryGetValue(old, out replacement))
                {
                    if (!shaders.TryGetValue(old, out shader)) return match.Value;
                    replacement = shader.Guid;
                }
                return match.Value.Substring(0, match.Groups[1].Index - match.Index) + replacement.ToLowerInvariant();
            });
        }

        private static string ScriptType(string code, string fileName)
        {
            code = StripCommentsAndStrings(code);
            var namespaces = Regex.Matches(code, @"\bnamespace\s+([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*[;{]");
            var classes = Regex.Matches(code, @"\bclass\s+([A-Za-z_]\w*)").Cast<Match>().ToList();
            var matching = classes.Where(m => string.Equals(m.Groups[1].Value, fileName, StringComparison.Ordinal)).ToList();
            if (matching.Count == 0) return null;
            // Unity's MonoScript type is the class matching its filename. Refuse an
            // ambiguous namespace rather than reconnecting a similarly named class.
            var namespaceNames = namespaces.Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
            if (namespaceNames.Count > 1) return null;
            return namespaceNames.Count == 0 ? fileName : namespaceNames[0] + "." + fileName;
        }

        private static string StripCommentsAndStrings(string source)
        {
            var output = source.ToCharArray();
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\r' && source[i] != '\n') output[i++] = ' ';
                    i--;
                }
                else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    output[i++] = ' '; output[i] = ' ';
                    while (++i < source.Length)
                    {
                        if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/') { output[i++] = ' '; output[i] = ' '; break; }
                        if (source[i] != '\r' && source[i] != '\n') output[i] = ' ';
                    }
                }
                else if (source[i] == '"' || source[i] == '\'')
                {
                    var quote = source[i]; var verbatim = quote == '"' && i > 0 && source[i - 1] == '@'; output[i] = ' ';
                    while (++i < source.Length)
                    {
                        if (source[i] != '\r' && source[i] != '\n') output[i] = ' ';
                        if (!verbatim && source[i] == '\\' && i + 1 < source.Length) { output[++i] = ' '; continue; }
                        if (source[i] != quote) continue;
                        if (verbatim && i + 1 < source.Length && source[i + 1] == quote) { output[++i] = ' '; continue; }
                        break;
                    }
                }
            }
            return new string(output);
        }

        private static string NormalizeTypeIdentifier(string value)
        {
            value = value.Trim().Trim('"', '\'');
            var separator = value.IndexOf("::", StringComparison.Ordinal);
            if (separator >= 0) value = value.Substring(separator + 2);
            var comma = value.IndexOf(',');
            if (comma >= 0) value = value.Substring(0, comma);
            return value.Trim();
        }

        private static IEnumerable<string> SplitDocuments(string value)
        {
            var starts = DocumentStart.Matches(value).Cast<Match>().Select(m => m.Index).ToList();
            if (starts.Count == 0) { yield return value; yield break; }
            for (var i = 0; i < starts.Count; i++) yield return value.Substring(starts[i], (i + 1 < starts.Count ? starts[i + 1] : value.Length) - starts[i]);
        }

        private static TextData TryReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            Encoding encoding;
            var offset = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) { encoding = new UTF8Encoding(true, true); offset = 3; }
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
            else
            {
                if (bytes.Any(b => b == 0 || (b < 9) || (b > 13 && b < 32))) return null;
                encoding = new UTF8Encoding(false, true);
            }
            try { return new TextData { Value = encoding.GetString(bytes, offset, bytes.Length - offset), Encoding = encoding }; }
            catch (DecoderFallbackException) { return null; }
        }

        private static bool IsSerializedBinary(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var header = new byte[48]; var count = stream.Read(header, 0, header.Length);
                if (count >= 8)
                {
                    var magic = Encoding.ASCII.GetString(header, 0, Math.Min(count, 8));
                    if (magic.StartsWith("UnityFS", StringComparison.Ordinal) || magic.StartsWith("UnityWeb", StringComparison.Ordinal) || magic.StartsWith("UnityRaw", StringComparison.Ordinal)) return true;
                }
                if (count < 20) return false;
                var metadata = BigEndian32(header, 0); var size = BigEndian32(header, 4); var version = BigEndian32(header, 8); var offset = BigEndian32(header, 12);
                if (version < 5 || version > 100 || header[16] > 1) return false;
                if (version >= 22 && count >= 48)
                {
                    metadata = BigEndian32(header, 20);
                    var largeSize = BigEndian64(header, 24); var largeOffset = BigEndian64(header, 32);
                    return metadata > 0 && largeSize == stream.Length && largeOffset >= 48 && largeOffset < largeSize;
                }
                return metadata > 0 && size == stream.Length && offset >= 20 && offset < size;
            }
        }

        private static long BigEndian32(byte[] bytes, int index) { return ((long)bytes[index] << 24) | ((long)bytes[index + 1] << 16) | ((long)bytes[index + 2] << 8) | bytes[index + 3]; }
        private static long BigEndian64(byte[] bytes, int index) { return (BigEndian32(bytes, index) << 32) | BigEndian32(bytes, index + 4); }
        private static bool IsYaml(string text) { return text.TrimStart('\ufeff', ' ', '\t', '\r', '\n').StartsWith("%YAML", StringComparison.Ordinal) || DocumentStart.IsMatch(text); }
        private static bool ValidGuid(string value) { return value != null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{32}\z") && value.Any(c => c != '0'); }
        private static void AddUnique(List<string> values, string value) { if (!values.Contains(value)) values.Add(value); }
        private static string Slash(string value) { return value.Replace('\\', '/'); }
        private static string Relative(string root, string path) { return Slash(path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        private static bool ContainsPath(string parent, string child) { return string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        private static string PathInside(string root, string relative) { var path = Path.GetFullPath(Path.Combine(root, relative)); if (!ContainsPath(root, path)) throw new IOException("Unsafe output path: " + relative); return path; }
        private static string ReservedFolder(string name, List<SourceAsset> assets) { var result = name; var number = 2; while (assets.Any(a => string.Equals(a.Relative.Split('/')[0], result, StringComparison.OrdinalIgnoreCase))) result = name + number++; return result; }
        private static void WriteText(string target, string value, Encoding encoding) { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.WriteAllText(target, value, encoding); }
        private static void CopyFile(string source, string target) { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target, false); }

        private static void AssertNoReparsePoints(string root, CancellationToken cancellation)
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links are not accepted in the export.");
            var queue = new Queue<string>(); queue.Enqueue(root);
            while (queue.Count != 0)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(queue.Dequeue()))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links are not accepted in the export: " + entry);
                    if ((attributes & FileAttributes.Directory) != 0) queue.Enqueue(entry);
                }
            }
        }

        private static void AssertDestinationParentsSafe(string destination)
        {
            var current = destination;
            while (!string.IsNullOrEmpty(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The output cannot pass through a symbolic link: " + current);
                current = Path.GetDirectoryName(current);
            }
        }

        private static string Readme(RecoveryReport report, string sourceFolder, string quarantineFolder)
        {
            return "Waffles Crate Ripper recovery\n\n" +
                "Output: " + report.outputFolder + "\n" +
                "Recovered files: " + report.copiedFiles + "\n" +
                "Reconnected script references: " + report.repairedScriptReferences + "\n" +
                "Reused installed shaders: " + report.reusedShaders + "\n\n" +
                "See recovery-report.json for the exact files and unresolved components. Imported asset GUIDs are new; installed SDK script and shader GUIDs remain their real project GUIDs. Prefab fields and object file IDs are preserved.\n\n" +
                "Recovered C#, managed assemblies, native libraries and assembly definitions are stored as .txt files in " + sourceFolder + ". They are reference material, not working implementations. Install the matching SDK or original dependency to resolve missing components. No replacement behaviour is invented.\n\n" +
                "Binary serialized Unity assets and bundles are preserved in " + quarantineFolder + " rather than imported with corrupt GUID references. Export them as Unity YAML with AssetRipper before retrying. Image, mesh interchange, audio and binary resource payloads remain in their original export formats.\n\n" +
                "A compiled shader cannot supply its original source. Exact-name installed shaders are reused; other exported shader files may be placeholders that require the original source.\n";
        }
    }
}
