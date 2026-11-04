using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Luma.Island.Services;

/// <summary>One process-only Toolhelp snapshot. Only matching game candidates are opened with query-limited rights.</summary>
public static class GameProcessReader
{
    public static GameProcessSnapshot Capture(IReadOnlyCollection<string> executableNames, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = new HashSet<string>(executableNames, StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return new(true, Array.Empty<GameProcessInfo>(), 0);
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS: no modules, threads or heaps.
        if (snapshot.IsInvalid) return Failure("process_snapshot_failed");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExecutableName = string.Empty };
        var matches = new List<GameProcessInfo>();
        if (!Process32First(snapshot, ref entry))
            return Marshal.GetLastWin32Error() == 18 ? new(true, Array.Empty<GameProcessInfo>(), 0) : Failure("process_snapshot_failed");
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ProcessId is > 0 and <= int.MaxValue && names.Contains(Path.GetFileName(entry.ExecutableName)))
                matches.Add(new GameProcessInfo((int)entry.ProcessId, Path.GetFileName(entry.ExecutableName), ReadPath(entry.ProcessId)));
        } while (Process32Next(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) return Failure("process_snapshot_failed");
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
        return new(true, matches.ToArray(), foreground <= int.MaxValue ? (int)foreground : 0);
    }

    private static string? ReadPath(uint processId)
    {
        using SafeProcessHandle process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION.
        if (process.IsInvalid) return null;
        var path = new StringBuilder(512);
        uint size = (uint)path.Capacity;
        if (QueryFullProcessImageName(process, 0, path, ref size)) return path.ToString();
        if (Marshal.GetLastWin32Error() != 122) return null;
        path = new StringBuilder(32768);
        size = (uint)path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
    }

    private static GameProcessSnapshot Failure(string reason) => new(false, Array.Empty<GameProcessInfo>(), 0, reason);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableName;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
