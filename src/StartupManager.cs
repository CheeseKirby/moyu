using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace PotPlayerAiSubtitle
{
    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "魔芋";
        private const string LegacyValueName = "PotPlayer AI Subtitle";

        internal static string BuildCommand(string executablePath)
        {
            return "\"" + Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executablePath)), WatcherContract.FileName) + "\"";
        }

        public static void SetEnabled(bool enabled)
        {
            WithControlLock(delegate
            {
                string watcher = Path.Combine(WatcherContract.Root, WatcherContract.FileName);
                if (enabled && !File.Exists(watcher))
                    throw new FileNotFoundException("找不到后台检测器，请重新构建或将 Moyu-Watcher.exe 放回程序目录。", watcher);
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) throw new InvalidOperationException("无法更新当前用户的自动启动设置。");
                    key.DeleteValue(LegacyValueName, false);
                    if (enabled) key.SetValue(ValueName, BuildCommand(Path.Combine(WatcherContract.Root, WatcherContract.MainFileName)), RegistryValueKind.String);
                    else key.DeleteValue(ValueName, false);
                }
                if (enabled) StartCore(watcher);
                else StopCore();
            });
        }

        internal static void EnsureRunning()
        {
            WithControlLock(delegate { StartCore(Path.Combine(WatcherContract.Root, WatcherContract.FileName)); });
        }

        private static void WithControlLock(Action action)
        {
            using (Mutex control = new Mutex(false, WatcherContract.ControlMutexName))
            {
                bool owned = false;
                try
                {
                    try { owned = control.WaitOne(5000); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new TimeoutException("后台检测器正在切换状态，请稍后重试。");
                    action();
                }
                finally { if (owned) control.ReleaseMutex(); }
            }
        }

        private static void StartCore(string watcher)
        {
            Stopwatch timer = Stopwatch.StartNew();
            // A previous stop may still be draining. Do not mistake that instance for a ready watcher.
            while (WatcherContract.MutexExists(WatcherContract.MutexName))
            {
                try
                {
                    using (EventWaitHandle stop = EventWaitHandle.OpenExisting(WatcherContract.StopEventName))
                        if (!stop.WaitOne(0)) return;
                }
                catch (WaitHandleCannotBeOpenedException) { }
                if (timer.ElapsedMilliseconds > 3000) throw new TimeoutException("后台检测器未能就绪，请稍后重试。");
                Thread.Sleep(25);
            }
            using (Process process = Process.Start(new ProcessStartInfo(watcher)
                { WorkingDirectory = WatcherContract.Root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                if (process == null) throw new InvalidOperationException("无法启动后台检测器。");
                timer.Restart();
                while (timer.ElapsedMilliseconds < 3000)
                {
                    try
                    {
                        using (EventWaitHandle stop = EventWaitHandle.OpenExisting(WatcherContract.StopEventName))
                            if (!stop.WaitOne(0) && WatcherContract.MutexExists(WatcherContract.MutexName)) return;
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    if (process.HasExited) throw new InvalidOperationException("后台检测器提前退出，请查看 Logs/watcher.log。");
                    Thread.Sleep(25);
                }
                throw new TimeoutException("后台检测器启动超时，请查看 Logs/watcher.log。");
            }
        }

        private static void StopCore()
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (WatcherContract.MutexExists(WatcherContract.MutexName))
            {
                try { using (EventWaitHandle stop = EventWaitHandle.OpenExisting(WatcherContract.StopEventName)) stop.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                if (timer.ElapsedMilliseconds > 3000) throw new TimeoutException("后台检测器尚未停止，请稍后重试。");
                Thread.Sleep(25);
            }
        }
    }
}
