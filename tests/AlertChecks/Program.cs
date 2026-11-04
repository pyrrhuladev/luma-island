using System;
using System.Linq;
using System.Text.Json;
using Luma.Island.Services;

internal static class Program
{
    private static int _checks;
    private static readonly AlertSettings Temperatures = new(Enabled: true, GpuTemperature: false, Stutters: false);
    private static readonly AlertSettings Frames = new(Enabled: true, CpuTemperature: false, GpuTemperature: false);
    private static int Main()
    {
        try
        {
            Settings(); TemperaturesAreTemporal(); StuttersUseRealFrames(); CombinedAndResets();
            Console.WriteLine($"PASS: {_checks} quiet alert checks.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Settings()
    {
        Check(!AlertSettings.Default.Enabled && !AlertSettings.Default.NeedsCpuTemperature && !AlertSettings.Default.NeedsFrames,
            "Warnings default off and never request background monitors");
        Check(AlertSettings.Normalize(null) == AlertSettings.Default, "Missing settings retain the opt-in default");
        var enabled = AlertSettings.Default with { Enabled = true };
        Check(enabled.NeedsCpuTemperature && enabled.NeedsGpuTemperature && !enabled.NeedsMemoryTemperature && enabled.NeedsFrames,
            "Enabled rules independently identify required existing sources");
        Check(!(enabled with { CpuTemperature = false, GpuTemperature = false, MemoryTemperature = false, Stutters = false }).NeedsFrames,
            "Disabling the stutter rule does not request raw frames");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var normalized = AlertSettings.Normalize(enabled with { CpuThreshold = invalid, GpuThreshold = invalid,
                MemoryThreshold = invalid, FrameTimeThreshold = invalid });
            Check(normalized.CpuThreshold == 90 && normalized.GpuThreshold == 85 && normalized.MemoryThreshold == 70 && normalized.FrameTimeThreshold == 40,
                "Invalid numbers restore finite defaults: " + invalid);
        }
        var clamped = AlertSettings.Normalize(enabled with { CpuThreshold = -3, GpuThreshold = 400, MemoryThreshold = 0,
            FrameTimeThreshold = 2000, StutterCount = int.MaxValue });
        Check(clamped.CpuThreshold == 30 && clamped.GpuThreshold == 115 && clamped.MemoryThreshold == 30 &&
            clamped.FrameTimeThreshold == 250 && clamped.StutterCount == 10, "All serialized numeric settings have bounded ranges");
        Check(AlertSettings.Normalize(enabled with { FrameTimeThreshold = -1, StutterCount = -1 }).FrameTimeThreshold == 10 &&
            AlertSettings.Normalize(enabled with { StutterCount = -1 }).StutterCount == 2, "Frame thresholds and event counts have nonzero lower bounds");
        string json = JsonSerializer.Serialize(enabled);
        Check(!json.Contains("Needs", StringComparison.Ordinal) && JsonSerializer.Deserialize<AlertSettings>(json) == enabled,
            "Computed sampling requirements are not persisted and settings round-trip");
        Check(JsonSerializer.Deserialize<AlertSettings>("{}") == AlertSettings.Default, "Empty old preferences migrate with warnings disabled");
    }

    private static void TemperaturesAreTemporal()
    {
        var engine = New(Temperatures);
        for (int index = 0; index < 100; index++) engine.ObserveTemperatures(92, null, null, 0);
        Check(!engine.View(0).Active, "Repeated callbacks at the same time cannot manufacture sustained heat");
        engine.ObserveTemperatures(92, null, null, 2);
        Check(!engine.View(2).Active, "A two-second hot sample remains below the sustain delay");
        engine.ObserveTemperatures(92, null, null, 4);
        Check(engine.View(4) is { Active: true, Severity: HudAlertSeverity.Warning, HasTemperatureAlert: true, HasStutterAlert: false },
            "Four seconds of continuously observed heat activates a quiet warning");
        Check(engine.View(4).Message.Contains("CPU 92", StringComparison.Ordinal) && engine.View(4).Message.Contains("порог 90", StringComparison.Ordinal),
            "Tooltip names the actual sensor and chosen threshold");
        Check(engine.NextExpirySeconds(4) == 10, "An active temperature exposes one freshness deadline");
        engine.ObserveTemperatures(96, null, null, 5);
        Check(engine.View(5).Severity == HudAlertSeverity.High, "A sustained alert five degrees above threshold uses the high indication");
        Check(engine.NextExpirySeconds(5) == 11, "A refreshed temperature extends its expiry even while the warning remains active");
        engine.ObserveTemperatures(88, null, null, 6);
        Check(engine.View(6).Active, "Three-degree hysteresis avoids flickering around the threshold");
        engine.ObserveTemperatures(87, null, null, 7);
        Check(!engine.View(7).Active, "Reaching the lower hysteresis boundary clears the warning");
        Check(engine.NextExpirySeconds(7) is null, "No UI timeout is needed while warnings are inactive");
        engine.ObserveTemperatures(91, null, null, 8);
        Check(!engine.View(8).Active, "A new episode must sustain heat again after cooling");

        engine = New(Temperatures);
        engine.ObserveTemperatures(91, null, null, 0);
        engine.ObserveTemperatures(89, null, null, 2);
        engine.ObserveTemperatures(91, null, null, 4);
        engine.ObserveTemperatures(91, null, null, 6);
        Check(!engine.View(6).Active, "A cool observation restarts a candidate even before warning activation");
        engine.ObserveTemperatures(91, null, null, 8);
        Check(engine.View(8).Active, "The restarted candidate activates only after its own four seconds");
        Check(!engine.View(15).Active, "An old temperature expires without new data");
        engine.ObserveTemperatures(100, null, null, 20);
        Check(!engine.View(20).Active, "A long provider pause cannot count as continuous hot readings");

        foreach (double? invalid in new double?[] { null, 0, -1, 201, double.NaN, double.PositiveInfinity })
        {
            engine = Hot();
            engine.ObserveTemperatures(invalid, null, null, 5);
            Check(!engine.View(5).Active, "Unavailable or invalid CPU data immediately clears an existing warning: " + invalid);
            engine.ObserveTemperatures(95, null, null, 6);
            Check(!engine.View(6).Active, "Recovered data begins a fresh sustain interval: " + invalid);
        }
        engine = New(Temperatures with { CpuTemperature = false });
        engine.ObserveTemperatures(120, null, null, 0); engine.ObserveTemperatures(120, null, null, 4);
        Check(!engine.View(4).Active, "A disabled sensor rule cannot produce warnings");
    }

    private static void StuttersUseRealFrames()
    {
        var engine = New(Frames);
        Batch(engine, "game-A", 1, (1, 10));
        Batch(engine, "game-A", 2, (2, 500));
        Check(!engine.View(2).Active, "An isolated long frame does not manufacture a repeated-stutter series");
        Batch(engine, "game-A", 3, (3, 42), (3.05, 50));
        Check(engine.View(3.05).HasStutterAlert && engine.View(3.05).Message.Contains("500,0", StringComparison.Ordinal),
            "Only a real series within two seconds activates and reports its measured peak");
        Check(!engine.View(6.05).Active, "The short indication expires after its last qualifying series");

        engine = New(Frames);
        Batch(engine, "game-A", 1.4, (1, 50), (1.2, 60), (1.4, 70));
        Check(engine.View(1.4).Active && engine.RetainedSlowFrameCount == 3, "Three distinct raw frames satisfy the configured count");
        Check(Math.Abs(engine.NextExpirySeconds(1.4)!.Value - 4.4) < .000001,
            "A frame-series indication exposes its one-shot hold deadline");
        Batch(engine, "game-A", 2, (1, 50), (1.2, 60), (1.4, 70));
        Check(engine.RetainedSlowFrameCount == 3, "Replaying an IPC batch cannot count the same frames twice");
        Batch(engine, "game-A", 3, (2.8, 10), (3, 10));
        Check(engine.View(3).Active, "Normal subsequent frames allow the existing brief warning to finish");
        Check(!engine.View(4.4).Active, "Normal frames do not extend the warning duration");

        engine = New(Frames);
        Batch(engine, "game-A", 0, (0, 60));
        Batch(engine, "game-A", 3, (3, 60));
        Batch(engine, "game-A", 6, (6, 60));
        Check(!engine.View(6).Active && engine.RetainedSlowFrameCount == 1, "Slow frames separated by more than two seconds do not accumulate");

        engine = New(Frames);
        Batch(engine, "game-A", 1.4, (1, 50), (1.2, 60), (1.4, 70));
        engine.BeginFrameBatch("game-A", true, true, 2);
        foreach (double time in new[] { 1d, 1.2, 1.4 }) engine.ObserveFrame(time, 80, 2);
        Check(!engine.View(2).Active && engine.RetainedSlowFrameCount == 0,
            "A capture gap clears a series while retaining duplicate protection in the same stream");
        engine.ObserveFrame(2, 50, 2);
        Check(engine.RetainedSlowFrameCount == 1 && !engine.View(2).Active, "Post-gap frames begin an independent series");
        engine.BeginFrameBatch("game-B", false, true, 2);
        engine.ObserveFrame(1.5, 80, 2); engine.ObserveFrame(1.6, 80, 2);
        Check(engine.RetainedSlowFrameCount == 2 && !engine.View(2).Active, "Changing PID/session/swapchain stream cannot merge series");
        engine.ObserveFrame(1.7, 80, 2);
        Check(engine.View(2).Active, "A new stream can independently form a real series");
        engine.BeginFrameBatch("game-B", false, false, 2.1);
        Check(!engine.View(2.1).Active && engine.RetainedSlowFrameCount == 0, "An unavailable frame source clears the warning immediately");
        engine.BeginFrameBatch(null, false, true, 2.2);
        engine.ObserveFrame(2.2, 100, 2.2);
        Check(!engine.View(2.2).Active && engine.RetainedSlowFrameCount == 0, "An anonymous source cannot create a warning");

        foreach (double invalid in new[] { 0d, -1, 60001, double.NaN, double.PositiveInfinity })
        {
            engine = New(Frames);
            Batch(engine, "game", 1, (.8, invalid), (.9, invalid), (1, invalid));
            Check(!engine.View(1).Active && engine.RetainedSlowFrameCount == 0, "Invalid durations cannot count as stutters: " + invalid);
        }
        engine = New(Frames);
        Batch(engine, "game", 10, (1, 100), (2, 100), (3, 100), (11, 100), (double.NaN, 100));
        Check(!engine.View(10).Active && engine.RetainedSlowFrameCount == 0, "Old, future and invalid timestamps cannot produce warnings");
        engine = New(Frames);
        engine.BeginFrameBatch("game", false, true, 1);
        for (int index = 0; index < 10000; index++) engine.ObserveFrame(index / 10000d, 60, 1);
        Check(engine.RetainedSlowFrameCount <= HudAlertEngine.MaximumQueuedSlowFrames && engine.View(1).Active,
            "Even an oversized source batch retains a fixed-size slow-frame queue");
        engine.Configure(Frames with { Stutters = false }, 1.1);
        Check(engine.RetainedSlowFrameCount == 0 && !engine.View(1.1).Active, "Disabling the rule releases all frame history immediately");
    }

    private static void CombinedAndResets()
    {
        var settings = new AlertSettings(Enabled: true, MemoryTemperature: true);
        var engine = New(settings);
        foreach (double time in new[] { 0d, 2, 4 }) engine.ObserveTemperatures(95, 89, 74, time);
        Batch(engine, "game", 4, (3.6, 50), (3.8, 60), (4, 70));
        var view = engine.View(4);
        Check(view.HasTemperatureAlert && view.HasStutterAlert && view.Message.Split('\n').Length == 4,
            "All independent hot sensors and a frame series combine into one small indicator");
        Check(engine.NextExpirySeconds(4) == 7, "Combined warnings expire at the first active deadline");
        Check(engine.NextExpirySeconds(7.1) == 10 && engine.View(7.1).HasTemperatureAlert && !engine.View(7.1).HasStutterAlert,
            "After the earlier series expires, temperature can retain its own later timeout");
        engine.ObserveTemperatures(95, null, 74, 8);
        Check(!engine.View(8).Message.Contains("GPU", StringComparison.Ordinal) && engine.View(8).Message.Contains("RAM", StringComparison.Ordinal),
            "Loss of one sensor clears only its own warning");
        engine.Configure(settings with { CpuThreshold = 100 }, 8);
        Check(!engine.View(8).Active && engine.RetainedSlowFrameCount == 0, "Changing settings resets old candidates and frame counts");
        engine = Hot();
        engine.Configure(Temperatures with { Enabled = false }, 5);
        Check(!engine.View(5).Active && !engine.Settings.NeedsCpuTemperature, "The master switch clears visible state and sampling requirements");
        engine = Hot(); engine.Reset();
        Check(!engine.View(4).Active && engine.Settings == Temperatures, "Hiding/profile/session reset clears state without changing user preferences");
        engine = Hot();
        Check(!engine.View(1).Active, "A backwards clock clears observations from the later time");
        engine = Hot();
        Check(!engine.View(double.NaN).Active && !engine.View(5).Active, "An invalid clock never displays or retains old warnings");
        engine = New(AlertSettings.Default);
        foreach (double time in new[] { 0d, 2, 4 }) engine.ObserveTemperatures(120, 120, 120, time);
        Batch(engine, "game", 4, (3.6, 100), (3.8, 100), (4, 100));
        Check(!engine.View(4).Active && engine.RetainedSlowFrameCount == 0, "The default master-off state ignores all supplied data");
    }
    private static HudAlertEngine New(AlertSettings settings)
    {
        var engine = new HudAlertEngine(); engine.Configure(settings, 0); return engine;
    }
    private static HudAlertEngine Hot()
    {
        var engine = New(Temperatures);
        foreach (double time in new[] { 0d, 2, 4 }) engine.ObserveTemperatures(92, null, null, time);
        return engine;
    }
    private static void Batch(HudAlertEngine engine, string stream, double now, params (double Time, double Ms)[] frames)
    {
        engine.BeginFrameBatch(stream, false, true, now);
        foreach (var frame in frames) engine.ObserveFrame(frame.Time, frame.Ms, now);
    }
    private static void Check(bool result, string description)
    {
        if (!result) throw new InvalidOperationException("FAIL: " + description);
        _checks++;
    }
}
