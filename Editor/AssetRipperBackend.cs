using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Waffles.CrateRipper
{
    /// <summary>Runs a separate, owned AssetRipper server. All exports happen in fresh staging folders.</summary>
    public static class AssetRipperBackend
    {
        public const string BackendVersion = "2.0.0";
        public const string DownloadUrl = "https://github.com/AssetRipper/AssetRipper/releases/download/2.0.0/AssetRipper_win_x64.zip";
        public const string DownloadSha256 = "9a7ef0e7c5c3ea5b90b4e6d855e2d98d5f7ec8c3f9e26fccbc194c6a7b01baf7";

        public static async Task<string> ExportAsync(string executablePath, string inputFolder,
            string freshExportFolder, IProgress<string> progress, CancellationToken cancellation)
        {
            executablePath = Path.GetFullPath(executablePath ?? throw new ArgumentNullException(nameof(executablePath)));
            inputFolder = Path.GetFullPath(inputFolder ?? throw new ArgumentNullException(nameof(inputFolder)));
            freshExportFolder = Path.GetFullPath(freshExportFolder ?? throw new ArgumentNullException(nameof(freshExportFolder)));
            if (!File.Exists(executablePath)) throw new FileNotFoundException("Select AssetRipper.GUI.Free.exe first.", executablePath);
            if (!Directory.Exists(inputFolder)) throw new DirectoryNotFoundException("The extracted mod folder does not exist: " + inputFolder);
            ValidateFreshExportFolder(freshExportFolder, inputFolder);
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(freshExportFolder);

            string logPath = freshExportFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".assetripper.log";
            int port = FindUnusedPort();
            var lastOutput = new Queue<string>();
            object outputLock = new object();
            var listening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string address = "http://127.0.0.1:" + port;
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--headless --port " + port + " --log-path " + QuoteArgument(logPath),
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var ownedProcess = new Process { StartInfo = startInfo })
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(90) })
            {
                DataReceivedEventHandler onOutput = (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    lock (outputLock)
                    {
                        lastOutput.Enqueue(e.Data);
                        while (lastOutput.Count > 24) lastOutput.Dequeue();
                    }
                    if (e.Data.IndexOf("Now listening on: " + address, StringComparison.OrdinalIgnoreCase) >= 0)
                        listening.TrySetResult(true);
                    if (e.Data.IndexOf("ExportProgress", StringComparison.OrdinalIgnoreCase) >= 0)
                        progress?.Report(e.Data);
                };
                ownedProcess.OutputDataReceived += onOutput;
                ownedProcess.ErrorDataReceived += onOutput;
                try
                {
                    progress?.Report("Starting AssetRipper " + BackendVersion + " in the background...");
                    if (!ownedProcess.Start()) throw new IOException("AssetRipper could not be started.");
                    ownedProcess.BeginOutputReadLine();
                    ownedProcess.BeginErrorReadLine();
                    using (cancellation.Register(() => StopOwnedProcess(ownedProcess)))
                    {
                        await WaitForStartupAsync(ownedProcess, client, address, listening.Task, cancellation).ConfigureAwait(false);
                        progress?.Report("Preparing prefab paths and script metadata...");
                        await PostAsync(client, address + "/Settings/Update", new[]
                        {
                            new KeyValuePair<string, string>("BundledAssetsExportMode", "DirectExport"),
                            new KeyValuePair<string, string>("ScriptExportMode", "Decompiled")
                        }, cancellation).ConfigureAwait(false);
                        progress?.Report("Reading mod bundles and their dependencies...");
                        await PostAsync(client, address + "/LoadFolder", new[]
                        {
                            new KeyValuePair<string, string>("Path", inputFolder)
                        }, cancellation).ConfigureAwait(false);
                        if (ownedProcess.HasExited) throw new IOException("AssetRipper exited while loading the bundles.");
                        progress?.Report("Exporting recoverable assets to staging...");
                        await PostAsync(client, address + "/Export/UnityProject", new[]
                        {
                            new KeyValuePair<string, string>("Path", freshExportFolder),
                            new KeyValuePair<string, string>("CreateSubfolder", "false")
                        }, cancellation).ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        string exportedAssets = FindExportedAssets(freshExportFolder);
                        if (exportedAssets == null || !Directory.EnumerateFileSystemEntries(exportedAssets).Any())
                            throw new IOException("AssetRipper returned without exporting any assets. Check the log: " + logPath);
                        progress?.Report("Asset export complete. Checking dependencies and script references...");
                        return exportedAssets;
                    }
                }
                catch (Exception exception)
                {
                    if (cancellation.IsCancellationRequested) throw new OperationCanceledException("Asset extraction was canceled.", exception, cancellation);
                    string tail;
                    lock (outputLock) tail = string.Join(Environment.NewLine, lastOutput);
                    string reason = exception is OperationCanceledException
                        ? "AssetRipper exceeded the HTTP operation timeout."
                        : "AssetRipper extraction failed.";
                    throw new IOException(reason + " Log: " + logPath +
                        (tail.Length == 0 ? string.Empty : Environment.NewLine + tail), exception);
                }
                finally
                {
                    StopOwnedProcess(ownedProcess);
                    // Cancellation callbacks only request termination. Wait off the Unity UI thread
                    // before the caller imports files or removes this job's staging directory.
                    await Task.Run(() =>
                    {
                        try { ownedProcess.WaitForExit(5000); }
                        catch (InvalidOperationException) { }
                    }).ConfigureAwait(false);
                    ownedProcess.OutputDataReceived -= onOutput;
                    ownedProcess.ErrorDataReceived -= onOutput;
                }
            }
        }

        /// <summary>Downloads the official, pinned Windows x64 release and verifies its published SHA-256.</summary>
        public static async Task<string> DownloadBackendAsync(string installFolder,
            IProgress<string> progress, CancellationToken cancellation)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitOperatingSystem)
                throw new PlatformNotSupportedException("Automatic installation supports Windows x64. Select a compatible AssetRipper executable manually on other systems.");
            installFolder = Path.GetFullPath(installFolder ?? throw new ArgumentNullException(nameof(installFolder)));
            string versionFolder = Path.Combine(installFolder, BackendVersion);
            string executable = Path.Combine(versionFolder, "AssetRipper.GUI.Free.exe");
            string verifiedMarker = Path.Combine(versionFolder, ".verified-release");
            if (File.Exists(executable) && File.Exists(verifiedMarker) && File.ReadAllText(verifiedMarker).Trim() == DownloadSha256)
                return executable;
            if (Directory.Exists(versionFolder) && Directory.EnumerateFileSystemEntries(versionFolder).Any())
                throw new IOException("The backend install folder already contains unverified files. Select a different install folder: " + versionFolder);
            Directory.CreateDirectory(installFolder);
            string temporaryArchive = Path.Combine(installFolder, "assetripper-" + Guid.NewGuid().ToString("N") + ".zip");
            string temporaryExtract = Path.Combine(installFolder, ".install-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                using (var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    long? total = response.Content.Headers.ContentLength;
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var destination = new FileStream(temporaryArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long completed = 0;
                        long lastReported = -1;
                        int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
                        {
                            await destination.WriteAsync(buffer, 0, read, cancellation).ConfigureAwait(false);
                            completed += read;
                            long megabytes = completed / (1024 * 1024);
                            if (megabytes != lastReported)
                            {
                                progress?.Report("Downloading AssetRipper " + BackendVersion + ": " + megabytes + " MB" +
                                    (total.HasValue ? " / " + total.Value / (1024 * 1024) + " MB" : string.Empty));
                                lastReported = megabytes;
                            }
                        }
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                string actualHash;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(temporaryArchive))
                    actualHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(actualHash, DownloadSha256, StringComparison.Ordinal))
                    throw new IOException("The AssetRipper download failed SHA-256 verification. The executable was not installed.");
                progress?.Report("Verified official release. Installing AssetRipper...");
                Directory.CreateDirectory(temporaryExtract);
                using (var archive = ZipFile.OpenRead(temporaryArchive))
                {
                    if (archive.Entries.Count > 1000 || archive.Entries.Sum(entry => entry.Length) > 2L * 1024 * 1024 * 1024)
                        throw new IOException("The backend archive exceeds the supported installation limits.");
                    string extractionRoot = temporaryExtract.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    foreach (var entry in archive.Entries)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string destination = Path.GetFullPath(Path.Combine(temporaryExtract, entry.FullName));
                        if (!destination.StartsWith(extractionRoot, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("The backend archive contains an invalid path.");
                        if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        entry.ExtractToFile(destination);
                    }
                }
                if (!File.Exists(Path.Combine(temporaryExtract, "AssetRipper.GUI.Free.exe")))
                    throw new IOException("The official backend archive did not contain AssetRipper.GUI.Free.exe.");
                File.WriteAllText(Path.Combine(temporaryExtract, ".verified-release"), DownloadSha256, Encoding.UTF8);
                if (Directory.Exists(versionFolder)) Directory.Delete(versionFolder, false);
                Directory.Move(temporaryExtract, versionFolder);
                progress?.Report("AssetRipper installed and verified.");
                return executable;
            }
            finally
            {
                if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
                if (Directory.Exists(temporaryExtract)) Directory.Delete(temporaryExtract, true);
            }
        }

        private static void ValidateFreshExportFolder(string folder, string inputFolder)
        {
            string normalized = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalized.Length == 0 || string.Equals(normalized, Path.GetPathRoot(folder)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The export directory must be a fresh staging subfolder.");
            string leaf = Path.GetFileName(normalized);
            if (new[] { "Assets", "Packages", "Desktop", "Documents", "Downloads", "Library" }.Any(x => string.Equals(x, leaf, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The export directory must be a fresh staging subfolder, not a project or system directory.");
            string source = inputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized, source, StringComparison.OrdinalIgnoreCase) || source.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The export directory cannot contain the source mod.");
            if (File.Exists(folder)) throw new IOException("The export path is an existing file.");
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                throw new IOException("The export directory must be empty. AssetRipper would erase its existing contents: " + folder);
        }

        private static int FindUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private static async Task WaitForStartupAsync(Process process, HttpClient client, string address,
            Task listening, CancellationToken cancellation)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(60))
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("AssetRipper exited before its local server started (exit code " + process.ExitCode + ").");
                if (listening.IsCompleted)
                {
                    using (var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        startupCancellation.CancelAfter(TimeSpan.FromSeconds(2));
                        try
                        {
                            using (var response = await client.GetAsync(address + "/", startupCancellation.Token).ConfigureAwait(false))
                            {
                                if (response.IsSuccessStatusCode && !process.HasExited) return;
                            }
                        }
                        catch (HttpRequestException) { }
                        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                    }
                }
                await Task.Delay(200, cancellation).ConfigureAwait(false);
            }
            throw new TimeoutException("AssetRipper did not start its local server within 60 seconds.");
        }

        private static async Task PostAsync(HttpClient client, string url,
            IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellation)
        {
            using (var content = new FormUrlEncodedContent(fields))
            using (var response = await client.PostAsync(url, content, cancellation).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Found || response.StatusCode == HttpStatusCode.SeeOther) return;
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (body.Length > 2000) body = body.Substring(0, 2000);
                throw new IOException("AssetRipper endpoint " + new Uri(url).AbsolutePath + " returned HTTP " +
                    (int)response.StatusCode + ": " + body);
            }
        }

        private static string FindExportedAssets(string exportFolder)
        {
            string standard = Path.Combine(exportFolder, "ExportedProject", "Assets");
            if (Directory.Exists(standard)) return standard;
            string direct = Path.Combine(exportFolder, "Assets");
            return Directory.Exists(direct) ? direct : null;
        }

        private static void StopOwnedProcess(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static string QuoteArgument(string argument)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }
    }
}
