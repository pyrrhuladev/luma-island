using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Luma.Island.Services;

public readonly record struct FrameTimeSample(double TimeSeconds, double Milliseconds);
public sealed record FpsSnapshot(double? Fps, string? ProcessName, string Status,
    double? OnePercentLow = null, double? FrameTimeMilliseconds = null, FrameTimeSample[]? Frames = null,
    string? StreamId = null, string? OnePercentLowStatus = null, string? FrameTimeStatus = null, bool HasFrameGap = false);

/// <summary>One optional SDK session for FPS, native 1% Low and individual Present intervals.</summary>
public sealed class FpsService : IDisposable
{
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly Func<bool, bool, IFrameViewSource> _sourceFactory;
    private readonly Func<uint> _foreground;
    private readonly Func<uint, string?> _processName;
    private readonly TimeSpan _statisticsInterval, _framePollInterval, _publishInterval;
    private readonly object _publishGate = new();
    private FpsSnapshot? _pendingSnapshot;
    private int _pendingGeneration;
    private bool _publishQueued;
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private FpsSnapshot _current = Empty("FPS выключен", false, false);
    private Options _options;
    private volatile bool _disposed;
    private int _generation, _running;
    private readonly record struct Options(bool Fps, bool Low, bool Frames) { public bool Enabled => Fps || Low || Frames; }
    public FpsSnapshot Current => Volatile.Read(ref _current);
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public Task Completion => _worker;
    public event Action<FpsSnapshot>? Changed;

    public FpsService() : this((low, frames) => new FrameViewSource(low, frames), GetForegroundProcess, GetProcessName) { }
    internal FpsService(Func<bool, bool, IFrameViewSource> sourceFactory, Func<uint> foreground, Func<uint, string?> processName,
        TimeSpan? statisticsInterval = null, TimeSpan? framePollInterval = null, TimeSpan? publishInterval = null)
    {
        _sourceFactory = sourceFactory; _foreground = foreground; _processName = processName;
        _statisticsInterval = statisticsInterval ?? TimeSpan.FromSeconds(1);
        _framePollInterval = framePollInterval ?? TimeSpan.FromMilliseconds(100);
        _publishInterval = publishInterval ?? TimeSpan.FromMilliseconds(250);
        if (_statisticsInterval <= TimeSpan.Zero || _framePollInterval <= TimeSpan.Zero || _publishInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(statisticsInterval));
    }

    public void Configure(bool fps, bool onePercentLow = false, bool frameTime = false)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var options = new Options(fps, onePercentLow, frameTime);
        if (_options == options) return;
        _options = options;
        int generation = Interlocked.Increment(ref _generation);
        lock (_publishGate) _pendingSnapshot = null;
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
        if (!options.Enabled)
        {
            Volatile.Write(ref _running, 0);
            PublishOnDispatcher(Empty("FPS выключен", false, false), generation);
            return;
        }
        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        _cancellation = cancellation;
        Task previous = _worker;
        Volatile.Write(ref _running, 1);
        PublishOnDispatcher(Empty("Подключение источника кадров…", options.Low, options.Frames), generation);
        _worker = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); } catch (Exception exception) { App.Log(exception); }
            await CollectAsync(options, generation, token).ConfigureAwait(false);
        });
    }

    private async Task CollectAsync(Options options, int generation, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var source = _sourceFactory(options.Low, options.Frames);
            var state = new FrameTimingState(Stopwatch.Frequency, Guid.NewGuid().ToString("N"));
            uint lastRaw = uint.MaxValue, lastTarget = 0;
            string? lastName = null, lastRawName = null;
            double nextStatistics = 0, nextPublish = 0, nextNameRetry = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                uint raw = _foreground();
                if (raw != (uint)Environment.ProcessId && (raw != lastRaw || lastRawName is null && now >= nextNameRetry))
                {
                    lastRaw = raw;
                    string? name = _processName(raw);
                    lastRawName = name;
                    nextNameRetry = now + _statisticsInterval.TotalSeconds;
                    lastTarget = IsExcluded(name) ? 0 : raw;
                    lastName = lastTarget == 0 ? null : name;
                }
                if (state.SetTarget(lastTarget, lastName)) nextStatistics = 0;
                if (now >= nextStatistics)
                {
                    state.ObserveStatistics(source.ReadStatistics(), now);
                    nextStatistics = now + _statisticsInterval.TotalSeconds;
                }
                if (options.Frames && source.FramesSupported) state.ObserveFrames(source.DrainFrames(), now);
                bool available = options.Fps || options.Low && source.LowSupported || options.Frames && source.FramesSupported;
                if (now >= nextPublish || !available)
                {
                    Publish(state.TakeSnapshot(options.Fps, options.Low, options.Frames, source, now), generation);
                    nextPublish = now + (options.Frames && source.FramesSupported ? _publishInterval : _statisticsInterval).TotalSeconds;
                }
                if (!available) return;
                await Task.Delay(options.Frames && source.FramesSupported ? _framePollInterval : _statisticsInterval, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (FileNotFoundException)
        {
            Publish(Empty("Для игровых показателей нужен NVIDIA FrameView SDK", options.Low, options.Frames), generation);
        }
        catch (Exception exception)
        {
            App.Log(exception);
            Publish(Empty("Не удалось подключить счётчик кадров. Выключите и включите игровые показатели.", options.Low, options.Frames), generation);
        }
        finally { if (generation == Volatile.Read(ref _generation)) Volatile.Write(ref _running, 0); }
    }

    private static FpsSnapshot Empty(string status, bool low, bool frames) => new(null, null, status,
        OnePercentLowStatus: low ? status : "1% Low выключен", FrameTimeStatus: frames ? status : "График времени кадра выключен");

    private static uint GetForegroundProcess() { GetWindowThreadProcessId(GetForegroundWindow(), out uint process); return process; }
    private static string? GetProcessName(uint process)
    {
        if (process == 0 || process > int.MaxValue) return null;
        try { using var item = Process.GetProcessById((int)process); return item.ProcessName; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }
    private static bool IsExcluded(string? name) => string.IsNullOrWhiteSpace(name) || name.ToLowerInvariant() is
        "luma.island" or "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi" or "browser" or "codex" or
        "dwm" or "explorer" or "applicationframehost" or "shellexperiencehost" or "startmenuexperiencehost" or "searchhost" or "textinputhost" or
        "systemsettings" or "gamebar" or "gamebarftserver" or "nvidia overlay" or "nvidiaoverlay" or "nvcontainer" or "rtss" or
        "presentmon" or "presentmon_x64" or "fvcontainer" or "nvfvsdksvc_x64";

    private void Publish(FpsSnapshot snapshot, int generation)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || generation != Volatile.Read(ref _generation)) return;
        lock (_publishGate)
        {
            if (_disposed || generation != Volatile.Read(ref _generation)) return;
            if (_pendingSnapshot is { } previous && _pendingGeneration == generation &&
                string.Equals(previous.StreamId, snapshot.StreamId, StringComparison.Ordinal) && !snapshot.HasFrameGap)
            {
                int oldCount = previous.Frames?.Length ?? 0, newCount = snapshot.Frames?.Length ?? 0;
                int retained = Math.Min(4096, oldCount + newCount);
                FrameTimeSample[]? merged = retained == 0 ? null : new FrameTimeSample[retained];
                if (merged is not null)
                {
                    int keepNew = Math.Min(newCount, retained), keepOld = retained - keepNew;
                    if (keepOld > 0) Array.Copy(previous.Frames!, oldCount - keepOld, merged, 0, keepOld);
                    if (keepNew > 0) Array.Copy(snapshot.Frames!, newCount - keepNew, merged, keepOld, keepNew);
                }
                snapshot = snapshot with { Frames = merged, HasFrameGap = previous.HasFrameGap || oldCount + newCount > retained };
            }
            _pendingSnapshot = snapshot; _pendingGeneration = generation;
            if (_publishQueued) return;
            _publishQueued = true;
        }
        try { _ = _dispatcher.BeginInvoke(new Action(DrainPendingSnapshot), DispatcherPriority.Background); }
        catch (InvalidOperationException)
        {
            lock (_publishGate) { _pendingSnapshot = null; _publishQueued = false; }
        }
    }
    private void DrainPendingSnapshot()
    {
        FpsSnapshot? snapshot; int generation;
        lock (_publishGate)
        {
            snapshot = _pendingSnapshot; generation = _pendingGeneration;
            _pendingSnapshot = null; _publishQueued = false;
        }
        if (snapshot is not null) PublishOnDispatcher(snapshot, generation);
    }
    private void PublishOnDispatcher(FpsSnapshot snapshot, int generation)
    {
        if (_disposed || generation != Volatile.Read(ref _generation)) return;
        Volatile.Write(ref _current, snapshot); Changed?.Invoke(snapshot);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Interlocked.Increment(ref _generation); Volatile.Write(ref _running, 0);
        lock (_publishGate) _pendingSnapshot = null;
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null; Changed = null;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
