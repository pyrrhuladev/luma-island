using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Luma.Island.Services;

public sealed record PerformanceSnapshot(double? CpuPercent, double? GpuPercent, double? MemoryPercent, ulong UsedMemoryBytes, ulong TotalMemoryBytes)
{
    public static PerformanceSnapshot Empty { get; } = new(null, null, null, 0, 0);
}

/// <summary>Opt-in system measurements. All native sampling runs off the UI thread.</summary>
public sealed class PerformanceService : IDisposable
{
    private sealed record Configuration(int Version, int Flags);
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly Timer _timer;
    private readonly object _nativeGate = new();
    private Configuration _configuration = new(0, 0);
    private int _version, _nativeVersion = -1;
    private volatile bool _disposed;
    private bool _hasCpuSample, _hasGpuSample, _gpuUnavailable;
    private ulong _idle, _kernel, _user;
    private long _lastSampleTimestamp;
    private IntPtr _query, _gpuCounter;
    private static readonly Regex EngineName = new(
        @"luid_(?<luid>0x[0-9a-f]+_0x[0-9a-f]+)_phys_(?<physical>\d+)_eng_(?<engine>\d+)(?:_engtype_|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public PerformanceService() => _timer = new Timer(_ => Sample(), null, Timeout.Infinite, Timeout.Infinite);
    public bool IsRunning => !_disposed && Volatile.Read(ref _configuration).Flags != 0;
    public PerformanceSnapshot Current { get; private set; } = PerformanceSnapshot.Empty;
    /// <summary>Raised on the WPF application dispatcher.</summary>
    public event Action<PerformanceSnapshot>? Changed;

    public void Configure(bool cpu, bool gpu, bool memory)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        int flags = (cpu ? 1 : 0) | (gpu ? 2 : 0) | (memory ? 4 : 0);
        if (Volatile.Read(ref _configuration).Flags == flags) return;
        var configuration = new Configuration(++_version, flags);
        Volatile.Write(ref _configuration, configuration);
        _timer.Change(flags == 0 ? Timeout.Infinite : 0, flags == 0 ? Timeout.Infinite : 2000);
        Publish(PerformanceSnapshot.Empty);
        if (!gpu)
        {
            // Serialize cleanup with an already-running PDH call, without blocking UI.
            _ = Task.Run(() =>
            {
                lock (_nativeGate)
                {
                    if (ReferenceEquals(configuration, Volatile.Read(ref _configuration)))
                    {
                        CloseGpu();
                        if (flags == 0) _hasCpuSample = false;
                    }
                }
            });
        }
    }

    private void Sample()
    {
        var configuration = Volatile.Read(ref _configuration);
        if (_disposed || configuration.Flags == 0 || !Monitor.TryEnter(_nativeGate)) return;
        PerformanceSnapshot? snapshot = null;
        try
        {
            if (_disposed || !ReferenceEquals(configuration, Volatile.Read(ref _configuration))) return;
            if (_nativeVersion != configuration.Version)
            {
                CloseGpu();
                _hasCpuSample = false;
                _gpuUnavailable = false;
                _nativeVersion = configuration.Version;
                _lastSampleTimestamp = 0;
            }
            long now = Stopwatch.GetTimestamp();
            if (_lastSampleTimestamp != 0 && Stopwatch.GetElapsedTime(_lastSampleTimestamp, now) > TimeSpan.FromSeconds(8))
            {
                // After sleep or a blocked provider, establish fresh baselines
                // instead of presenting a long historical interval as current load.
                _hasCpuSample = _hasGpuSample = false;
            }
            _lastSampleTimestamp = now;
            double? cpu = (configuration.Flags & 1) != 0 ? ReadCpu() : null;
            double? gpu = (configuration.Flags & 2) != 0 ? ReadGpu() : null;
            (double? Percent, ulong Used, ulong Total) memory = (null, 0, 0);
            if ((configuration.Flags & 4) != 0)
            {
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref status)) memory = CalculateMemory(status.TotalPhysical, status.AvailablePhysical);
            }
            snapshot = new PerformanceSnapshot(cpu, gpu, memory.Percent, memory.Used, memory.Total);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            snapshot = PerformanceSnapshot.Empty;
            CloseGpu();
            _gpuUnavailable = true;
        }
        finally { Monitor.Exit(_nativeGate); }
        if (snapshot is null || _disposed || _dispatcher.HasShutdownStarted) return;
        var result = snapshot;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed && ReferenceEquals(configuration, Volatile.Read(ref _configuration))) Publish(result);
        }), DispatcherPriority.Background);
    }

    private void Publish(PerformanceSnapshot snapshot)
    {
        if (snapshot == Current) return;
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }

    private double? ReadCpu()
    {
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) { _hasCpuSample = false; return null; }
        double? percent = _hasCpuSample ? CalculateCpu(_idle, _kernel, _user, idle, kernel, user) : null;
        (_idle, _kernel, _user) = (idle, kernel, user);
        _hasCpuSample = true;
        return percent;
    }

    private double? ReadGpu()
    {
        if (_gpuUnavailable) return null;
        if (_query == IntPtr.Zero)
        {
            if (PdhOpenQuery(null, UIntPtr.Zero, out _query) != 0 ||
                PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out _gpuCounter) != 0)
            {
                CloseGpu();
                _gpuUnavailable = true;
                return null;
            }
        }
        if (PdhCollectQueryData(_query) != 0) { _hasGpuSample = false; return null; }
        if (!_hasGpuSample) { _hasGpuSample = true; return null; }
        const uint MoreData = 0x800007D2, FormatDoubleNoCap = 0x8200;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint bytes = 0;
            uint status = PdhGetFormattedCounterArray(_gpuCounter, FormatDoubleNoCap, ref bytes, out _, IntPtr.Zero);
            if (status != MoreData || bytes == 0 || bytes > 32 * 1024 * 1024) return null;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                uint allocated = bytes;
                status = PdhGetFormattedCounterArray(_gpuCounter, FormatDoubleNoCap, ref bytes, out uint count, buffer);
                if (status == MoreData) continue; // Instances changed between sizing and reading.
                int stride = Marshal.SizeOf<FormattedCounterItem>();
                if (status != 0 || count > allocated / stride) return null;
                var values = new List<(string Instance, double Utilization)>(checked((int)count));
                for (uint i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<FormattedCounterItem>(IntPtr.Add(buffer, checked((int)i * stride)));
                    if (item.Value.Status > 1 || item.Name == IntPtr.Zero) continue;
                    string? name = Marshal.PtrToStringUni(item.Name);
                    if (name is not null) values.Add((name, item.Value.Value));
                }
                return AggregateGpu(values);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    /// <summary>Kernel time includes idle time; totals use double to avoid ulong addition overflow.</summary>
    public static double? CalculateCpu(ulong previousIdle, ulong previousKernel, ulong previousUser, ulong idle, ulong kernel, ulong user)
    {
        if (idle < previousIdle || kernel < previousKernel || user < previousUser) return null;
        double total = (double)(kernel - previousKernel) + (user - previousUser);
        double idleDelta = idle - previousIdle;
        if (total <= 0 || idleDelta > total || !double.IsFinite(total)) return null;
        return Math.Clamp((total - idleDelta) / total * 100, 0, 100);
    }

    public static (double? Percent, ulong Used, ulong Total) CalculateMemory(ulong total, ulong available)
    {
        if (total == 0 || available > total) return (null, 0, 0);
        ulong used = total - available;
        return (Math.Clamp((double)used / total * 100, 0, 100), used, total);
    }

    /// <summary>Sum processes sharing an engine, then use the busiest engine across physical adapters.</summary>
    public static double? AggregateGpu(IEnumerable<(string Instance, double Utilization)> counters)
    {
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, utilization) in counters)
        {
            if (string.IsNullOrEmpty(instance) || !double.IsFinite(utilization) || utilization < 0) continue;
            var match = EngineName.Match(instance);
            if (!match.Success) continue;
            string key = match.Groups["luid"].Value + ":" + match.Groups["physical"].Value + ":" + match.Groups["engine"].Value;
            engines.TryGetValue(key, out double sum);
            engines[key] = Math.Min(100, sum + Math.Min(100, utilization));
        }
        if (engines.Count == 0) return null;
        double maximum = 0;
        foreach (double load in engines.Values) maximum = Math.Max(maximum, load);
        return maximum;
    }

    private void CloseGpu()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _gpuCounter = IntPtr.Zero;
        _hasGpuSample = false;
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        Volatile.Write(ref _configuration, new Configuration(++_version, 0));
        lock (_nativeGate) CloseGpu();
        Changed = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FormattedCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedCounterItem
    {
        public IntPtr Name;
        public FormattedCounterValue Value;
    }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
