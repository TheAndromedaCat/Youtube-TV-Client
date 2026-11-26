using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Youtube_TV.Desktop
{
    /// <summary>
    /// Automatically applies TubeTV's branded tile artwork to the Windows 11 Xbox app's
    /// "My apps" tab whenever TubeTV is registered in the Xbox app's CustomLibraryManagement
    /// manifest (%LOCALAPPDATA%\Packages\Microsoft.GamingApp_8wekyb3d8bbwe\LocalState\CustomLibraryManagement).
    ///
    /// Preserves all existing manifest fields verbatim (never injecting synthetic entries
    /// that could conflict with future Xbox app schema changes) and only updates the PNG
    /// image (or populates an empty imagePath) for existing TubeTV entries.
    /// </summary>
    public sealed class XboxTileArtworkService : IDisposable
    {
        private const string EmbeddedTileResourceName = "Youtube_TV.Desktop.XboxTile.png";
        private const string FallbackTileFileName = "TubeTV-XboxTile.png";
        private const string ManifestFileName = "CustomLibraryManagement.manifest";
        private const int WatchDebounceMs = 300;

        private readonly object _syncLock = new object();
        private FileSystemWatcher _manifestDirWatcher;
        private FileSystemWatcher _imagesDirWatcher;
        private Timer _debounceTimer;
        private bool _isDisposed;

        /// <summary>
        /// One-shot check that updates the Xbox app tile PNG for any existing TubeTV entry
        /// in CustomLibraryManagement.manifest. Safe no-op if the Xbox app is not installed
        /// or TubeTV has not been added to the Xbox app library.
        /// </summary>
        public static bool SyncIfRegistered()
        {
            try
            {
                string manifestDir = GetManifestDirectory();
                if (string.IsNullOrEmpty(manifestDir))
                {
                    return false;
                }

                string manifestPath = Path.Combine(manifestDir, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    return false;
                }

                byte[] tileBytes = ReadTileArtworkBytes();
                if (tileBytes == null || tileBytes.Length == 0)
                {
                    return false;
                }

                string rawJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                JsonNode rootNode = JsonNode.Parse(rawJson);
                JsonObject rootObj = rootNode as JsonObject;
                if (rootObj == null)
                {
                    return false;
                }

                string exePath = GetCurrentExecutablePath();
                string exeDir = NormalizeDirectory(Path.GetDirectoryName(exePath));
                string exeName = Path.GetFileName(exePath);

                List<JsonObject> matchedEntries = FindMatchingTubeTvEntries(rootObj, exeDir, exeName);
                if (matchedEntries.Count == 0)
                {
                    return false;
                }

                string imagesDir = Path.Combine(manifestDir, "Images");
                bool manifestModified = false;
                bool anyUpdated = false;

                for (int i = 0; i < matchedEntries.Count; i++)
                {
                    JsonObject entry = matchedEntries[i];
                    string imagePath = GetStringProperty(entry, "imagePath");

                    if (string.IsNullOrWhiteSpace(imagePath))
                    {
                        string entryId = GetStringProperty(entry, "id");
                        string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(entryId) ? "TubeTV" : entryId);
                        Directory.CreateDirectory(imagesDir);
                        imagePath = Path.Combine(imagesDir, safeName + ".png");

                        if (WriteFileAtomicIfDifferent(imagePath, tileBytes))
                        {
                            anyUpdated = true;
                        }

                        entry["imagePath"] = imagePath;
                        manifestModified = true;
                    }
                    else
                    {
                        string targetDir = Path.GetDirectoryName(imagePath);
                        if (!string.IsNullOrEmpty(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        if (WriteFileAtomicIfDifferent(imagePath, tileBytes))
                        {
                            anyUpdated = true;
                        }
                    }
                }

                if (manifestModified)
                {
                    string backupPath = manifestPath + ".tubetv.bak";
                    if (!File.Exists(backupPath))
                    {
                        try
                        {
                            File.Copy(manifestPath, backupPath, false);
                        }
                        catch (Exception)
                        {
                        }
                    }

                    string updatedJson = rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                    byte[] manifestBytes = new UTF8Encoding(false).GetBytes(updatedJson);
                    if (WriteFileAtomicIfDifferent(manifestPath, manifestBytes))
                    {
                        anyUpdated = true;
                    }
                }

                return anyUpdated;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Watches the Xbox app's CustomLibraryManagement folder while TubeTV is running so
        /// if the user adds TubeTV via "+" or the Xbox app regenerates the placeholder icon,
        /// our tile artwork is re-applied automatically.
        /// </summary>
        public void StartWatching()
        {
            lock (_syncLock)
            {
                if (_isDisposed || _manifestDirWatcher != null)
                {
                    return;
                }

                try
                {
                    string manifestDir = GetManifestDirectory();
                    if (string.IsNullOrEmpty(manifestDir) || !Directory.Exists(manifestDir))
                    {
                        return;
                    }

                    _debounceTimer = new Timer(OnDebouncedSync, null, Timeout.Infinite, Timeout.Infinite);

                    _manifestDirWatcher = CreateDirectoryWatcher(manifestDir);
                    string imagesDir = Path.Combine(manifestDir, "Images");
                    if (Directory.Exists(imagesDir))
                    {
                        _imagesDirWatcher = CreateDirectoryWatcher(imagesDir);
                    }
                }
                catch (Exception)
                {
                    DisposeWatchersLocked();
                }
            }
        }

        private FileSystemWatcher CreateDirectoryWatcher(string directoryPath)
        {
            FileSystemWatcher watcher = new FileSystemWatcher(directoryPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += OnDirectoryChanged;
            watcher.Created += OnDirectoryChanged;
            watcher.Renamed += OnDirectoryRenamed;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private void OnDirectoryChanged(object sender, FileSystemEventArgs e)
        {
            ScheduleDebouncedSync();
        }

        private void OnDirectoryRenamed(object sender, RenamedEventArgs e)
        {
            ScheduleDebouncedSync();
        }

        private void ScheduleDebouncedSync()
        {
            lock (_syncLock)
            {
                if (_isDisposed || _debounceTimer == null)
                {
                    return;
                }

                try
                {
                    _debounceTimer.Change(WatchDebounceMs, Timeout.Infinite);
                }
                catch (Exception)
                {
                }
            }
        }

        private void OnDebouncedSync(object state)
        {
            if (_isDisposed)
            {
                return;
            }

            SyncIfRegistered();
        }

        private static List<JsonObject> FindMatchingTubeTvEntries(JsonObject rootObj, string exeDir, string exeName)
        {
            List<JsonObject> exactMatches = new List<JsonObject>();
            List<JsonObject> nameMatches = new List<JsonObject>();

            foreach (KeyValuePair<string, JsonNode> topPair in rootObj)
            {
                JsonObject cacheObj = topPair.Value as JsonObject;
                if (cacheObj == null)
                {
                    continue;
                }

                if (string.Equals(topPair.Key, "provider", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (KeyValuePair<string, JsonNode> itemPair in cacheObj)
                {
                    JsonObject entry = itemPair.Value as JsonObject;
                    if (entry == null)
                    {
                        continue;
                    }

                    string entryExe = GetStringProperty(entry, "executableName");
                    string entryLoc = NormalizeDirectory(GetStringProperty(entry, "installLocation"));
                    string entryTitle = GetStringProperty(entry, "title");

                    bool exeMatches = (!string.IsNullOrEmpty(exeName) &&
                        string.Equals(entryExe, exeName, StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(entryExe, "TubeTV.exe", StringComparison.OrdinalIgnoreCase);
                    bool dirMatches = !string.IsNullOrEmpty(exeDir) &&
                        string.Equals(entryLoc, exeDir, StringComparison.OrdinalIgnoreCase);

                    if (exeMatches && dirMatches)
                    {
                        exactMatches.Add(entry);
                    }
                    else if (exeMatches)
                    {
                        nameMatches.Add(entry);
                    }
                    else if (string.IsNullOrEmpty(entryExe) &&
                             (string.Equals(entryTitle, "TubeTV", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(entryTitle, "TubeTV - Client for YouTube TV", StringComparison.OrdinalIgnoreCase)))
                    {
                        nameMatches.Add(entry);
                    }
                }
            }

            return exactMatches.Count > 0 ? exactMatches : nameMatches;
        }

        private static string GetManifestDirectory()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                return null;
            }

            return Path.Combine(
                localAppData,
                "Packages",
                "Microsoft.GamingApp_8wekyb3d8bbwe",
                "LocalState",
                "CustomLibraryManagement");
        }

        private static string GetCurrentExecutablePath()
        {
            string path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            return Path.Combine(AppContext.BaseDirectory, "TubeTV.exe");
        }

        private static byte[] ReadTileArtworkBytes()
        {
            try
            {
                Assembly asm = typeof(XboxTileArtworkService).Assembly;
                using (Stream stream = asm.GetManifestResourceStream(EmbeddedTileResourceName))
                {
                    if (stream != null)
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            try
            {
                string diskPath = Path.Combine(AppContext.BaseDirectory, FallbackTileFileName);
                if (File.Exists(diskPath))
                {
                    return File.ReadAllBytes(diskPath);
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private static bool WriteFileAtomicIfDifferent(string targetPath, byte[] bytes)
        {
            if (File.Exists(targetPath))
            {
                try
                {
                    byte[] existing = File.ReadAllBytes(targetPath);
                    if (ByteArraysEqual(existing, bytes))
                    {
                        return false;
                    }
                }
                catch (Exception)
                {
                }
            }

            string tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, targetPath, true);
                return true;
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception)
                {
                }
                return false;
            }
        }

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string GetStringProperty(JsonObject obj, string key)
        {
            JsonNode node;
            if (obj == null || !obj.TryGetPropertyValue(key, out node) || node == null)
            {
                return null;
            }

            JsonValue val = node as JsonValue;
            if (val == null)
            {
                return null;
            }

            string str;
            return val.TryGetValue<string>(out str) ? str : null;
        }

        private static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Trim().Replace('/', '\\').TrimEnd('\\');
            return normalized + "\\";
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool isInvalid = false;
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (c == invalid[j])
                    {
                        isInvalid = true;
                        break;
                    }
                }
                sb.Append(isInvalid ? '_' : c);
            }
            return sb.ToString();
        }

        private void DisposeWatchersLocked()
        {
            if (_debounceTimer != null)
            {
                _debounceTimer.Dispose();
                _debounceTimer = null;
            }

            if (_manifestDirWatcher != null)
            {
                _manifestDirWatcher.EnableRaisingEvents = false;
                _manifestDirWatcher.Changed -= OnDirectoryChanged;
                _manifestDirWatcher.Created -= OnDirectoryChanged;
                _manifestDirWatcher.Renamed -= OnDirectoryRenamed;
                _manifestDirWatcher.Dispose();
                _manifestDirWatcher = null;
            }

            if (_imagesDirWatcher != null)
            {
                _imagesDirWatcher.EnableRaisingEvents = false;
                _imagesDirWatcher.Changed -= OnDirectoryChanged;
                _imagesDirWatcher.Created -= OnDirectoryChanged;
                _imagesDirWatcher.Renamed -= OnDirectoryRenamed;
                _imagesDirWatcher.Dispose();
                _imagesDirWatcher = null;
            }
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                DisposeWatchersLocked();
            }
        }
    }
}
