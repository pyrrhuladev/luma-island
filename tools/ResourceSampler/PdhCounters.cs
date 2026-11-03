using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

internal sealed class PdhCounters : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();
    private readonly List<string> _setupErrors = new();
    private static readonly string[] Names = { "ID Process", "% Processor Time", "Working Set", "Working Set - Private", "Private Bytes" };
    public PdhCounters()
    {
        Check(OpenQuery(null, UIntPtr.Zero, out _query), "PdhOpenQuery");
        try
        {
            foreach (string name in Names)
            {
                uint status = AddEnglishCounter(_query, @"\Process(*)\" + name, UIntPtr.Zero, out var counter);
                if (status == 0) _counters.Add(name, counter);
                else _setupErrors.Add($"{name} unavailable 0x{status:X8}");
            }
            if (!_counters.ContainsKey("ID Process")) throw new InvalidOperationException("PDH Process ID mapping is unavailable.");
            Collect(_query); // Rate counters require a second successful sample.
        }
        catch { Dispose(); throw; }
    }
    public Dictionary<string, Dictionary<string, double?>> Read(List<string> errors)
    {
        var result = new Dictionary<string, Dictionary<string, double?>>();
        errors.AddRange(_setupErrors);
        uint status = Collect(_query);
        if (status != 0) { errors.Add($"PDH collect 0x{status:X8}"); return result; }
        foreach (var pair in _counters) result[pair.Key] = ReadArray(pair.Value, pair.Key, errors);
        bool available = false;
        foreach (var values in result.Values)
            foreach (double? value in values.Values) if (value.HasValue) { available = true; break; }
        if (!available) errors.Add("All PDH Process counters are unavailable.");
        return result;
    }
    private static Dictionary<string, double?> ReadArray(IntPtr counter, string name, List<string> errors)
    {
        const uint MoreData = 0x800007D2;
        var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint bytes = 0;
            uint status = GetArray(counter, 0x8200, ref bytes, out _, IntPtr.Zero); // DOUBLE | NOCAP100
            if (status != MoreData || bytes == 0 || bytes > 32 * 1024 * 1024) { errors.Add($"{name} sizing 0x{status:X8}"); return values; }
            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                uint allocated = bytes;
                status = GetArray(counter, 0x8200, ref bytes, out uint count, buffer);
                if (status == MoreData) continue;
                int stride = Marshal.SizeOf<CounterItem>();
                if (status != 0 || count > allocated / stride) { errors.Add($"{name} read 0x{status:X8}"); return values; }
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, i * stride));
                    string? instance = Marshal.PtrToStringUni(item.Name);
                    if (instance is null) continue;
                    values[instance] = item.Value.Status <= 1 && double.IsFinite(item.Value.Number) && item.Value.Number >= 0 ? item.Value.Number : null;
                }
                return values;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        errors.Add(name + " instances changed during both reads");
        return values;
    }
    private static void Check(uint status, string operation) { if (status != 0) throw new Win32Exception(unchecked((int)status), operation + $": 0x{status:X8}"); }
    public void Dispose() { if (_query != IntPtr.Zero) CloseQuery(_query); _query = IntPtr.Zero; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct CounterValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Number; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] private static extern uint OpenQuery(string? source, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] private static extern uint AddEnglishCounter(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")] private static extern uint Collect(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)] private static extern uint GetArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")] private static extern uint CloseQuery(IntPtr query);
}
