using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PotPlayerAiSubtitle;

internal static class WatcherTests
{
    private static int groups;
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pass(string message) { groups++; Console.WriteLine("PASS " + message); }
    private static HashSet<string> Players(params string[] names) { return new HashSet<string>(names, StringComparer.Ordinal); }
    private static void Until(Func<bool> condition, string message)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > 8000) throw new Exception(message); Thread.Sleep(50); }
    }
    private static Process Start(string file, string args)
    {
        return Process.Start(new ProcessStartInfo(Path.Combine(WatcherContract.Root, file), args)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = WatcherContract.Root });
    }
    private static void Finish(Process p) { if (p == null) return; try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } finally { p.Dispose(); } }
    private static int Launches() { string p = Path.Combine(WatcherContract.Root, "launches.txt"); try { return File.Exists(p) ? File.ReadAllLines(p).Length : 0; } catch (IOException) { return 0; } }
    private static void StopMain()
    {
        try { using (EventWaitHandle stop = EventWaitHandle.OpenExisting(WatcherContract.MutexName + "-testmain-stop")) stop.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
        Until(delegate { return !WatcherContract.MutexExists(WatcherContract.AppMutexName); }, "mock main did not exit");
    }
    private static void StopWatcher()
    {
        using (Process stop = Start(WatcherContract.FileName, "--stop"))
        { Assert(stop.WaitForExit(4000) && stop.ExitCode == 0, "stop command failed"); }
        Until(delegate { return !WatcherContract.MutexExists(WatcherContract.MutexName); }, "watcher did not stop");
    }
    private static int Main()
    {
        Process player = null, second = null;
        try
        {
            int calls = 0;
            Func<bool> launch = delegate { calls++; return true; };
            LaunchPolicy policy = new LaunchPolicy();
            policy.Observe(Players(), false, 0, launch); Assert(calls == 0, "launched without a player");
            policy.Observe(Players("1:100"), false, 1000, launch); Assert(calls == 1, "first player not launched");
            policy.Observe(Players("1:100"), false, 10000, launch); Assert(calls == 1, "same player relaunched after exit");
            policy.Observe(Players("1:100", "2:100"), false, 11000, launch); Assert(calls == 2, "new concurrent player missed");
            policy.Observe(Players("1:200"), false, 17000, launch); Assert(calls == 3, "PID reuse missed");
            policy.Observe(Players(), false, 18000, launch);
            policy.Observe(Players("3:100"), false, 24000, launch); Assert(calls == 4, "restart missed");
            Pass("new process, concurrent player, PID reuse, restart; respects explicit main exit");

            calls = 0; policy = new LaunchPolicy();
            policy.Observe(Players("1"), true, 0, launch);
            policy.Observe(Players("1"), false, 10000, launch); Assert(calls == 0, "existing main should consume arrival");
            policy.Observe(Players("2"), false, 11000, launch); Assert(calls == 1, "subsequent player missed");
            Pass("existing main is not duplicated or awakened");

            calls = 0; policy = new LaunchPolicy(); Func<bool> fail = delegate { calls++; return false; };
            policy.Observe(Players("1"), false, 0, fail);
            policy.Observe(Players("1"), false, 4999, fail); Assert(calls == 1, "retry cooldown not respected");
            policy.Observe(Players("1"), false, 5000, fail);
            policy.Observe(Players("1"), false, 10000, fail);
            policy.Observe(Players("1"), false, 20000, fail); Assert(calls == 3, "unbounded retries");
            policy.Observe(Players("2"), false, 21000, fail); Assert(calls == 4, "new arrival cannot retry");
            policy.Observe(Players(), false, 27000, fail); Assert(calls == 4, "departed player retried");
            Pass("failed launches have bounded retries, cooldown, and cancellation on player exit");

            string expected = "\"C:\\Program Files\\AI Subtitle\\Moyu-Watcher.exe\"";
            Assert(StartupManager.BuildCommand(@"C:\Program Files\AI Subtitle\AI-Subtitle-Worker.exe") == expected, "startup command quoting");
            Assert(!WatcherContract.IsPlayerName("unrelated.exe"), "unrelated process matched");
            Assembly helper = Assembly.ReflectionOnlyLoadFrom(Path.Combine(WatcherContract.Root, WatcherContract.FileName));
            Assert(helper.GetReferencedAssemblies().All(delegate(AssemblyName name) { return name.Name == "mscorlib" || name.Name == "System" || name.Name == "System.Core"; }), "heavy helper dependency");
            using (BinaryReader exe = new BinaryReader(File.OpenRead(Path.Combine(WatcherContract.Root, WatcherContract.FileName))))
            { exe.BaseStream.Position = 0x3c; int pe = exe.ReadInt32(); exe.BaseStream.Position = pe + 24 + 68; Assert(exe.ReadUInt16() == 2, "helper is not Windows GUI subsystem"); }
            Pass("quoted helper startup command; no Forms/WMI references or console subsystem");

            player = Start("MoyuTestPlayer.exe", "");
            Until(delegate { return PlayerSnapshot.Read(WatcherContract.IsPlayerName).Count == 1; }, "native snapshot missed fake player");
            string token = PlayerSnapshot.Read(WatcherContract.IsPlayerName).Single();
            Assert(token.StartsWith(player.Id + ":", StringComparison.Ordinal), "snapshot token not PID + creation time");
            Finish(player); player = null;
            Until(delegate { return PlayerSnapshot.Read(WatcherContract.IsPlayerName).Count == 0; }, "snapshot retained exited player");
            Pass("real native process snapshot detects same-session stub creation and exit");

            StartupManager.EnsureRunning(); StartupManager.EnsureRunning();
            using (Process duplicate = Start(WatcherContract.FileName, ""))
                Assert(duplicate.WaitForExit(4000) && duplicate.ExitCode == 0, "duplicate watcher did not exit");
            Assert(Launches() == 0, "watcher launched without player");
            Pass("helper starts without main and remains single-instance");

            player = Start("MoyuTestPlayer.exe", "");
            Until(delegate { return Launches() == 1 && WatcherContract.MutexExists(WatcherContract.AppMutexName); }, "helper did not launch mock main");
            Assert(File.ReadAllLines(Path.Combine(WatcherContract.Root, "launches.txt"))[0] == "--tray", "main launch arguments");
            StopMain(); Thread.Sleep(6000);
            Assert(Launches() == 1, "explicit exit immediately relaunched main");
            Pass("real helper wakes mock main on new player, then respects explicit main exit");

            second = Start("MoyuTestPlayer.exe", "");
            Until(delegate { return Launches() == 2 && WatcherContract.MutexExists(WatcherContract.AppMutexName); }, "second player did not relaunch main");
            Finish(player); player = null; Finish(second); second = null; StopMain();
            StopWatcher(); StartupManager.EnsureRunning();
            // Exercise the exact settings stop implementation without changing any registry values.
            typeof(StartupManager).GetMethod("StopCore", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            Assert(!WatcherContract.MutexExists(WatcherContract.MutexName), "settings controller left watcher running");
            StartupManager.EnsureRunning(); StopWatcher();
            Pass("new concurrent player relaunches main; command/settings stop and immediate restart work");
            Console.WriteLine(groups + " watcher groups passed. No production registry, player, or main process modified.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
        finally
        {
            Finish(player); Finish(second);
            try { StopMain(); } catch { }
            try { StopWatcher(); } catch { }
        }
    }
}

internal static class WatcherTestStub
{
    private static int Main(string[] args)
    {
        if (!string.Equals(Path.GetFileName(Assembly.GetExecutingAssembly().Location), WatcherContract.MainFileName, StringComparison.OrdinalIgnoreCase))
        { Thread.Sleep(60000); return 0; }
        bool created;
        using (Mutex mutex = new Mutex(true, WatcherContract.AppMutexName, out created))
        {
            if (!created) return 0;
            using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, WatcherContract.MutexName + "-testmain-stop"))
            { File.AppendAllText(Path.Combine(WatcherContract.Root, "launches.txt"), string.Join(" ", args) + Environment.NewLine); stop.WaitOne(60000); }
        }
        return 0;
    }
}
