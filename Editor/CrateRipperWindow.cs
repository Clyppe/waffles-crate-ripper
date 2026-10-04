using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Waffles.CrateRipper
{
    public sealed class CrateRipperWindow : EditorWindow
    {
        [SerializeField] private List<CrateEntry> crates = new List<CrateEntry>();
        [SerializeField] private string sourceFolder = "";
        [SerializeField] private string sourceLabel = "";
        [SerializeField] private string backend = "";
        [SerializeField] private string outputRoot = "Assets/Ripped Crates";
        [SerializeField] private string search = "";
        [SerializeField] private bool spawnablesOnly = true;
        [SerializeField] private bool reuseShaders = true;
        [SerializeField] private int selected;
        [SerializeField] private string exportedAssets = "";
        [SerializeField] private string pendingDestination = "";
        [SerializeField] private string lastOutput = "";
        [SerializeField] private bool pendingWholeMod;
        [SerializeField] private bool browsingWarehouse = true;
        private string warehouseState;
        private double nextWarehousePoll;
        private Vector2 scroll;
        private string status = "Choose a crate or load a mod ZIP.";
        private string error = "";
        private bool busy;
        private CancellationTokenSource cancellation;
        private string[] exportedPrefabs;
        private int selectedPrefab;

        [MenuItem("Tools/Waffles/Crate Ripper")]
        public static void Open() { GetWindow<CrateRipperWindow>("Crate Ripper"); }

        private static string ProjectRoot { get { return Directory.GetParent(Application.dataPath).FullName; } }
        private static string CacheRoot { get { return Path.Combine(ProjectRoot, "Library", "CrateRipper"); } }
        private static string NewJob()
        {
            var folder = Path.Combine(CacheRoot, "Jobs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private void OnEnable()
        {
            minSize = new Vector2(630, 540);
            if (string.IsNullOrEmpty(backend)) backend = EditorPrefs.GetString("Waffles.CrateRipper.Backend", "");
            if (!string.IsNullOrEmpty(exportedAssets) && Directory.Exists(exportedAssets)) RefreshPrefabChoices();
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            EditorApplication.update += PollWarehouse;
            EditorApplication.delayCall += RefreshOnOpen;
        }

        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            EditorApplication.update -= PollWarehouse;
            EditorApplication.delayCall -= RefreshOnOpen;
            Cancel();
        }

        private void RefreshOnOpen() { if (this != null && browsingWarehouse && !busy && string.IsNullOrEmpty(exportedAssets)) RefreshCrates(); }

        private void PollWarehouse()
        {
            if (!browsingWarehouse || busy || !string.IsNullOrEmpty(exportedAssets) || EditorApplication.timeSinceStartup < nextWarehousePoll) return;
            nextWarehousePoll = EditorApplication.timeSinceStartup + 1;
            var state = UnityBindings.WarehouseState();
            if (state != warehouseState) RefreshCrates();
        }

        private void RefreshCrates()
        {
            var visible = VisibleCrates();
            var oldBarcode = visible.Count > selected && selected >= 0 ? visible[selected].Barcode : null;
            crates = UnityBindings.AvailableCrates(SetStatus);
            sourceFolder = "";
            sourceLabel = "";
            browsingWarehouse = true;
            warehouseState = UnityBindings.WarehouseState();
            var current = VisibleCrates();
            selected = string.IsNullOrEmpty(oldBarcode) ? 0 : Math.Max(current.FindIndex(c => c.Barcode == oldBarcode), 0);
            ClearPending();
            SetStatus("Found " + crates.Count + " available crates from the Marrow warehouse and project.");
        }
        private void Cancel() { cancellation?.Cancel(); }
        private void SetStatus(string value) { status = value; Repaint(); }
        private IProgress<string> Progress() { return new Progress<string>(SetStatus); }

        private async void Run(Func<CancellationToken, Task> operation)
        {
            if (busy) return;
            busy = true; error = "";
            cancellation = new CancellationTokenSource();
            try { await operation(cancellation.Token); }
            catch (OperationCanceledException) { SetStatus("Cancelled. Partial imports are listed in the output folder if copying had started."); }
            catch (Exception exception) { error = exception.Message; Debug.LogException(exception); SetStatus("Could not complete the import. Check the message below."); }
            finally { busy = false; cancellation.Dispose(); cancellation = null; Repaint(); }
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Waffles Crate Ripper", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Pick a spawnable crate, or extract an entire mod. Textures, models, materials, animation, audio and referenced assets are gathered into a named folder.", MessageType.Info);
            using (new EditorGUI.DisabledScope(busy))
            {
                EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Load mod ZIP"))
                {
                    var zip = EditorUtility.OpenFilePanel("Select BONELAB mod ZIP", "", "zip");
                    if (zip.Length > 0) Run(token => LoadZip(zip, token));
                }
                if (GUILayout.Button("Load mod folder"))
                {
                    var folder = EditorUtility.OpenFolderPanel("Select mod or Mods folder", DefaultMods(), "");
                    if (folder.Length > 0) Run(token => LoadFolder(folder, token));
                }
                if (GUILayout.Button("All available crates")) RefreshCrates();
                EditorGUILayout.EndHorizontal();
                if (browsingWarehouse) EditorGUILayout.LabelField("Marrow Asset Warehouse · built-in, loaded mod and project crates", EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(sourceFolder)) EditorGUILayout.LabelField(sourceFolder, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.BeginHorizontal();
                search = EditorGUILayout.TextField("Find crate", search);
                spawnablesOnly = GUILayout.Toggle(spawnablesOnly, "Spawnables only", GUILayout.Width(135));
                EditorGUILayout.EndHorizontal();
                var visible = VisibleCrates();
                selected = Mathf.Clamp(selected, 0, Math.Max(visible.Count - 1, 0));
                if (visible.Count > 0) selected = EditorGUILayout.Popup("Crate", selected, visible.Select(c => c.ToString()).ToArray());
                else EditorGUILayout.HelpBox("No matching crates yet. The list refreshes as Marrow loads its pallets. You can also load a ZIP/folder or switch off Spawnables only for other crate types.", MessageType.None);
                if (visible.Count > 0)
                {
                    EditorGUILayout.LabelField(visible[selected].Barcode, EditorStyles.wordWrappedMiniLabel);
                    if (!string.IsNullOrEmpty(visible[selected].OriginalAssetPath)) EditorGUILayout.LabelField(visible[selected].OriginalAssetPath, EditorStyles.wordWrappedMiniLabel);
                    if (!string.IsNullOrEmpty(visible[selected].SourceFolder)) EditorGUILayout.LabelField("Pallet source: " + visible[selected].SourceFolder, EditorStyles.wordWrappedMiniLabel);
                }
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Extraction backend", EditorStyles.boldLabel);
                EditorGUILayout.BeginHorizontal();
                backend = EditorGUILayout.TextField("AssetRipper", backend);
                if (GUILayout.Button("Browse", GUILayout.Width(65)))
                {
                    var exe = EditorUtility.OpenFilePanel("Select AssetRipper executable", "", "exe");
                    if (exe.Length > 0) { backend = exe; EditorPrefs.SetString("Waffles.CrateRipper.Backend", backend); }
                }
                EditorGUILayout.EndHorizontal();
                if (!File.Exists(backend) && GUILayout.Button("Download official AssetRipper " + AssetRipperBackend.BackendVersion))
                    Run(async token => {
                        backend = await AssetRipperBackend.DownloadBackendAsync(Path.Combine(CacheRoot, "Backend"), Progress(), token);
                        EditorPrefs.SetString("Waffles.CrateRipper.Backend", backend);
                    });
                outputRoot = EditorGUILayout.TextField("Output folder", outputRoot);
                reuseShaders = EditorGUILayout.Toggle("Reconnect installed shaders", reuseShaders);
                EditorGUILayout.HelpBox("Installed SDK scripts are matched by full type name. Missing code stays in the report and recovered source folder. Compiled shader exports may need an installed shader or manual shader work.", MessageType.None);
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(visible.Count == 0))
                    if (GUILayout.Button("Rip selected crate", GUILayout.Height(32)))
                    { var chosen = visible[selected]; Run(token => Rip(chosen, false, token)); }
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(sourceFolder)))
                    if (GUILayout.Button("Rip entire mod / ZIP", GUILayout.Height(32)))
                        Run(token => Rip(null, true, token));
                EditorGUILayout.EndHorizontal();
                DrawPending();
            }
            if (busy)
            {
                EditorGUILayout.Space();
                if (GUILayout.Button("Cancel operation")) Cancel();
            }
            EditorGUILayout.LabelField(status, EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (!string.IsNullOrEmpty(lastOutput))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Show extracted folder"))
                { var asset = AssetDatabase.LoadMainAssetAtPath(lastOutput); if (asset != null) { EditorGUIUtility.PingObject(asset); Selection.activeObject = asset; } }
                if (GUILayout.Button("Open repair report"))
                { var path = Path.Combine(ProjectRoot, lastOutput, "recovery-report.json"); if (File.Exists(path)) EditorUtility.RevealInFinder(path); }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        private List<CrateEntry> VisibleCrates()
        {
            return crates.Where(c => (!spawnablesOnly || c.Kind.IndexOf("spawnable", StringComparison.OrdinalIgnoreCase) >= 0) &&
                (string.IsNullOrWhiteSpace(search) || (c.Name + " " + c.Barcode + " " + c.PalletName).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        private static string DefaultMods()
        { return Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow", "Stress Level Zero", "BONELAB", "Mods")); }

        private async Task LoadZip(string zip, CancellationToken token)
        {
            ClearPending();
            var folder = Path.Combine(NewJob(), "Source");
            var progress = Progress();
            await Task.Run(() => SourceCatalog.ExtractZip(zip, folder, token, progress), token);
            await LoadFolder(folder, token, Path.GetFileNameWithoutExtension(zip));
        }

        private async Task LoadFolder(string folder, CancellationToken token, string label = null)
        {
            ClearPending();
            browsingWarehouse = false;
            sourceFolder = Path.GetFullPath(folder);
            sourceLabel = label ?? Path.GetFileName(sourceFolder.TrimEnd(Path.DirectorySeparatorChar));
            SetStatus("Reading pallet metadata...");
            crates = await Task.Run(() => SourceCatalog.ReadPallets(sourceFolder), token);
            token.ThrowIfCancellationRequested();
            UnityBindings.ResolveCatalogPaths(crates, SetStatus);
            selected = 0;
            SetStatus("Found " + crates.Count + " crates. Pick a crate or rip the entire source.");
        }

        private string Destination(string name)
        {
            var root = outputRoot.Replace('\\', '/').TrimEnd('/');
            var fullRoot = Path.GetFullPath(Path.Combine(ProjectRoot, root));
            if (!root.StartsWith("Assets/", StringComparison.Ordinal) || !fullRoot.StartsWith(Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output must be a folder inside Assets, for example Assets/Ripped Crates.");
            var folder = root + "/" + SourceCatalog.FolderName(name);
            var unique = folder;
            int suffix = 2;
            while (Directory.Exists(Path.Combine(ProjectRoot, unique)) || File.Exists(Path.Combine(ProjectRoot, unique)) || File.Exists(Path.Combine(ProjectRoot, unique + ".meta")))
                unique = folder + " (" + suffix++ + ")";
            return unique;
        }

        private async Task Rip(CrateEntry crate, bool wholeMod, CancellationToken token)
        {
            ClearPending();
            var job = NewJob();
            var destination = Destination(wholeMod ? ModName() : crate.Name);
            var progress = Progress();
            string assets;
            string prefab = null;
            if (!wholeMod && !string.IsNullOrEmpty(crate.LocalPrefabPath))
            {
                SetStatus("Gathering project crate dependencies...");
                assets = UnityBindings.StageProjectCrate(crate, job);
                prefab = crate.LocalPrefabPath;
            }
            else
            {
                if (!File.Exists(backend)) throw new InvalidOperationException("Download or select AssetRipper first.");
                var input = sourceFolder;
                if (!wholeMod)
                {
                    SetStatus("Resolving the selected crate's bundle dependencies...");
                    var files = UnityBindings.BundleFiles(crate);
                    input = await Task.Run(() => BundleStaging.Stage(files, Path.Combine(job, "SelectedBundles"), token, progress), token);
                    SetStatus("Extracting " + files.Count + " bundles belonging to the selected crate and its dependencies...");
                }
                if (string.IsNullOrEmpty(input)) throw new InvalidOperationException("Load the mod folder or ZIP containing this crate first.");
                assets = await AssetRipperBackend.ExportAsync(backend, input, Path.Combine(job, "Export"), progress, token);
                token.ThrowIfCancellationRequested();
                if (!wholeMod) prefab = UnityBindings.MatchPrefab(assets, crate);
            }
            if (!wholeMod && string.IsNullOrEmpty(prefab))
            {
                exportedAssets = assets; pendingDestination = destination; pendingWholeMod = false;
                RefreshPrefabChoices();
                SetStatus("Export complete. The catalog did not identify one exact prefab. Choose it below to finish the crate import.");
                return;
            }
            await Import(assets, destination, prefab, wholeMod, token);
        }

        private string ModName()
        {
            var names = crates.Select(c => c.PalletName).Distinct().ToArray();
            return names.Length == 1 ? names[0] : sourceLabel;
        }

        private async Task Import(string assets, string destination, string prefab, bool wholeMod, CancellationToken token)
        {
            var scripts = UnityBindings.Scripts();
            var shaders = reuseShaders ? UnityBindings.Shaders() : null;
            SetStatus("Recovering dependencies and reconnecting installed scripts...");
            // Bind Unity objects on the editor thread; pure file work can run in the background.
            var report = await Task.Run(() => RecoveryPipeline.Import(assets, destination, prefab, wholeMod, scripts, shaders, token), token);
            lastOutput = destination;
            AssetDatabase.Refresh();
            await ValidatePrefabs(report, token);
            ClearPending();
            SetStatus("Imported into " + destination + ". Repaired " + report.repairedScriptReferences + " script references; " + report.missingScripts.Count + " unresolved script entries. See the repair report.");
        }

        private static async Task ValidatePrefabs(RecoveryReport report, CancellationToken token)
        {
            var messages = new List<string>();
            foreach (var path in Directory.GetFiles(Path.Combine(ProjectRoot, report.outputFolder), "*.prefab", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var assetPath = UnityBindings.Relative(ProjectRoot, path);
                try
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null) { messages.Add(assetPath + ": Unity could not load this prefab."); continue; }
                    int missing = prefab.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
                    if (missing > 0) messages.Add(assetPath + ": " + missing + " missing script components.");
                }
                catch (Exception ex) { messages.Add(assetPath + ": " + ex.GetBaseException().Message); }
                await Task.Yield();
            }
            File.WriteAllText(Path.Combine(ProjectRoot, report.outputFolder, "unity-prefab-check.txt"),
                report.prefabs.Count == 0 ? "No prefabs were recovered in this import. See recovery-report.json for scene and asset diagnostics.\n" :
                messages.Count == 0 ? "Every recovered prefab loaded in Unity with no missing-script components. Runtime behavior still requires testing.\n" : string.Join("\n", messages));
            AssetDatabase.Refresh();
        }

        private void ClearPending() { exportedAssets = ""; pendingDestination = ""; exportedPrefabs = null; }
        private void RefreshPrefabChoices()
        { exportedPrefabs = Directory.GetFiles(exportedAssets, "*.prefab", SearchOption.AllDirectories).Select(p => UnityBindings.Relative(exportedAssets, p)).OrderBy(p => p).ToArray(); selectedPrefab = 0; }
        private void DrawPending()
        {
            if (string.IsNullOrEmpty(exportedAssets)) return;
            EditorGUILayout.HelpBox("Choose the crate's root prefab from the export. All of its referenced assets will be included.", MessageType.Warning);
            if (exportedPrefabs == null) RefreshPrefabChoices();
            if (exportedPrefabs.Length == 0) { EditorGUILayout.HelpBox("No prefabs were recovered. The mod may contain levels only or missing bundles.", MessageType.Error); return; }
            selectedPrefab = EditorGUILayout.Popup("Root prefab", Mathf.Clamp(selectedPrefab, 0, exportedPrefabs.Length - 1), exportedPrefabs);
            if (GUILayout.Button("Import chosen prefab and dependencies"))
            {
                var assets = exportedAssets; var target = pendingDestination; var root = exportedPrefabs[selectedPrefab];
                Run(token => Import(assets, target, root, pendingWholeMod, token));
            }
        }
    }
}
