using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;
using ShapePath = System.Windows.Shapes.Path;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static readonly ComponentSettings Empty = new(Music: false, Volume: false, Time: false, Date: false, Battery: false);
    private static string Output = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(FindRepository(), "work", "frame-metrics-ui"));
        Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(); }
            catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(exception); }
            finally
            {
                var assembly = typeof(MainWindow).Assembly;
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks,
                    version = assembly.GetName().Version?.ToString(),
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                    isolation = "No App startup, FrameView session, SMTC initialization, performance collector, extra sensor pipe, global input or real preference writes. Native HWNDs are invisible and nonactivating; all timing samples are explicit synthetic fixtures.",
                    demonstrationData = true, failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} frame metrics UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        var realPreferences = RealPreferences();
        string fixtures = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var appearance = new AppearanceStore(Path.Combine(fixtures, "appearance.json"));
        var components = new ComponentStore(Path.Combine(fixtures, "components.json"));
        var placement = new PlacementStore(Path.Combine(fixtures, "placement.json"));
        Check(appearance.Save(new AppearanceSettings(IslandAppearance.Glass, .4)) && components.Save(Empty), "Only isolated HUD preference files are prepared");
        using var media = new MediaService();
        using var system = new SystemService();
        system.Dispose();
        var window = new MainWindow(media, system, placement, appearance, components, new AutostartService(readOnly: true))
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            window.Show(); await Settle();
            Check(!window.Components.OnePercentLow && !window.Components.FrameTime && !components.Load().OnePercentLow && !components.Load().FrameTime,
                "New frame metrics are opt-in in memory and persisted settings");
            var menu = window.CreateComponentsMenu();
            MenuItem lowToggle = menu.Items.OfType<MenuItem>().Single(item => item.IsCheckable && item.Header is string header && header.Contains("1% Low", StringComparison.Ordinal));
            MenuItem frameToggle = menu.Items.OfType<MenuItem>().Single(item => item.IsCheckable && item.Header is string header && header.Contains("кадра", StringComparison.OrdinalIgnoreCase));
            Check(!lowToggle.IsChecked && !frameToggle.IsChecked, "Both new component menu items start unchecked");
            lowToggle.IsChecked = true; Click(lowToggle); await Settle();
            Check(window.Components.OnePercentLow && components.Load().OnePercentLow && !window.Components.Fps && !window.Components.FrameTime,
                "The real 1% Low checkbox persists independently of FPS and frame-time collection");
            Check(Named<FrameworkElement>(window, "CompactLowPanel").Visibility == Visibility.Visible &&
                !Named<TextBlock>(window, "CompactFps").IsVisible,
                "Low-only mode keeps its own visible HUD value without showing average FPS");
            window.ApplyFps(Snapshot(184, 123, 5.4));
            Check(Named<TextBlock>(window, "CompactOnePercentLow").Text == "123",
                "The 1% Low label formats the actual low metric");
            foreach (double? missing in new double?[] { null, double.NaN, double.PositiveInfinity, -1 })
            {
                window.ApplyFps(Snapshot(184, missing, null, frames: null, lowStatus: "Fixture low unavailable"));
                Check(Named<TextBlock>(window, "CompactOnePercentLow").Text.Contains('—'),
                    "Missing/invalid low never reuses the available average FPS: " + missing);
            }

            window.SetComponents(Empty);
            menu = window.CreateComponentsMenu();
            frameToggle = menu.Items.OfType<MenuItem>().Single(item => item.IsCheckable && item.Header is string header && header.Contains("кадра", StringComparison.OrdinalIgnoreCase));
            frameToggle.IsChecked = true; Click(frameToggle); await Settle();
            Check(window.Components.FrameTime && components.Load().FrameTime && !window.Components.Charts && !window.Components.Fps && !window.Components.OnePercentLow,
                "The frame-time checkbox persists without enabling FPS, 1% Low or load charts");
            Check(Named<FrameworkElement>(window, "FrameTimeCard").IsVisible && window.ChartsHeight == 0 &&
                Named<FrameworkElement>(window, "ChartsPanel").Visibility == Visibility.Collapsed,
                "The separate frame-time card does not depend on the CPU/GPU/RAM chart switch");
            Check(Named<RowDefinition>(window, "FrameTimeRow").ActualHeight > 0 &&
                Named<Canvas>(window, "FrameTimeCanvas").ActualHeight > 4,
                "The compact HUD allocates visible space to the frame-time plot");
            window.SetComponents(Empty with { Music = true, FrameTime = true });
            window.ApplyFps(Snapshot(null, null, 5.4)); await Settle();
            Check(Named<TextBlock>(window, "FrameTimeValue").IsVisible && Named<TextBlock>(window, "FrameTimeValue").ActualHeight > 10,
                "Music and frame time are visible together with FPS, Low, time and battery turned off");
            window.SetComponents(Empty with { FrameTime = true });
            window.ApplyFps(Snapshot(184, null, 5.4, SpikeFrames(), "stream-A")); await Settle();
            Check(Normalize(Named<TextBlock>(window, "FrameTimeValue").Text).Contains("5,4мс", StringComparison.Ordinal) ||
                Normalize(Named<TextBlock>(window, "FrameTimeValue").Text).Contains("5.4мс", StringComparison.Ordinal),
                "The latest per-frame interval is labelled in milliseconds rather than inferred from FPS");
            Check(Named<ShapePath>(window, "FrameTimeLine").Data is Geometry geometry && !geometry.Bounds.IsEmpty,
                "A batch of individual frames produces actual chart geometry");
            Check(Named<TextBlock>(window, "FrameTimePeak").Text.Contains("40", StringComparison.Ordinal),
                "An isolated forty-millisecond frame remains visible in the chart peak");
            Save(Render(window, 2), Path.Combine(Output, "frame-time-only.png"));
            window.ApplyFps(Snapshot(184, null, null, frames: null, stream: "stream-A", frameStatus: "Fixture frames unavailable", gap: true)); await Settle();
            Check(Named<TextBlock>(window, "FrameTimeValue").Text.Contains('—'), "A missing frame interval displays a dash rather than reciprocal average FPS");
            await Task.Delay(35);
            double resumed = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            window.ApplyFps(Snapshot(184, null, 5.4, new[] { new FrameTimeSample(resumed - .01, 5.4), new FrameTimeSample(resumed, 5.4) }, "stream-A")); await Settle();
            Check(Named<ShapePath>(window, "FrameTimeLine").Data is Geometry resumedGeometry &&
                resumedGeometry.GetWidenedPathGeometry(new Pen(Brushes.Black, 1)).Figures.Count >= 2,
                "An explicit missing-frame gap is not bridged by the resumed per-frame line");
            window.ApplyFps(Snapshot(184, null, 8, Frames(8), "stream-B")); await Settle();
            Check(Named<TextBlock>(window, "FrameTimePeak").Text.Contains("8", StringComparison.Ordinal) &&
                !Named<TextBlock>(window, "FrameTimePeak").Text.Contains("40", StringComparison.Ordinal),
                "A new stream identity clears the old swapchain peak even when the process name is unchanged");

            foreach (bool compact in new[] { false, true })
            foreach (double scale in new[] { .75, 1d, 1.5 })
            {
                window.SetCompactMode(compact); window.SetHudScale(scale);
                window.SetComponents(ComponentSettings.Default with { Battery = false, Fps = true, OnePercentLow = true, FrameTime = true,
                    Cpu = true, Gpu = true, Memory = true, CpuTemperature = true, GpuTemperature = true, MemoryTemperature = true, Charts = true });
                window.ApplyPerformance(new PerformanceSnapshot(58, 78, 42, 1, 2, 61, "Fixture", 58, "Fixture", 41, "Fixture"));
                window.ApplyFps(Snapshot(184, 123, 5.4, SpikeFrames(), "layout-stream")); await Settle();
                CheckInside(Named<FrameworkElement>(window, "FrameTimeCard"), Named<FrameworkElement>(window, "IslandSurface"), "Frame-time card fits the all-component HUD: " + compact + "/" + scale);
                var line = Named<ShapePath>(window, "FrameTimeLine");
                var canvas = Named<Canvas>(window, "FrameTimeCanvas");
                Check(line.Data is Geometry data && data.Bounds.Left >= -2 && data.Bounds.Top >= -2 &&
                    data.Bounds.Right <= canvas.ActualWidth + 2 && data.Bounds.Bottom <= canvas.ActualHeight + 2,
                    "Per-frame geometry stays inside its own canvas: " + compact + "/" + scale);
                foreach (double dpiScale in new[] { 1d, 2d })
                {
                    var bitmap = Render(window, dpiScale);
                    Check(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Frame metrics render at native and double DPI: " + compact + "/" + scale + "/" + dpiScale);
                }
                CheckInside(Named<FrameworkElement>(window, "CompactLowPanel"), Named<FrameworkElement>(window, "IslandSurface"), "1% Low fits the HUD surface: " + compact + "/" + scale);
                CheckNonOverlapping(Named<FrameworkElement>(window, "CompactLowPanel"), Named<FrameworkElement>(window, "CompactClock"), Named<FrameworkElement>(window, "IslandSurface"),
                    "Low and clock do not overlap: " + compact + "/" + scale);
                if (compact && scale == 1) Save(Render(window, 2), Path.Combine(Output, "frame-metrics-compact.png"));
            }
            window.SetComponents(Empty);
            Check(Named<FrameworkElement>(window, "CompactLowPanel").Visibility == Visibility.Collapsed &&
                Named<FrameworkElement>(window, "FrameTimeCard").Visibility == Visibility.Collapsed && Named<RowDefinition>(window, "FrameTimeRow").Height.Value == 0,
                "Disabling both new metrics removes their controls and reserved chart row");
            Check(Named<ShapePath>(window, "FrameTimeLine").Data is null, "Disabling frame time clears its retained plot geometry");
            window.SetComponents(Empty with { FrameTime = true }); await Settle();
            Check(Named<TextBlock>(window, "FrameTimeValue").Text.Contains('—') &&
                (Named<ShapePath>(window, "FrameTimeLine").Data is null || Named<ShapePath>(window, "FrameTimeLine").Data.Bounds.IsEmpty),
                "Re-enabling frame time starts empty instead of reviving an old stream");
        }
        finally { window.Close(); }
        await CheckProfilePersistenceAsync(fixtures, media, system);
        Check(realPreferences.OrderBy(pair => pair.Key).SequenceEqual(RealPreferences().OrderBy(pair => pair.Key)), "All real appearance/component/placement/profile preference hashes remain unchanged");
    }

    private static async Task CheckProfilePersistenceAsync(string directory, MediaService media, SystemService system)
    {
        var appearance = new AppearanceStore(Path.Combine(directory, "profile-appearance.json"));
        var components = new ComponentStore(Path.Combine(directory, "profile-components.json"));
        var placement = new PlacementStore(Path.Combine(directory, "profile-placement.json"));
        var profiles = new GameProfileStore(Path.Combine(directory, "profiles.json"));
        var ordinary = new HudProfileSettings(AppearanceSettings.Default, Empty, PlacementSettings.Default);
        var low = new GameProfile(Guid.NewGuid().ToString("D"), "Low fixture", @"D:\Games\LowFixture.exe", ordinary with { Components = Empty with { OnePercentLow = true } });
        var frames = new GameProfile(Guid.NewGuid().ToString("D"), "Frame fixture", @"D:\Games\FrameFixture.exe", ordinary with { Components = Empty with { FrameTime = true } });
        Check(appearance.Save(ordinary.Appearance) && components.Save(ordinary.Components) && placement.Save(ordinary.Placement) &&
            profiles.Save(new GameProfileSettings(true, new[] { low, frames })), "Frame metric profile fixtures use isolated settings");
        byte[] baseBytes = File.ReadAllBytes(Path.Combine(directory, "profile-components.json"));
        var controller = new GameProfileController(appearance, components, placement, profiles);
        var window = new MainWindow(media, system, controller.PlacementStore, controller.AppearanceStore, controller.ComponentStore,
            new AutostartService(readOnly: true), controller) { Opacity = 0, Topmost = false, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); await Settle();
            Check(window.ActivateGameProfile(low.Id) && window.Components.OnePercentLow && !window.Components.FrameTime && !window.Components.Fps,
                "A low-only game profile restores its independent metric choice");
            window.SetComponents(window.Components with { FrameTime = true });
            Check(profiles.Load().Profiles.Single(profile => profile.Id == low.Id).Settings.Components.FrameTime &&
                File.ReadAllBytes(Path.Combine(directory, "profile-components.json")).AsSpan().SequenceEqual(baseBytes),
                "Editing frame time writes only the active game profile and preserves ordinary components bytes");
            Check(window.ActivateGameProfile(frames.Id) && !window.Components.OnePercentLow && window.Components.FrameTime,
                "Another game's frame-time-only profile does not inherit 1% Low");
            Check(window.ActivateGameProfile(null) && !window.Components.OnePercentLow && !window.Components.FrameTime,
                "Returning to the ordinary HUD restores both metrics off");
        }
        finally { window.Close(); }
    }

    private static FpsSnapshot Snapshot(double? fps, double? low, double? milliseconds, FrameTimeSample[]? frames = null,
        string stream = "fixture-stream", string lowStatus = "Fixture low", string frameStatus = "Fixture frames", bool gap = false) =>
        new(fps, "Synthetic game", "Synthetic fixture", OnePercentLow: low, FrameTimeMilliseconds: milliseconds,
            Frames: frames, StreamId: stream, OnePercentLowStatus: lowStatus, FrameTimeStatus: frameStatus, HasFrameGap: gap);

    private static FrameTimeSample[] SpikeFrames()
    {
        const int count = 1500;
        double end = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - .001;
        double total = (count - 1) * .0054 + (.040 - .0054);
        double time = end - total;
        var result = new FrameTimeSample[count];
        for (int index = 0; index < count; index++)
        {
            double milliseconds = index == 1000 ? 40 : 5.4;
            if (index > 0) time += milliseconds / 1000;
            result[index] = new FrameTimeSample(time, milliseconds);
        }
        return result;
    }
    private static FrameTimeSample[] Frames(double value, bool spike = false)
    {
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        return Enumerable.Range(0, 20).Select(index => new FrameTimeSample(now - .2 + index * .01, spike && index == 12 ? 40 : value)).ToArray();
    }
    private static void CheckInside(FrameworkElement element, FrameworkElement surface, string description)
    {
        var bounds = element.TransformToAncestor(surface).TransformBounds(new Rect(new Point(), element.RenderSize));
        Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= surface.ActualWidth + 1 && bounds.Bottom <= surface.ActualHeight + 1, description);
    }
    private static void CheckNonOverlapping(FrameworkElement left, FrameworkElement right, FrameworkElement surface, string description)
    {
        Rect first = left.TransformToAncestor(surface).TransformBounds(new Rect(new Point(), left.RenderSize));
        Rect second = right.TransformToAncestor(surface).TransformBounds(new Rect(new Point(), right.RenderSize));
        Check(!first.IntersectsWith(second) || first.Right <= second.Left + 1 || second.Right <= first.Left + 1, description);
    }
    private static T Named<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing element: " + name);
    private static string Normalize(string value) => value.Replace(" ", "", StringComparison.Ordinal).Replace("\u00a0", "", StringComparison.Ordinal);
    private static async Task Settle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    private static BitmapSource Render(MainWindow window, double dpiScale)
    {
        Rect original = DisplayService.GetWindowBounds(window);
        double opacity = window.Opacity;
        bool wasDragging = window.Placement.IsDragging;
        if (!wasDragging) window.Placement.BeginDrag();
        try
        {
            DisplayService.MoveWindow(window, -30000, -30000);
            window.Opacity = 1;
            window.UpdateLayout();
            Check(DisplayService.GetWindowBounds(window).Right < 0 && GetForegroundWindow() != new WindowInteropHelper(window).Handle,
                "Opaque render capture stays offscreen and never owns foreground focus");
            var result = new RenderTargetBitmap((int)Math.Ceiling(window.Width * dpiScale), (int)Math.Ceiling(window.Height * dpiScale), 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
            result.Render(window); result.Freeze();
            var pixels = new byte[result.PixelWidth * result.PixelHeight * 4];
            result.CopyPixels(pixels, result.PixelWidth * 4, 0);
            Check(Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 3] > 0), "Actual WPF capture contains visible UI pixels");
            return result;
        }
        finally
        {
            window.Opacity = opacity;
            DisplayService.MoveWindow(window, original.X, original.Y);
            if (!wasDragging) window.Placement.EndDrag();
        }
    }
    private static void Save(BitmapSource bitmap, string path)
    { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Check(bool passed, string description) { if (!passed) throw new InvalidOperationException("FAIL: " + description); Checks.Add(description); }
    private static Dictionary<string, string?> RealPreferences()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }.ToDictionary(name => name,
            name => File.Exists(Path.Combine(root, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))) : null);
    }
    private static string FindRepository()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "Luma.Island", "MainWindow.xaml.cs"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
