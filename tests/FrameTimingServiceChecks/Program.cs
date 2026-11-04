using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Luma.Island.Services;

internal static partial class Program
{
    private static readonly List<string> Checks = new();
    private static string _output = string.Empty;
    private static int _logs;

    [STAThread]
    private static int Main(string[] args)
    {
        _output = Path.GetFullPath(args.Length > 0 ? args[0] : "work/frame-timing/service-checks");
        Directory.CreateDirectory(_output);
        string? failure = null;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            try { StateChecks(); await ServiceChecks(); }
            catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(exception); }
            finally
            {
                File.WriteAllText(Path.Combine(_output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, descriptions = Checks,
                    isolation = "Linked production state/service with an in-memory IFrameViewSource. No HWND, native SDK, driver, hardware, game, real configuration, input or global hotkey use.",
                    expectedProviderLogs = Volatile.Read(ref _logs), failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                application.Shutdown(failure is null ? 0 : 1);
            }
        };
        application.Run();
        Console.WriteLine(failure is null ? $"PASS: {Checks.Count} frame-timing state/service checks." : "FAIL: frame-timing state/service checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task ServiceChecks()
    {
        var harness = new Harness();
        using var service = Make(harness);
        Check(!service.IsRunning && service.Completion.IsCompleted && harness.Opened == 0,
            "Constructing the service does not open an SDK session or collector");
        service.Configure(false, false, false);
        await Task.Delay(35);
        Check(harness.Opened == 0, "All-off configuration creates no background source");

        service.Configure(true);
        await Until(() => service.Current.Fps == 100);
        Check(service.IsRunning && harness.Requests.Last() == (false, false), "FPS-only preserves the legacy constructor/configuration contract");
        Check(service.Current.OnePercentLow is null && service.Current.FrameTimeMilliseconds is null && service.Current.Frames is null,
            "Unrequested optional metrics cannot leak into FPS-only snapshots");
        Check(harness.Drains == 0, "FPS-only mode never drains the per-frame stream");
        service.Configure(false, true, false);
        await Until(() => service.Current.OnePercentLow == 37);
        Check(harness.Requests.Last() == (true, false) && service.Current.Fps is null,
            "Low-only mode requests its own metric without enabling the FPS component");
        Check(service.Current.OnePercentLow == 37 && service.Current.OnePercentLow != 100 && harness.Drains == 0,
            "Native 1% Low is neither copied from average FPS nor tied to per-frame capture");
        service.Configure(false, false, true);
        await Until(() => service.Current.FrameTimeMilliseconds == 27.5 && service.Current.Frames?.Length > 0);
        Check(harness.Requests.Last() == (false, true) && service.Current.Fps is null && service.Current.OnePercentLow is null,
            "Frames-only mode independently requests the frame stream");
        Check(service.Current.Frames!.All(frame => frame.Milliseconds == 27.5) && service.Current.FrameTimeMilliseconds != 1000d / 100,
            "Frames retain supplied Present intervals rather than an inverse average FPS estimate");
        Check(service.Current.StreamId is string stream && stream.Contains("/42/", StringComparison.Ordinal),
            "Snapshots identify the selected capture session and PID");

        string oldStream = service.Current.StreamId!;
        harness.Foreground = (uint)Environment.ProcessId;
        await Task.Delay(50);
        Check(service.Current.StreamId == oldStream && service.Current.ProcessName == "TestGame",
            "Interacting with this application retains the last actual game target");
        harness.Foreground = 43;
        await Until(() => service.Current.ProcessName == "OtherGame" && service.Current.StreamId != oldStream);
        Check(service.Current.Fps is null && service.Current.FrameTimeMilliseconds is null && service.Current.Frames is null,
            "A target with no samples cannot reuse the previous game's data");
        harness.Foreground = 99;
        await Until(() => service.Current.ProcessName is null && service.Current.StreamId is null);
        Check(service.Current.FrameTimeMilliseconds is null, "An excluded browser target does not become an invented game frame stream");

        int opened = harness.Opened;
        service.Configure(false, false, true);
        await Task.Delay(35);
        Check(harness.Opened == opened, "An unchanged metric selection does not reopen capture");
        service.Configure(false);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!service.IsRunning && harness.Active == 0 && harness.Opened == harness.Disposed,
            "All-off closes every fake source and finishes the worker");
        int reads = harness.Reads, drains = harness.Drains;
        await Task.Delay(35);
        Check(harness.Reads == reads && harness.Drains == drains, "No SDK polling survives all-off");

        await UnsupportedChecks();
        await LifetimeChecks();
        await StaleDispatchChecks();
        await BackpressureChecks();
    }

    private static FpsService Make(Harness harness) => new(harness.Open, () => Volatile.Read(ref harness.Foreground),
        process => process switch { 42 => "TestGame", 43 => "OtherGame", 99 => "chrome", _ => null },
        TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(8));

    private static async Task UnsupportedChecks()
    {
        foreach (var option in new[] { (Low: true, Frames: false), (Low: false, Frames: true) })
        {
            var harness = new Harness { LowSupported = false, FramesSupported = false };
            using var service = Make(harness);
            service.Configure(false, option.Low, option.Frames);
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            await Pump();
            Check(!service.IsRunning && harness.Active == 0 && harness.Opened == 1 && harness.Disposed == 1,
                "A sole unsupported metric does not retain an idle collector: " + option);
            Check(service.Current.Fps is null && service.Current.OnePercentLow is null && service.Current.FrameTimeMilliseconds is null &&
                (option.Low ? service.Current.OnePercentLowStatus : service.Current.FrameTimeStatus)!.Contains("unsupported", StringComparison.Ordinal),
                "Unsupported metrics show their capability reason without synthetic values: " + option);
            int reads = harness.Reads;
            await Task.Delay(35);
            Check(harness.Reads == reads && harness.Drains == 0, "An unsupported-only source stops polling: " + option);
        }
        var fallback = new Harness { LowSupported = false, FramesSupported = false };
        using (var service = Make(fallback))
        {
            service.Configure(true, true, true);
            await Until(() => service.Current.Fps == 100);
            Check(service.IsRunning && service.Current.OnePercentLow is null && service.Current.FrameTimeMilliseconds is null,
                "Unsupported optional metrics leave ordinary FPS functional");
            Check(fallback.Drains == 0, "Unsupported frame capability does not trigger frame polling");
            service.Configure(false); await service.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        using (var missing = new FpsService((_, _) => throw new FileNotFoundException("Fake SDK missing"), () => 42, _ => "TestGame",
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(8)))
        {
            missing.Configure(true, true, true);
            await missing.Completion.WaitAsync(TimeSpan.FromSeconds(3)); await Pump();
            Check(!missing.IsRunning && missing.Current.Status.Contains("FrameView SDK", StringComparison.Ordinal) && missing.Current.OnePercentLow is null,
                "A missing SDK finishes cleanly with an actionable unavailable status");
        }
        var broken = new Harness { FailReads = true };
        using (var service = Make(broken))
        {
            service.Configure(true, true, true);
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(3)); await Pump();
            Check(!service.IsRunning && broken.Active == 0 && broken.Disposed == 1 && service.Current.Fps is null,
                "A provider exception disposes its session without retaining stale readings");
        }
    }

    private static async Task LifetimeChecks()
    {
        var harness = new Harness { DisposeDelayMilliseconds = 25 };
        var service = Make(harness);
        service.Configure(true, true, true);
        await Until(() => harness.Active == 1);
        for (int index = 0; index < 40; index++) service.Configure(index % 3 == 0, index % 3 == 1, index % 3 == 2);
        service.Configure(true, true, true);
        await Until(() => service.Current.OnePercentLow == 37 && service.Current.FrameTimeMilliseconds == 27.5);
        Check(harness.MaximumActive == 1, "Rapid metric changes serialize source shutdown before the next session starts");
        service.Dispose(); service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(3)); await Pump();
        Check(!service.IsRunning && harness.Active == 0 && harness.Disposed == harness.Opened,
            "Disposal cancels and releases all rapidly toggled sessions exactly once");
        int reads = harness.Reads;
        await Task.Delay(35);
        Check(harness.Reads == reads, "Disposal cannot leave an idle background poll");
        bool rejected = false;
        try { service.Configure(true); } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "Disposed services cannot silently recreate capture");
    }

    private static async Task StaleDispatchChecks()
    {
        var harness = new Harness();
        using var service = Make(harness);
        service.Configure(true, true, true);
        await Until(() => service.Current.Fps.HasValue);
        var afterDisable = new List<FpsSnapshot>();
        Thread.Sleep(80); // Deliberately block only this no-window test dispatcher while the worker queues data.
        service.Changed += snapshot => afterDisable.Add(snapshot);
        service.Configure(false);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(3)); await Pump();
        Check(afterDisable.Count >= 1 && afterDisable.All(snapshot => snapshot.Fps is null && snapshot.OnePercentLow is null && snapshot.Frames is null),
            "Queued publications from an old generation cannot resurrect values after all-off");
    }

    private static async Task BackpressureChecks()
    {
        var harness = new Harness { FramesPerDrain = 1024 };
        using var service = Make(harness);
        service.Configure(false, false, true);
        await Until(() => service.Current.Frames is { Length: > 0 });
        var delivered = new List<FpsSnapshot>();
        service.Changed += snapshot => delivered.Add(snapshot);
        int before = harness.Drains;
        Thread.Sleep(450); // A blocked renderer must not create an unbounded queue of per-frame snapshots.
        Check(harness.Drains - before >= 5, "The fake collector continues receiving actual frames while the no-window UI dispatcher is blocked");
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check(delivered.Count is >= 1 and <= 2, "Blocked UI updates are coalesced rather than replayed as a long dispatcher backlog");
        Check(delivered.Any(snapshot => snapshot.HasFrameGap && snapshot.Frames is { Length: <= 4096 }),
            "Backpressure bounds the delivered frame batch and explicitly exposes dropped capture history");
        Check(delivered.Where(snapshot => snapshot.Frames is not null).All(snapshot => snapshot.Frames!.Length <= 4096 &&
            snapshot.Frames.Zip(snapshot.Frames.Skip(1), (first, next) => first.TimeSeconds < next.TimeSeconds).All(increasing => increasing)),
            "Merged frame batches remain bounded and chronologically ordered");
        service.Configure(false);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(3)); await Pump();
        Check(harness.Active == 0 && service.Current.Frames is null, "All-off also discards the queued backpressure payload");
    }

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("Timed out waiting for a fake-source state change.");
            await Task.Delay(5);
        }
    }
    private static async Task Pump() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static void Check(bool passed, string description)
    { if (!passed) throw new InvalidOperationException("FAIL: " + description); Checks.Add(description); }

    private sealed class Harness
    {
        public uint Foreground = 42;
        public bool LowSupported = true, FramesSupported = true, FailReads;
        public int Active, MaximumActive, Opened, Disposed, Reads, Drains, DisposeDelayMilliseconds;
        public int FramesPerDrain = 1;
        public readonly ConcurrentQueue<(bool Low, bool Frames)> Requests = new();
        public IFrameViewSource Open(bool low, bool frames)
        {
            Requests.Enqueue((low, frames));
            Interlocked.Increment(ref Opened);
            int active = Interlocked.Increment(ref Active);
            int previous;
            do { previous = MaximumActive; } while (active > previous && Interlocked.CompareExchange(ref MaximumActive, active, previous) != previous);
            return new FakeSource(this);
        }
    }

    private sealed class FakeSource(Harness harness) : IFrameViewSource
    {
        private int _disposed;
        public bool LowSupported => harness.LowSupported;
        public bool FramesSupported => harness.FramesSupported;
        public string LowStatus => LowSupported ? "fake native low" : "fake unsupported low";
        public string FrameTimeStatus => FramesSupported ? "fake actual Present" : "fake unsupported frames";
        public FrameViewStatistics ReadStatistics()
        {
            Interlocked.Increment(ref harness.Reads);
            if (harness.FailReads) throw new InvalidDataException("Fake provider failure");
            ulong now = (ulong)Stopwatch.GetTimestamp();
            return new(new[] { new FrameViewMetric(now, 42, 7, 100) }, new[] { new FrameViewMetric(now, 42, 7, 37) });
        }
        public FrameViewFrameBatch DrainFrames()
        {
            Interlocked.Increment(ref harness.Drains);
            ulong now = (ulong)Stopwatch.GetTimestamp();
            var frames = new FrameViewFrame[harness.FramesPerDrain];
            for (int index = 0; index < frames.Length; index++)
                frames[index] = new FrameViewFrame(now - (ulong)(frames.Length - index), 42, 7, 27.5, 42.75);
            return new(frames, false);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (harness.DisposeDelayMilliseconds > 0) Thread.Sleep(harness.DisposeDelayMilliseconds);
            Interlocked.Increment(ref harness.Disposed);
            Interlocked.Decrement(ref harness.Active);
        }
    }

    internal static void Log(Exception _) => Interlocked.Increment(ref _logs);
}

namespace Luma.Island
{
    internal static class App { internal static void Log(Exception exception) => global::Program.Log(exception); }
}
