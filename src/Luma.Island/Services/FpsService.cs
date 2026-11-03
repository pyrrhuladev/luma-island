using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Luma.Island.Services;

public sealed record FpsSnapshot(double? Fps, string? ProcessName, string Status);

/// <summary>
/// Optional real application FPS from an existing, signed NVIDIA FrameView SDK.
/// The SDK owns its capture helper; no collector, timer, or session is retained when disabled.
/// </summary>
public sealed class FpsService : IDisposable
{
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private FpsSnapshot _current = new(null, null, "FPS выключен");
    private bool _enabled;
    private volatile bool _disposed;
    private int _generation;
    private int _running;

    public FpsSnapshot Current => Volatile.Read(ref _current);
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    /// <summary>Await after disabling for deterministic session cleanup.</summary>
    public Task Completion => _worker;
    /// <summary>Always raised on the WPF application dispatcher.</summary>
    public event Action<FpsSnapshot>? Changed;

    public void Configure(bool enabled)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enabled == enabled) return;
        _enabled = enabled;
        int generation = Interlocked.Increment(ref _generation);
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        if (!enabled)
        {
            Volatile.Write(ref _running, 0);
            PublishOnDispatcher(new FpsSnapshot(null, null, "FPS выключен"), generation);
            return;
        }

        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        _cancellation = cancellation;
        Task previous = _worker;
        Volatile.Write(ref _running, 1);
        PublishOnDispatcher(new FpsSnapshot(null, null, "Подключение источника FPS…"), generation);
        _worker = Task.Run(async () =>
        {
            // Never overlap SDK initialization/shutdown during rapid toggles.
            try { await previous.ConfigureAwait(false); }
            catch (Exception ex) { App.Log(ex); }
            await CollectAsync(generation, token).ConfigureAwait(false);
        });
    }

    private async Task CollectAsync(int generation, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var source = new FrameViewSource();
            uint lastTarget = 0, cachedTarget = 0;
            string? lastTargetName = null;
            Metric? cached = null;
            ulong? preferredSwapChain = null;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                GetWindowThreadProcessId(GetForegroundWindow(), out uint target);
                string? name;
                if (target == (uint)Environment.ProcessId)
                {
                    target = lastTarget;
                    name = lastTargetName;
                }
                else
                {
                    name = GetProcessName(target);
                    if (IsExcluded(name)) { target = 0; name = null; }
                    lastTarget = target;
                    lastTargetName = name;
                }

                if (target != cachedTarget)
                {
                    cachedTarget = target;
                    cached = null;
                    preferredSwapChain = null;
                }

                Metric? selected = null;
                foreach (Metric sample in source.Read())
                {
                    if (sample.ProcessId != target || !IsFresh(sample) ||
                        !double.IsFinite(sample.Fps) || sample.Fps <= 0 || sample.Fps > 20000) continue;
                    bool preferred = preferredSwapChain.HasValue && sample.SwapChain == preferredSwapChain.Value;
                    bool currentPreferred = selected.HasValue && preferredSwapChain.HasValue &&
                        selected.Value.SwapChain == preferredSwapChain.Value;
                    if (!selected.HasValue || (preferred && !currentPreferred) ||
                        (preferred == currentPreferred && (sample.Timestamp > selected.Value.Timestamp ||
                        (sample.Timestamp == selected.Value.Timestamp && sample.Fps > selected.Value.Fps))))
                        selected = sample;
                }

                if (selected.HasValue)
                {
                    cached = selected;
                    preferredSwapChain = selected.Value.SwapChain;
                }

                if (target != 0 && cached.HasValue && IsFresh(cached.Value))
                    Publish(new FpsSnapshot(cached.Value.Fps, name, "FPS приложения · NVIDIA FrameView"), generation);
                else
                    Publish(new FpsSnapshot(null, name, target == 0
                        ? "Откройте игру или приложение"
                        : "Ожидание кадров приложения"), generation);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (FileNotFoundException)
        {
            Publish(new FpsSnapshot(null, null, "Для FPS нужен NVIDIA FrameView SDK"), generation);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Publish(new FpsSnapshot(null, null, "Не удалось подключить счётчик FPS. Попробуйте выключить и включить FPS."), generation);
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation)) Volatile.Write(ref _running, 0);
        }
    }

    private static bool IsFresh(Metric metric)
    {
        long now = Stopwatch.GetTimestamp();
        double age = (now - (double)metric.Timestamp) / Stopwatch.Frequency;
        return metric.Timestamp != 0 && age >= -.25 && age <= 3;
    }

    private static string? GetProcessName(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue) return null;
        try { using var process = Process.GetProcessById((int)processId); return process.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    private static bool IsExcluded(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return name.ToLowerInvariant() is "luma.island" or "chrome" or "msedge" or "firefox" or "brave" or "opera" or
            "vivaldi" or "browser" or "codex" or "dwm" or "explorer" or "applicationframehost" or
            "shellexperiencehost" or "startmenuexperiencehost" or "searchhost" or "textinputhost" or
            "systemsettings" or "gamebar" or "gamebarftserver" or "nvidia overlay" or "nvidiaoverlay" or
            "nvcontainer" or "rtss" or "presentmon" or "presentmon_x64" or "fvcontainer" or "nvfvsdksvc_x64";
    }

    private void Publish(FpsSnapshot snapshot, int generation)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _ = _dispatcher.BeginInvoke(new Action(() => PublishOnDispatcher(snapshot, generation)), DispatcherPriority.Background);
    }

    private void PublishOnDispatcher(FpsSnapshot snapshot, int generation)
    {
        if (_disposed || generation != Volatile.Read(ref _generation)) return;
        Volatile.Write(ref _current, snapshot);
        Changed?.Invoke(snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _running, 0);
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        Changed = null;
    }

    private readonly record struct Metric(ulong Timestamp, ulong ProcessId, ulong SwapChain, double Fps);

    /// <summary>
    /// ABI verified against the locally installed NVIDIA SDK/Public_Release/FvSDK.h
    /// and its official FvSDK_x64.lib wrapper. No NVIDIA files are redistributed.
    /// </summary>
    private sealed class FrameViewSource : IDisposable
    {
        private const uint AvgFpsMetric = 0x6C4B1348;
        private const int MetricSize = 56;
        private IntPtr _library, _session, _buffer;
        private int _capacity;
        private bool _initialized, _started;
        private readonly SimpleCall _shutdown = null!;
        private readonly SessionCall _destroy = null!, _stop = null!;
        private readonly SampleCall _sample = null!;
        private readonly ReadCall _read = null!;

        public FrameViewSource()
        {
            if (!Environment.Is64BitProcess) throw new NotSupportedException("Нужна 64-разрядная версия приложения");
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "FrameViewSDK");
            string path = Path.Combine(root, "SDK", "Public_Release", "FvSDK_x64.dll");
            if (!File.Exists(path)) path = Path.Combine(root, "bin", "FvSDK_x64.dll");
            if (!File.Exists(path)) throw new FileNotFoundException("FrameView SDK не установлен");
            VerifyPublisher(path);
            try
            {
                // Dependencies are restricted to this protected directory and System32.
                _library = LoadLibraryExW(path, IntPtr.Zero, 0x100 | 0x800);
                if (_library == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var query = Marshal.GetDelegateForFunctionPointer<QueryCall>(NativeLibrary.GetExport(_library, "fv_QueryInterface"));
                T Bind<T>(uint id) where T : Delegate
                {
                    IntPtr function = query(id);
                    if (function == IntPtr.Zero) throw new NotSupportedException("Версия FrameView SDK не поддерживается");
                    return Marshal.GetDelegateForFunctionPointer<T>(function);
                }

                var create = Bind<CreateCall>(0);
                _destroy = Bind<SessionCall>(1);
                var enable = Bind<EnableCall>(2);
                _sample = Bind<SampleCall>(3);
                _read = Bind<ReadCall>(4);
                var start = Bind<SessionCall>(5);
                _stop = Bind<SessionCall>(6);
                var initialize = Bind<SimpleCall>(7);
                _shutdown = Bind<SimpleCall>(8);
                Check(initialize(), "инициализация");
                _initialized = true;
                Check(create(out _session), "создание сессии");
                uint status = start(_session);
                // The installed SDK reports SESSION_IN_PROGRESS for its already-running capture.
                if (status is not 0 and not 0x14) Check(status, "запуск сбора");
                _started = true;
                Check(enable(_session, new[] { AvgFpsMetric }, 1), "подключение FPS");
            }
            catch { Dispose(); throw; }
        }

        public Metric[] Read()
        {
            uint status = _sample(_session, 0); // FVSDK_SAMPLEDATA_DONOTWAIT
            if (status == 3) return Array.Empty<Metric>(); // FV_NO_DATA
            Check(status, "чтение кадров");
            var samples = new Samples { Version = 0x10018, Type = AvgFpsMetric };
            status = _read(_session, ref samples, 1);
            if (status is not 0 and not 2 and not 0x10) Check(status, "размер выборки FPS");
            ulong count = samples.Count.ToUInt64();
            if (count == 0) return Array.Empty<Metric>();
            if (count > 4096) throw new InvalidDataException("Некорректный размер выборки FrameView");
            EnsureCapacity((int)count);
            samples.Data = _buffer;
            status = _read(_session, ref samples, 1);
            if (status is not 0 and not 2) Check(status, "значения FPS");
            count = samples.Count.ToUInt64();
            if (count > (ulong)_capacity) return Array.Empty<Metric>();
            var result = new Metric[(int)count];
            for (int index = 0; index < result.Length; index++)
            {
                var value = Marshal.PtrToStructure<AvgFps>(IntPtr.Add(_buffer, index * MetricSize));
                if (value.BaseVersion != 0x10010 || value.Version != 0x10038 || value.FpsVersion != 0x10020)
                    throw new InvalidDataException("Неизвестный формат метрик FrameView");
                result[index] = new Metric(value.Timestamp, value.ProcessId, value.SwapChain, value.Fps);
            }
            return result;
        }

        private void EnsureCapacity(int count)
        {
            if (_capacity >= count) return;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _capacity = 0;
            _buffer = Marshal.AllocHGlobal(checked(count * MetricSize));
            _capacity = count;
            for (int index = 0; index < count; index++)
            {
                IntPtr item = IntPtr.Add(_buffer, index * MetricSize);
                Marshal.StructureToPtr(new AvgFps { BaseVersion = 0x10010, Version = 0x10038, FpsVersion = 0x10020 }, item, false);
            }
        }

        private static void Check(uint status, string operation)
        {
            if (status != 0) throw new InvalidOperationException($"FrameView: {operation}, код {status}");
        }

        private static void VerifyPublisher(string path)
        {
            // The certificate loaders accept certificate files, not Authenticode PE signers.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            if (!certificate.Subject.Contains("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Издатель FrameView SDK не подтверждён");
            IntPtr info = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
            try
            {
                Marshal.StructureToPtr(new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path }, info, false);
                var trust = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2,
                    UnionChoice = 1, File = info, ProviderFlags = 0x1000 };
                Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                if (WinVerifyTrust(new IntPtr(-1), ref action, ref trust) != 0)
                    throw new InvalidDataException("Цифровая подпись FrameView SDK недействительна");
            }
            finally
            {
                Marshal.DestroyStructure<TrustFile>(info);
                Marshal.FreeHGlobal(info);
            }
        }

        public void Dispose()
        {
            if (_session != IntPtr.Zero)
            {
                if (_started) _stop?.Invoke(_session);
                _destroy?.Invoke(_session);
                _session = IntPtr.Zero;
            }
            if (_initialized) { _shutdown?.Invoke(); _initialized = false; }
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; _capacity = 0; }
            if (_library != IntPtr.Zero) { FreeLibrary(_library); _library = IntPtr.Zero; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Samples { public uint Version, Type; public UIntPtr Count; public IntPtr Data; }
        [StructLayout(LayoutKind.Explicit, Size = MetricSize)]
        private struct AvgFps
        {
            [FieldOffset(0)] public uint BaseVersion;
            [FieldOffset(8)] public ulong Timestamp;
            [FieldOffset(16)] public uint Version;
            [FieldOffset(24)] public uint FpsVersion;
            [FieldOffset(32)] public ulong ProcessId;
            [FieldOffset(40)] public ulong SwapChain;
            [FieldOffset(48)] public double Fps;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File, KnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint Size; public IntPtr PolicyCallback, SipClient;
            public uint UiChoice, RevocationChecks, UnionChoice; public IntPtr File;
            public uint StateAction; public IntPtr StateData, UrlReference;
            public uint ProviderFlags, UiContext; public IntPtr SignatureSettings;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryCall(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint SimpleCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CreateCall(out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint SessionCall(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint EnableCall(IntPtr session, [In] uint[] metrics, uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint SampleCall(IntPtr session, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ReadCall(IntPtr session, ref Samples samples, uint count);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr library);
        [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
