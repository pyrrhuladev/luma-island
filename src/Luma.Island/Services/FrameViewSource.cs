using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Luma.Island.Services;

internal readonly record struct FrameViewMetric(ulong Timestamp, ulong ProcessId, ulong SwapChain, double Value);
internal sealed record FrameViewStatistics(FrameViewMetric[] Average, FrameViewMetric[] Low);
internal readonly record struct FrameViewFrame(ulong Timestamp, ulong ProcessId, ulong SwapChain, double Milliseconds, double DisplayMilliseconds);
internal sealed record FrameViewFrameBatch(FrameViewFrame[] Frames, bool HasGap);

internal interface IFrameViewSource : IDisposable
{
    bool LowSupported { get; }
    bool FramesSupported { get; }
    string LowStatus { get; }
    string FrameTimeStatus { get; }
    FrameViewStatistics ReadStatistics();
    FrameViewFrameBatch DrainFrames();
}

/// <summary>Existing signed SDK only. All methods, including disposal, are used by one collector worker.</summary>
internal sealed class FrameViewSource : IFrameViewSource
{
    internal const uint AverageMetric = 0x6C4B1348, LowMetric = 0x29CCDD99, FrameMetric = 0x5BE0DB9E;
    internal const int MetricSize = 56, MaximumMetricCount = 4096, MaximumFramesPerDrain = 4096, MaximumBytesPerDrain = 256 * 1024;
    private IntPtr _library, _session, _buffer;
    private int _capacity;
    private bool _initialized, _started, _disposed;
    private SafeFileHandle? _pipe;
    private byte[]? _pipeBuffer;
    private FrameViewCsvDecoder? _decoder;
    private readonly SimpleCall _shutdown = null!;
    private readonly SessionCall _destroy = null!, _stop = null!;
    private readonly EnableCall _enable = null!;
    private readonly SampleCall _sample = null!;
    private readonly ReadCall _read = null!;
    public bool LowSupported { get; private set; }
    public bool FramesSupported { get; private set; }
    public string LowStatus { get; private set; }
    public string FrameTimeStatus { get; private set; }

    public FrameViewSource(bool wantLow, bool wantFrames)
    {
        LowStatus = wantLow ? "Подключение 1% Low…" : "1% Low выключен";
        FrameTimeStatus = wantFrames ? "Подключение времени кадра…" : "График времени кадра выключен";
        if (!Environment.Is64BitProcess) throw new NotSupportedException("Нужна 64-разрядная версия приложения");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "FrameViewSDK");
        string path = Path.Combine(root, "SDK", "Public_Release", "FvSDK_x64.dll");
        if (!File.Exists(path)) path = Path.Combine(root, "bin", "FvSDK_x64.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("FrameView SDK не установлен");
        VerifyPublisher(path);
        try
        {
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
            _destroy = Bind<SessionCall>(1); _enable = Bind<EnableCall>(2); _sample = Bind<SampleCall>(3); _read = Bind<ReadCall>(4);
            var start = Bind<SessionCall>(5); _stop = Bind<SessionCall>(6);
            var initialize = Bind<SimpleCall>(7); _shutdown = Bind<SimpleCall>(8);
            IntPtr pipeFunction = wantFrames ? query(16) : IntPtr.Zero;
            PipeCall? getPipe = pipeFunction == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<PipeCall>(pipeFunction);
            Check(initialize(), "инициализация"); _initialized = true;
            Check(create(out _session), "создание сессии");
            uint status = start(_session);
            if (status is not 0 and not 0x14) Check(status, "запуск сбора");
            _started = true;
            ConfigureMetrics(wantLow, wantFrames && getPipe is not null);
            if (wantFrames && getPipe is null) FrameTimeStatus = "Установленная версия FrameView не поддерживает поток кадров.";
            if (FramesSupported)
            {
                status = getPipe!(_session, out IntPtr borrowed);
                if (status != 0 || borrowed == IntPtr.Zero || GetFileType(borrowed) != 3 ||
                    !DuplicateHandle(new IntPtr(-1), borrowed, new IntPtr(-1), out IntPtr duplicate, 0, false, 2))
                {
                    FramesSupported = false;
                    FrameTimeStatus = "Поток кадров FrameView недоступен.";
                    RestoreEnabledMetrics();
                }
                else
                {
                    // The SDK closes its original read handle at StopSession. Own only this duplicate.
                    _pipe = new SafeFileHandle(duplicate, ownsHandle: true);
                    _pipeBuffer = new byte[64 * 1024];
                    _decoder = new FrameViewCsvDecoder();
                    FrameTimeStatus = "Время между Present · NVIDIA FrameView";
                }
            }
        }
        catch { Dispose(); throw; }
    }

    private void ConfigureMetrics(bool wantLow, bool wantFrames)
    {
        var capabilities = EnableCapabilities(wantLow, wantFrames, metrics => _enable(_session, metrics, (uint)metrics.Length));
        LowSupported = capabilities.Low; FramesSupported = capabilities.Frames;
        if (wantLow) LowStatus = LowSupported ? "1% Low · NVIDIA FrameView" : "Установленная версия FrameView не поддерживает 1% Low.";
        if (wantFrames && !FramesSupported) FrameTimeStatus = "Установленная версия FrameView не поддерживает поток кадров.";
    }

    internal readonly record struct Capabilities(bool Low, bool Frames);
    internal static Capabilities EnableCapabilities(bool wantLow, bool wantFrames, Func<uint[], uint> enable)
    {
        // Every call replaces the entire SDK set, so finish by explicitly enabling the supported combination.
        if (enable(Metrics(wantLow, wantFrames)) == 0) return new(wantLow, wantFrames);
        bool low = wantLow && enable(Metrics(true, false)) == 0;
        bool frames = wantFrames && enable(Metrics(false, true)) == 0;
        if (enable(Metrics(low, frames)) == 0) return new(low, frames);
        // An older provider may support each optional metric but reject their combination. Preserve one rather than dropping both.
        if (low && enable(Metrics(true, false)) == 0) return new(true, false);
        if (frames && enable(Metrics(false, true)) == 0) return new(false, true);
        Check(enable(Metrics(false, false)), "подключение FPS");
        return new(false, false);
    }

    private static uint[] Metrics(bool low, bool frames) => low
        ? frames ? new[] { AverageMetric, LowMetric, FrameMetric } : new[] { AverageMetric, LowMetric }
        : frames ? new[] { AverageMetric, FrameMetric } : new[] { AverageMetric };

    private uint EnableCurrent()
    {
        uint[] metrics = Metrics(LowSupported, FramesSupported);
        return _enable(_session, metrics, (uint)metrics.Length);
    }

    private void RestoreEnabledMetrics()
    {
        uint status = EnableCurrent();
        if (status == 0) return;
        App.Log(new InvalidOperationException($"FrameView: не удалось убрать недоступную метрику, код {status}."));
    }

    public FrameViewStatistics ReadStatistics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        uint status = _sample(_session, 0); // Explicitly nonblocking SampleData.
        if (status == 3) return new(Array.Empty<FrameViewMetric>(), Array.Empty<FrameViewMetric>());
        Check(status, "чтение кадров");
        FrameViewMetric[] average = ReadMetric(AverageMetric);
        FrameViewMetric[] low = Array.Empty<FrameViewMetric>();
        if (LowSupported)
        {
            try { low = ReadMetric(LowMetric); }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
            {
                App.Log(exception);
                LowSupported = false;
                LowStatus = "Не удалось прочитать 1% Low из FrameView.";
                RestoreEnabledMetrics();
            }
        }
        return new(average, low);
    }

    private FrameViewMetric[] ReadMetric(uint metric)
    {
        var samples = new Samples { Version = 0x10018, Type = metric };
        uint status = _read(_session, ref samples, 1);
        if (status == 3) return Array.Empty<FrameViewMetric>();
        if (status is not 0 and not 2 and not 0x10) Check(status, "размер выборки FPS");
        ulong count = samples.Count.ToUInt64();
        if (count == 0) return Array.Empty<FrameViewMetric>();
        if (count > MaximumMetricCount) throw new InvalidDataException("Некорректный размер выборки FrameView");
        EnsureCapacity((int)count);
        samples.Data = _buffer;
        samples.Count = (UIntPtr)(uint)_capacity;
        status = _read(_session, ref samples, 1);
        if (status is not 0 and not 2) Check(status, "значения FPS");
        count = samples.Count.ToUInt64();
        if (count > (ulong)_capacity) throw new InvalidDataException("Переполнение выборки FrameView");
        var result = new FrameViewMetric[(int)count];
        for (int index = 0; index < result.Length; index++)
            result[index] = DecodeMetric(Marshal.PtrToStructure<NativeMetric>(IntPtr.Add(_buffer, index * MetricSize)));
        return result;
    }

    internal static FrameViewMetric DecodeMetric(NativeMetric value)
    {
        if (value.BaseVersion != 0x10010 || value.Version != 0x10038 || value.FpsVersion != 0x10020)
            throw new InvalidDataException("Неизвестный формат метрик FrameView");
        return new(value.Timestamp, value.ProcessId, value.SwapChain, value.Fps);
    }

    private void EnsureCapacity(int count)
    {
        if (_capacity >= count) return;
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero; _capacity = 0;
        _buffer = Marshal.AllocHGlobal(checked(count * MetricSize)); _capacity = count;
        for (int index = 0; index < count; index++)
            Marshal.StructureToPtr(new NativeMetric { BaseVersion = 0x10010, Version = 0x10038, FpsVersion = 0x10020 }, IntPtr.Add(_buffer, index * MetricSize), false);
    }

    public FrameViewFrameBatch DrainFrames()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!FramesSupported || _pipe is null || _pipe.IsClosed || _pipeBuffer is not { } buffer || _decoder is not { } decoder)
            return new(Array.Empty<FrameViewFrame>(), false);
        List<FrameViewFrame>? frames = null;
        bool gap = false;
        int budget = MaximumBytesPerDrain;
        while (budget > 0)
        {
            if (!PeekNamedPipe(_pipe, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
                return FramePipeFailed();
            if (available == 0) break;
            int requested = (int)Math.Min(available, (uint)Math.Min(budget, buffer.Length));
            bool read = ReadFile(_pipe, buffer, (uint)requested, out uint actual, IntPtr.Zero);
            int error = read ? 0 : Marshal.GetLastWin32Error();
            if (!IsReadablePipeChunk(read, actual, (uint)requested, error))
                return FramePipeFailed();
            budget -= (int)actual;
            frames ??= new List<FrameViewFrame>(128);
            decoder.Feed(buffer.AsSpan(0, (int)actual), frames, ref gap, MaximumFramesPerDrain);
        }
        return new(frames?.ToArray() ?? Array.Empty<FrameViewFrame>(), gap);
    }

    internal static bool IsReadablePipeChunk(bool success, uint actual, uint requested, int error) =>
        actual > 0 && actual <= requested && (success || error == 234); // ERROR_MORE_DATA is a valid partial message, not stream loss.

    private FrameViewFrameBatch FramePipeFailed()
    {
        FramesSupported = false;
        FrameTimeStatus = "Поток кадров FrameView закрыт. Выключите и включите график для повторного подключения.";
        _pipe?.Dispose(); _pipe = null;
        _decoder?.Reset(); _decoder = null; _pipeBuffer = null;
        RestoreEnabledMetrics();
        return new(Array.Empty<FrameViewFrame>(), true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe?.Dispose(); _pipe = null;
        _decoder = null; _pipeBuffer = null;
        if (_session != IntPtr.Zero)
        {
            if (_started) _stop?.Invoke(_session);
            _destroy?.Invoke(_session); _session = IntPtr.Zero;
        }
        if (_initialized) { _shutdown?.Invoke(); _initialized = false; }
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; _capacity = 0; }
        if (_library != IntPtr.Zero) { FreeLibrary(_library); _library = IntPtr.Zero; }
    }

    private static void Check(uint status, string operation)
    {
        if (status != 0) throw new InvalidOperationException($"FrameView: {operation}, код {status}");
    }

    private static void VerifyPublisher(string path)
    {
#pragma warning disable SYSLIB0057
        using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        if (!certificate.Subject.Contains("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Издатель FrameView SDK не подтверждён");
        IntPtr info = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path }, info, false);
            var trust = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = info, ProviderFlags = 0x1000 };
            Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref trust) != 0) throw new InvalidDataException("Цифровая подпись FrameView SDK недействительна");
        }
        finally { Marshal.DestroyStructure<TrustFile>(info); Marshal.FreeHGlobal(info); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Samples { public uint Version, Type; public UIntPtr Count; public IntPtr Data; }
    [StructLayout(LayoutKind.Explicit, Size = MetricSize)]
    internal struct NativeMetric
    {
        [FieldOffset(0)] public uint BaseVersion;
        [FieldOffset(8)] public ulong Timestamp;
        [FieldOffset(16)] public uint Version;
        [FieldOffset(24)] public uint FpsVersion;
        [FieldOffset(32)] public ulong ProcessId;
        [FieldOffset(40)] public ulong SwapChain;
        [FieldOffset(48)] public double Fps;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint PipeCall(IntPtr session, out IntPtr pipe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr library);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr file);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekNamedPipe(SafeFileHandle pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr remaining);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(SafeFileHandle file, [Out] byte[] buffer, uint count, out uint actual, IntPtr overlapped);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}

/// <summary>Bounded ASCII CSV stream parser: each feed may end anywhere, including inside a number.</summary>
internal sealed class FrameViewCsvDecoder
{
    internal const int MaximumLineBytes = 1024;
    private readonly byte[] _line = new byte[MaximumLineBytes];
    private int _length;
    private bool _discard;
    public void Reset() { _length = 0; _discard = false; }

    internal void Feed(ReadOnlySpan<byte> bytes, List<FrameViewFrame> frames, ref bool hasGap, int maximumFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrames);
        foreach (byte value in bytes)
        {
            if (value == (byte)'\n')
            {
                if (!_discard)
                {
                    var line = Trim(_line.AsSpan(0, _length));
                    if (!line.IsEmpty && !line.StartsWith("ProcessId,"u8))
                    {
                        if (TryParse(line, out FrameViewFrame frame))
                        {
                            if (frames.Count == maximumFrames) { frames.Clear(); hasGap = true; }
                            frames.Add(frame);
                        }
                        else { frames.Clear(); hasGap = true; }
                    }
                }
                _length = 0; _discard = false;
            }
            else if (!_discard)
            {
                if (_length == MaximumLineBytes) { _discard = true; _length = 0; frames.Clear(); hasGap = true; }
                else _line[_length++] = value;
            }
        }
    }

    internal static bool TryParse(ReadOnlySpan<byte> line, out FrameViewFrame frame)
    {
        frame = default;
        Span<Range> fields = stackalloc Range[6];
        int count = 0, start = 0;
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] != (byte)',') continue;
            if (count == 5) return false;
            fields[count++] = new Range(start, index);
            start = index + 1;
        }
        if (count != 5) return false;
        fields[5] = new Range(start, line.Length);
        if (!UInt(line[fields[0]], out ulong processId) || processId == 0 || processId > uint.MaxValue ||
            !UInt(line[fields[1]], out ulong swapChain) ||
            !UInt(line[fields[2]], out ulong timestamp) || timestamp == 0 || timestamp > long.MaxValue ||
            !Milliseconds(line[fields[3]], out double present) || !Milliseconds(line[fields[4]], out double display)) return false;
        frame = new(timestamp, processId, swapChain, present, display);
        return true;
    }

    private static bool UInt(ReadOnlySpan<byte> bytes, out ulong value)
    {
        bytes = Trim(bytes);
        if (bytes.StartsWith("0x"u8) || bytes.StartsWith("0X"u8)) { bytes = bytes[2..]; return Utf8Parser.TryParse(bytes, out value, out int length, 'X') && length == bytes.Length; }
        return Utf8Parser.TryParse(bytes, out value, out int consumed) && consumed == bytes.Length;
    }
    private static bool Milliseconds(ReadOnlySpan<byte> bytes, out double value)
    {
        bytes = Trim(bytes);
        return Utf8Parser.TryParse(bytes, out value, out int consumed) && consumed == bytes.Length && double.IsFinite(value) && value >= 0 && value <= 60_000;
    }
    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("\uFEFF"u8)) bytes = bytes[3..];
        while (!bytes.IsEmpty && bytes[0] is (byte)' ' or (byte)'\r' or (byte)'\t') bytes = bytes[1..];
        while (!bytes.IsEmpty && bytes[^1] is (byte)' ' or (byte)'\r' or (byte)'\t') bytes = bytes[..^1];
        return bytes;
    }
}
