using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    // Shared with the small watcher executable. No Forms, WMI, configuration or subtitle dependencies.
    internal static class WatcherContract
    {
        internal const string FileName = "Moyu-Watcher.exe";
        internal const string MainFileName = "AI-Subtitle-Worker.exe";
        internal const string AppMutexName = @"Local\PotPlayerAiSubtitleApp-v2";
        internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        internal static readonly string Identity = CreateIdentity(Root);
        internal static string MutexName { get { return @"Local\MoyuWatcher-" + Identity; } }
        internal static string StopEventName { get { return MutexName + "-stop"; } }
        internal static string ControlMutexName { get { return MutexName + "-control"; } }

        private static string CreateIdentity(string root)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))).Replace("-", "").Substring(0, 20);
        }
        internal static bool MutexExists(string name)
        {
            try { using (Mutex.OpenExisting(name)) return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
        }
        internal static bool IsPlayerName(string name)
        {
            if (name == null) return false;
            string stem = Path.GetFileNameWithoutExtension(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe");
            return string.Equals(stem, "PotPlayerMini64", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, "PotPlayerMini", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, "PotPlayer64", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, "PotPlayer", StringComparison.OrdinalIgnoreCase);
        }
    }
}
