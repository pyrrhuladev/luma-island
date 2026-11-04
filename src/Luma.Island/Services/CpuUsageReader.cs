using System;
using System.Runtime.InteropServices;

namespace Luma.Island.Services;

public sealed record CpuUsageReading(double? Percent, string Source);

/// <summary>
/// Reads the frequency-aware total CPU utility, with an explicitly labelled busy-time fallback
/// when the counter cannot be opened. Call Read/Reset/Dispose from the sampling worker: PDH
/// is synchronous. This class does not create threads, timers or performance-counter services.
/// </summary>
public sealed class CpuUsageReader : IDisposable
{
    public const string UtilitySource = "Windows · % Processor Utility (_Total), с учётом частоты CPU";
    public const string BusySource = "Windows · GetSystemTimes (занятость CPU, без учёта частоты)";
    internal const string CounterPath = @"\Processor Information(_Total)\% Processor Utility";
    internal const uint DoubleNoCap = 0x00000200 | 0x00008000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
    internal const long RetryMilliseconds = 30_000;
    private const uint InvalidHandle = 0xC0000BBC;
    private readonly object _gate = new();
    private readonly ICpuUsageApi _api;
    private readonly Func<long> _clock;
    private IntPtr _query, _counter;
    private bool _utilityBaseline, _busyBaseline, _disposed;
    private SystemTimes _previousTimes;
    private long _retryAt;
    private string _unavailableReason = "Счётчик % Processor Utility недоступен";

    public CpuUsageReader() : this(new NativeCpuUsageApi(), () => Environment.TickCount64) { }
    internal CpuUsageReader(ICpuUsageApi api, Func<long>? clock = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public CpuUsageReading Read()
    {
        lock (_gate)
        {
            if (_disposed) return new(null, "Мониторинг CPU выключен");
            if (_query == IntPtr.Zero && !TryOpenUtility()) return ReadBusyFallback();
            _busyBaseline = false;
            try
            {
                uint status = _api.Collect(_query);
                if (status != 0) return InvalidUtility(status, 0);
                if (!_utilityBaseline)
                {
                    _utilityBaseline = true;
                    return new(null, UtilitySource + " · ожидание второго отсчёта");
                }
                status = _api.GetFormattedValue(_counter, DoubleNoCap, out uint dataStatus, out double value);
                if (status != 0 || dataStatus > 1 || !double.IsFinite(value) || value < 0)
                    return InvalidUtility(status, dataStatus);
                // _Total is already the average across the logical CPUs. Utility includes
                // performance above the nominal frequency, so valid values may exceed 100.
                return new(value, UtilitySource);
            }
            catch (Exception exception) when (IsNativeUnavailable(exception))
            {
                CloseUtility();
                _unavailableReason = "Счётчик % Processor Utility недоступен через API Windows";
                _retryAt = _clock() + RetryMilliseconds;
                return ReadBusyFallback();
            }
        }
    }

    private bool TryOpenUtility()
    {
        if (_clock() < _retryAt) return false;
        try
        {
            uint status = _api.OpenQuery(out _query);
            if (status != 0 || _query == IntPtr.Zero) return UtilityUnavailable(status);
            status = _api.AddEnglishCounter(_query, CounterPath, out _counter);
            if (status != 0 || _counter == IntPtr.Zero) return UtilityUnavailable(status);
            _utilityBaseline = false;
            return true;
        }
        catch (Exception exception) when (IsNativeUnavailable(exception))
        {
            CloseUtility();
            _unavailableReason = "Счётчик % Processor Utility недоступен через API Windows";
            _retryAt = _clock() + RetryMilliseconds;
            return false;
        }
    }

    private bool UtilityUnavailable(uint status)
    {
        CloseUtility();
        _unavailableReason = "Счётчик % Processor Utility недоступен (PDH 0x" + status.ToString("X8") + ")";
        _retryAt = _clock() + RetryMilliseconds;
        return false;
    }

    private CpuUsageReading InvalidUtility(uint status, uint dataStatus)
    {
        // Missing or invalid data from a working counter must not silently change the
        // metric to busy time. Re-establish two fresh samples after a transient failure.
        _utilityBaseline = false;
        if (status == InvalidHandle || dataStatus == InvalidHandle)
        {
            CloseUtility();
            _retryAt = 0;
        }
        return new(null, UtilitySource + " · нет корректного отсчёта (PDH 0x" + status.ToString("X8")
            + ", данные 0x" + dataStatus.ToString("X8") + ")");
    }

    private CpuUsageReading ReadBusyFallback()
    {
        string source = BusySource + " · резервный источник: " + _unavailableReason;
        if (!_api.TryGetSystemTimes(out SystemTimes current))
        {
            _busyBaseline = false;
            return new(null, source + " · данные Windows недоступны");
        }
        double? value = _busyBaseline ? CalculateBusy(_previousTimes.Idle, _previousTimes.Kernel, _previousTimes.User,
            current.Idle, current.Kernel, current.User) : null;
        _previousTimes = current;
        _busyBaseline = true;
        return new(value, value.HasValue ? source : source + " · ожидание корректного интервала");
    }

    /// <summary>GetSystemTimes kernel time includes idle; this measures busy time, not processor utility.</summary>
    public static double? CalculateBusy(ulong previousIdle, ulong previousKernel, ulong previousUser, ulong idle, ulong kernel, ulong user)
    {
        if (idle < previousIdle || kernel < previousKernel || user < previousUser) return null;
        double total = (double)(kernel - previousKernel) + (user - previousUser);
        double idleDelta = idle - previousIdle;
        if (total <= 0 || idleDelta > total || !double.IsFinite(total)) return null;
        return Math.Clamp((total - idleDelta) / total * 100, 0, 100);
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ResetCore();
        }
    }
    private void ResetCore()
    {
        CloseUtility();
        _busyBaseline = false;
        _previousTimes = default;
        _retryAt = 0;
    }
    private void CloseUtility()
    {
        IntPtr query = _query;
        _query = _counter = IntPtr.Zero;
        _utilityBaseline = false;
        if (query == IntPtr.Zero) return;
        try { _api.CloseQuery(query); }
        catch (Exception exception) when (IsNativeUnavailable(exception)) { }
    }
    private static bool IsNativeUnavailable(Exception exception) => exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ResetCore();
        }
    }

    internal readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);
    internal interface ICpuUsageApi
    {
        uint OpenQuery(out IntPtr query);
        uint AddEnglishCounter(IntPtr query, string path, out IntPtr counter);
        uint Collect(IntPtr query);
        uint GetFormattedValue(IntPtr counter, uint format, out uint dataStatus, out double value);
        uint CloseQuery(IntPtr query);
        bool TryGetSystemTimes(out SystemTimes times);
    }
    private sealed class NativeCpuUsageApi : ICpuUsageApi
    {
        public uint OpenQuery(out IntPtr query) => PdhOpenQuery(null, UIntPtr.Zero, out query);
        public uint AddEnglishCounter(IntPtr query, string path, out IntPtr counter) => PdhAddEnglishCounter(query, path, UIntPtr.Zero, out counter);
        public uint Collect(IntPtr query) => PdhCollectQueryData(query);
        public uint GetFormattedValue(IntPtr counter, uint format, out uint dataStatus, out double value)
        {
            uint status = PdhGetFormattedCounterValue(counter, format, out _, out FormattedCounterValue result);
            dataStatus = result.Status;
            value = result.Value;
            return status;
        }
        public uint CloseQuery(IntPtr query) => PdhCloseQuery(query);
        public bool TryGetSystemTimes(out SystemTimes times)
        {
            bool success = GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
            times = new(idle, kernel, user);
            return success;
        }
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FormattedCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQuery(string? source, UIntPtr userData, out IntPtr query);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhCollectQueryData(IntPtr query);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FormattedCounterValue value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhCloseQuery(IntPtr query);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
