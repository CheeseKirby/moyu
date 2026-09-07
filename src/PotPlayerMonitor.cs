using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal sealed class PotPlayerMonitor : IDisposable
    {
        private const int PollMilliseconds = 250;
        private readonly Action<string> mediaChanged;
        private readonly object stateLock = new object();
        private readonly List<string> knownMediaPaths = new List<string>();
        private readonly HashSet<int> inspectedProcessIds = new HashSet<int>();
        private CancellationTokenSource cancellation;
        private Thread thread;
        private string currentSessionPath = "";
        private DateTime noPlayerSince = DateTime.MinValue;

        public PotPlayerMonitor(Action<string> mediaChanged)
        {
            if (mediaChanged == null) throw new ArgumentNullException("mediaChanged");
            this.mediaChanged = mediaChanged;
        }

        public bool IsRunning { get { return thread != null && thread.IsAlive; } }

        public void Start()
        {
            if (IsRunning) return;
            cancellation = new CancellationTokenSource();
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "PotPlayer media monitor";
            thread.Start();
        }

        public void Stop()
        {
            CancellationTokenSource source = cancellation;
            if (source != null) source.Cancel();
            Thread running = thread;
            if (running != null && running.IsAlive && Thread.CurrentThread != running) running.Join(2000);
            thread = null;
            if (source != null) source.Dispose();
            cancellation = null;
        }

        public void Dispose()
        {
            Stop();
        }

        private void Loop()
        {
            Logger.Write("PotPlayer monitor started.");
            int loopCount = 0;
            try
            {
                while (cancellation != null && !cancellation.IsCancellationRequested)
                {
                    int playerCount = InspectRunningProcesses();
                    if ((loopCount++ % 4) == 0) InspectWindowTitles();
                    if (playerCount > 0)
                    {
                        noPlayerSince = DateTime.MinValue;
                    }
                    else
                    {
                        if (noPlayerSince == DateTime.MinValue) noPlayerSince = DateTime.UtcNow;
                        if ((DateTime.UtcNow - noPlayerSince).TotalSeconds >= 2 && !string.IsNullOrEmpty(currentSessionPath))
                            ResetPlaybackSession();
                    }
                    if (cancellation.Token.WaitHandle.WaitOne(PollMilliseconds)) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Write("PotPlayer monitor error: " + ex);
            }
            finally
            {
                Logger.Write("PotPlayer monitor stopped.");
            }
        }

        private int InspectRunningProcesses()
        {
            List<Process> players = GetPlayers();
            HashSet<int> activeIds = new HashSet<int>(players.Select(delegate(Process process) { return process.Id; }));
            foreach (Process process in players)
            {
                bool inspect;
                lock (stateLock) inspect = inspectedProcessIds.Add(process.Id);
                try
                {
                    if (inspect) InspectProcessById((uint)process.Id);
                }
                catch (Exception ex)
                {
                    Logger.Write("Cannot inspect PotPlayer process " + process.Id + ": " + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }
            lock (stateLock) inspectedProcessIds.RemoveWhere(delegate(int id) { return !activeIds.Contains(id); });
            return players.Count;
        }

        private void InspectProcessById(uint processId)
        {
            string query = "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + processId;
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                {
                    using (item)
                    {
                        string commandLine = item["CommandLine"] as string;
                        if (!string.IsNullOrWhiteSpace(commandLine)) InspectCommandLine(commandLine);
                    }
                }
            }
        }

        private void InspectCommandLine(string commandLine)
        {
            foreach (string argument in SplitCommandLine(commandLine))
            {
                string candidate = NormalizeCandidate(argument);
                if (candidate != null) ReportMedia(candidate);
            }
        }

        private static string NormalizeCandidate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string candidate = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri;
                if (Uri.TryCreate(candidate, UriKind.Absolute, out uri) && uri.IsFile) candidate = uri.LocalPath;
            }
            if (candidate.StartsWith("/", StringComparison.Ordinal) || candidate.StartsWith("-", StringComparison.Ordinal)) return null;
            try
            {
                if (!Path.IsPathRooted(candidate) || !File.Exists(candidate) || !IsMediaFile(candidate)) return null;
                return Path.GetFullPath(candidate);
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsMediaFile(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            switch (extension)
            {
                case ".3g2": case ".3gp": case ".asf": case ".avi": case ".divx": case ".flv":
                case ".m2ts": case ".m4v": case ".mkv": case ".mov": case ".mp4": case ".mpeg":
                case ".mpg": case ".mts": case ".ogm": case ".rm": case ".rmvb": case ".ts":
                case ".vob": case ".webm": case ".wmv":
                    return true;
                default:
                    return false;
            }
        }

        private void InspectWindowTitles()
        {
            foreach (Process process in GetPlayers())
            {
                try
                {
                    string displayName = ExtractDisplayName(process.MainWindowTitle);
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    string resolved = ResolveFromKnownPaths(displayName) ?? ResolveFromCurrentMedia(displayName) ?? ResolveFromRecentLinks(displayName);
                    if (resolved != null) ReportMedia(resolved);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }

        private static string ExtractDisplayName(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            const string suffix = " - PotPlayer";
            string value = title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? title.Substring(0, title.Length - suffix.Length).Trim()
                : title.Trim();
            if (string.Equals(value, "PotPlayer", StringComparison.OrdinalIgnoreCase)) return null;
            return value;
        }

        private string ResolveFromKnownPaths(string displayName)
        {
            lock (stateLock)
            {
                foreach (string path in knownMediaPaths.ToArray())
                {
                    if (!File.Exists(path)) continue;
                    if (TitleMatchesPath(displayName, path)) return path;
                    string directory = Path.GetDirectoryName(path);
                    string candidate = Path.Combine(directory, displayName);
                    if (File.Exists(candidate) && IsMediaFile(candidate)) return Path.GetFullPath(candidate);
                }
            }
            return null;
        }

        private static string ResolveFromCurrentMedia(string displayName)
        {
            CurrentMediaState current = AtomicJson.Read<CurrentMediaState>(StoragePaths.CurrentMediaFile, null);
            if (current == null || string.IsNullOrWhiteSpace(current.MediaPath) || !File.Exists(current.MediaPath)) return null;
            return TitleMatchesPath(displayName, current.MediaPath) ? Path.GetFullPath(current.MediaPath) : null;
        }

        private static string ResolveFromRecentLinks(string displayName)
        {
            string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (string.IsNullOrEmpty(recent) || !Directory.Exists(recent)) return null;
            foreach (string shortcutPath in Directory.GetFiles(recent, "*.lnk").OrderByDescending(File.GetLastWriteTimeUtc).Take(30))
            {
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(shortcutPath)).TotalMinutes > 10) break;
                string target = ResolveShortcutTarget(shortcutPath);
                if (target != null && File.Exists(target) && IsMediaFile(target) && TitleMatchesPath(displayName, target)) return Path.GetFullPath(target);
            }
            return null;
        }

        private static string ResolveShortcutTarget(string shortcutPath)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                return shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        private static bool TitleMatchesPath(string displayName, string path)
        {
            string fileName = Path.GetFileName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);
            return string.Equals(displayName, fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(displayName, baseName, StringComparison.OrdinalIgnoreCase)
                || displayName.StartsWith(fileName + " ", StringComparison.OrdinalIgnoreCase);
        }

        private void ReportMedia(string path)
        {
            string fullPath = Path.GetFullPath(path);
            lock (stateLock)
            {
                knownMediaPaths.RemoveAll(delegate(string item) { return string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase); });
                knownMediaPaths.Insert(0, fullPath);
                if (knownMediaPaths.Count > 30) knownMediaPaths.RemoveRange(30, knownMediaPaths.Count - 30);
                if (string.Equals(currentSessionPath, fullPath, StringComparison.OrdinalIgnoreCase)) return;
                currentSessionPath = fullPath;
            }

            AtomicJson.Write(StoragePaths.CurrentMediaFile, new CurrentMediaState
            {
                MediaPath = fullPath,
                UpdatedUtc = DateTime.UtcNow.ToString("o")
            });
            Logger.Write("PotPlayer media detected: " + fullPath);
            mediaChanged(fullPath);
        }

        private void ResetPlaybackSession()
        {
            lock (stateLock)
            {
                currentSessionPath = "";
                inspectedProcessIds.Clear();
            }
            AtomicJson.Write(StoragePaths.CurrentMediaFile, new CurrentMediaState
            {
                MediaPath = "",
                UpdatedUtc = DateTime.UtcNow.ToString("o")
            });
            mediaChanged(null);
        }

        private static List<Process> GetPlayers()
        {
            List<Process> result = new List<Process>();
            result.AddRange(Process.GetProcessesByName("PotPlayerMini64"));
            result.AddRange(Process.GetProcessesByName("PotPlayer64"));
            return result;
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static IEnumerable<string> SplitCommandLine(string commandLine)
        {
            int count;
            IntPtr values = CommandLineToArgvW(commandLine, out count);
            if (values == IntPtr.Zero) yield break;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr value = Marshal.ReadIntPtr(values, i * IntPtr.Size);
                    yield return Marshal.PtrToStringUni(value);
                }
            }
            finally
            {
                LocalFree(values);
            }
        }
    }
}