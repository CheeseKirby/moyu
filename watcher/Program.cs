using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal static class WatcherProgram
    {
        // WinExe: no console, Forms, message loop or tray icon. The wait is interruptible by the settings switch.
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--stop")
            {
                try { using (EventWaitHandle stop = EventWaitHandle.OpenExisting(WatcherContract.StopEventName)) stop.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                return 0;
            }
            if (args.Length != 0) return 2;
            try
            {
                bool created;
                using (Mutex instance = new Mutex(true, WatcherContract.MutexName, out created))
                {
                    if (!created) return 0;
                    using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, WatcherContract.StopEventName))
                    {
                        Log("Watcher started; waiting for PotPlayer.");
                        LaunchPolicy policy = new LaunchPolicy();
                        Stopwatch clock = Stopwatch.StartNew();
                        long nextErrorLog = 0;
                        while (!stop.WaitOne(0))
                        {
                            try
                            {
                                policy.Observe(PlayerSnapshot.Read(WatcherContract.IsPlayerName),
                                    WatcherContract.MutexExists(WatcherContract.AppMutexName), clock.ElapsedMilliseconds, StartMain);
                            }
                            catch (Exception ex)
                            {
                                // Do not clear observed players on a transient snapshot error, or they would look new again.
                                if (clock.ElapsedMilliseconds >= nextErrorLog) { Log("Detection error: " + ex.Message); nextErrorLog = clock.ElapsedMilliseconds + 60000; }
                            }
                            if (stop.WaitOne(1000)) break;
                        }
                        Log("Watcher stopped.");
                    }
                }
                return 0;
            }
            catch (Exception ex) { Log("Watcher failed: " + ex.Message); return 1; }
        }
        private static bool StartMain()
        {
            try
            {
                string executable = Path.Combine(WatcherContract.Root, WatcherContract.MainFileName);
                if (!File.Exists(executable)) throw new FileNotFoundException("Main application is missing.");
                using (Process process = Process.Start(new ProcessStartInfo(executable, "--tray")
                    { WorkingDirectory = WatcherContract.Root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    if (process == null) return false;
                }
                Log("PotPlayer started; launched Moyu.");
                return true;
            }
            catch (Exception ex) { Log("Cannot launch Moyu: " + ex.Message); return false; }
        }
        private static void Log(string message)
        {
            try
            {
                string directory = Path.Combine(WatcherContract.Root, "Logs"); Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "watcher.log");
                if (File.Exists(file) && new FileInfo(file).Length > 262144)
                {
                    File.Copy(file, file + ".previous", true); File.WriteAllText(file, "");
                }
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { } // A read-only installation must not produce repeated dialogs or crash the detector.
        }
    }
}
