using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PotPlayerAiSubtitle
{
    internal static class PlayerSnapshot
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeFileHandle process, out long created, out long exited, out long kernel, out long user);

        internal static HashSet<string> Read(Func<string, bool> matchesName)
        {
            uint currentSession;
            using (Process self = Process.GetCurrentProcess())
                if (!ProcessIdToSessionId((uint)self.Id, out currentSession)) throw new Win32Exception();
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            // One native snapshot per tick. Only matching players are opened; no WMI queries or module scans.
            using (SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0))
            {
                if (snapshot.IsInvalid) throw new Win32Exception();
                ProcessEntry entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                if (!Process32First(snapshot, ref entry)) throw new Win32Exception();
                do
                {
                    if (!matchesName(entry.ExeFile)) continue;
                    uint session;
                    if (!ProcessIdToSessionId(entry.ProcessId, out session) || session != currentSession) continue;
                    using (SafeFileHandle process = OpenProcess(0x1000, false, entry.ProcessId))
                    {
                        long created, exited, kernel, user;
                        // Include creation time to distinguish reused PIDs. Skip exited or inaccessible processes.
                        if (process.IsInvalid || !GetProcessTimes(process, out created, out exited, out kernel, out user)) continue;
                        result.Add(entry.ProcessId + ":" + created);
                    }
                } while (Process32Next(snapshot, ref entry));
                int error = Marshal.GetLastWin32Error();
                if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
            }
            return result;
        }
    }
}
