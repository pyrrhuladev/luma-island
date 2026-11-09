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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static string Output = null!;
    private static readonly ComponentSettings Empty = new(Music: false, Volume: false, Time: false, Date: false, Battery: false);
    [STAThread]
    private static int Main(string[] args)
    {
        Output = System.IO.Path.GetFullPath(args.Length > 0 ? args[0] : System.IO.Path.Combine(FindRepository(), "work", "alerts-ui"));
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
                File.WriteAllText(System.IO.Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks,
                    version = assembly.GetName().Version?.ToString(),
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                    isolation = "No App startup, SMTC initialization, performance collector, FrameView session, sensor pipe, global input or real preference writes. Only explicit fixture readings, monotonic test times and invisible nonactivating test windows.",
                    demonstrationData = true, failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} quiet alerts UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        var realPreferences = RealPreferences();
        string fixtures = System.IO.Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var appearance = new AppearanceStore(System.IO.Path.Combine(fixtures, "appearance.json"));
        var components = new ComponentStore(System.IO.Path.Combine(fixtures, "components.json"));
        var placement = new PlacementStore(System.IO.Path.Combine(fixtures, "placement.json"));
        Check(appearance.Save(new AppearanceSettings(.15)) && components.Save(Empty), "Only isolated preference fixtures are prepared");
        using var media = new MediaService();
        using var system = new SystemService(); system.Dispose();
        var window = new MainWindow(media, system, placement, appearance, components, new AutostartService(readOnly: true))
            { Opacity = 0, Topmost = false, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var indicator = Named<Ellipse>(window, "QuietAlertIndicator");
            Check(indicator.Visibility == Visibility.Collapsed && !window.AlertView.Active && !window.Appearance.Alerts.Enabled,
                "The new indicator and all warning requirements default off");
            var settings = new AlertSettings(Enabled: true, GpuTemperature: false, Stutters: false);
            window.SetAlertSettings(settings);
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "Configuring an unshown editor preview creates no HWND");
            foreach (double time in new[] { 0d, 2, 4 }) window.ApplyAlertPerformance(Temperature(92), time);
            Check(window.AlertView.Active && indicator.Visibility == Visibility.Visible,
                "An unshown preview can render a genuine temporal warning without a collector");
            Check(window.AlertView.Message.Contains("CPU 92", StringComparison.Ordinal) &&
                System.Windows.Automation.AutomationProperties.GetName(indicator).Contains("CPU 92", StringComparison.Ordinal),
                "Warning explanation is available to tooltips and accessibility");
            Check(indicator.Fill is SolidColorBrush amber && amber.IsFrozen && amber.Color == Color.FromRgb(244, 202, 122),
                "The quiet amber indicator uses one frozen brush");
            await Task.Delay(40);
            Check(window.AlertView.Active, "Explicit preview clocks never schedule an expiry against the real machine clock");
            Check(!window.Components.CpuTemperature && !window.Components.Cpu && window.Appearance.Alerts.NeedsCpuTemperature,
                "A warning rule requests temperature independently of whether its numeric component is shown");
            window.ApplyAlertPerformance(Temperature(96), 5);
            Check(window.AlertView.Severity == HudAlertSeverity.High && indicator.Fill is SolidColorBrush red && red.Color == Color.FromRgb(241, 138, 133),
                "More elevated sustained heat changes the single dot to the high color");
            window.ApplyAlertPerformance(PerformanceSnapshot.Empty, 6);
            Check(!window.AlertView.Active && indicator.Visibility == Visibility.Collapsed, "Lost temperature data removes the indicator rather than displaying an alarm");
            Check(appearance.Load().Alerts == settings, "Warning choices persist through the normal appearance store");

            window.SetAlertSettings(settings with { Enabled = false });
            foreach (double time in new[] { 0d, 2, 4 }) window.ApplyAlertPerformance(Temperature(110), time);
            Check(!window.AlertView.Active && indicator.Visibility == Visibility.Collapsed, "The master switch suppresses fixture data too");

            window.SetAlertSettings(settings);
            double expiryReference = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            foreach (double time in new[] { expiryReference - 9.75, expiryReference - 7.75, expiryReference - 5.75 })
                window.ApplyAlertPerformance(Temperature(92), time);
            window.RefreshAlerts();
            Check(window.AlertView.Active, "A real-clock refresh schedules the remaining freshness deadline");
            await Task.Delay(400); await Settle();
            Check(!window.AlertView.Active && indicator.Visibility == Visibility.Collapsed,
                "One UI expiry removes a stale provider warning even if no further measurement callback arrives");

            new WindowInteropHelper(window).EnsureHandle(); window.Show(); await Settle();
            window.SetAlertSettings(new AlertSettings(Enabled: true, CpuTemperature: false, GpuTemperature: false));
            window.ApplyAlertFrames(Frames(10, "game-A"), 10);
            Check(window.AlertView.HasStutterAlert && indicator.Visibility == Visibility.Visible && !window.Components.FrameTime,
                "Raw-frame warnings work independently of the optional graph component");
            Check(window.AlertView.Message.Contains("70,0", StringComparison.Ordinal), "The series tooltip reports a real fixture duration");
            window.ApplyAlertFrames(Frames(10.2, "game-B") with { Frames = new[] { new FrameTimeSample(10.2, 60) } }, 10.2);
            Check(!window.AlertView.Active, "A session/PID/swapchain change clears the old series");
            window.ApplyAlertFrames(Frames(11, "game-B"), 11);
            Check(window.AlertView.Active, "A fresh stream can form its own warning");
            window.ApplyAlertFrames(new FpsSnapshot(null, "game", "No source", StreamId: "game-B"), 11.1);
            Check(!window.AlertView.Active && indicator.Visibility == Visibility.Collapsed, "A missing frame provider clears the visible indication");
            window.ApplyAlertFrames(Frames(12, "game-C"), 12);
            window.RefreshAlerts(nowSeconds: 15);
            Check(!window.AlertView.Active && indicator.Visibility == Visibility.Collapsed, "The series indication expires after three seconds");
            window.ApplyAlertFrames(Frames(16, "game-C"), 16);
            window.ResetAlerts();
            Check(!window.AlertView.Active && window.Appearance.Alerts.Enabled && indicator.Visibility == Visibility.Collapsed,
                "Visibility/profile reset releases warning state without changing saved preferences");

            foreach (double scale in new[] { .75, 1d, 1.5 })
            {
                window.SetHudScale(scale);
                window.SetComponents(Empty with { Time = true, Cpu = true, Gpu = true, Memory = true, Fps = true, OnePercentLow = true });
                window.SetAlertSettings(settings);
                foreach (double time in new[] { 0d, 2, 4 }) window.ApplyAlertPerformance(Temperature(92), time);
                await Settle();
                Check(indicator.Visibility == Visibility.Visible && indicator.Width == 7 && indicator.Height == 7,
                    "The alert stays one small seven-DIP dot: " + scale);
                Inside(window, indicator, "Warning dot stays inside the translucent HUD: " + scale);
                Check(window.Appearance.GlassOpacity == .15 && indicator.Opacity == 1,
                    "The background transparency setting does not fade the warning: " + scale);
                if (scale == 1)
                {
                    var snapshot = RenderOffscreen(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(snapshot));
                    using var output = File.Create(System.IO.Path.Combine(Output, "quiet-alert.png")); encoder.Save(output);
                }
            }
            window.SetAlertSettings(settings with { Enabled = false });
            Check(indicator.Visibility == Visibility.Collapsed && !window.AlertView.Active && !appearance.Load().Alerts.Enabled,
                "Final disable removes the dot and persists the opt-out");
        }
        finally { window.Close(); }
        Check(realPreferences.OrderBy(pair => pair.Key).SequenceEqual(RealPreferences().OrderBy(pair => pair.Key)),
            "All real appearance/component/placement/profile preference hashes are unchanged");
    }

    private static PerformanceSnapshot Temperature(double value) => PerformanceSnapshot.Empty with { CpuTemperatureCelsius = value };
    private static FpsSnapshot Frames(double now, string stream) => new(100, "game", "Fixture", FrameTimeMilliseconds: 70,
        Frames: new[] { new FrameTimeSample(now - .2, 50), new FrameTimeSample(now - .1, 60), new FrameTimeSample(now, 70) }, StreamId: stream);
    private static T Named<T>(MainWindow window, string name) where T : class
        => window.FindName(name) as T ?? throw new InvalidOperationException("Missing element " + name);
    private static async Task Settle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Inside(MainWindow window, FrameworkElement child, string description)
    {
        var surface = Named<FrameworkElement>(window, "IslandSurface");
        Rect bounds = child.TransformToAncestor(surface).TransformBounds(new Rect(new Point(), child.RenderSize));
        Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= surface.ActualWidth + 1 && bounds.Bottom <= surface.ActualHeight + 1, description);
    }
    private static BitmapSource RenderOffscreen(MainWindow window)
    {
        Rect original = DisplayService.GetWindowBounds(window);
        window.Placement.BeginDrag();
        try
        {
            DisplayService.MoveWindow(window, -30000, -30000); window.Opacity = 1; window.UpdateLayout();
            Check(DisplayService.GetWindowBounds(window).Right < 0 && GetForegroundWindow() != new WindowInteropHelper(window).Handle,
                "Demonstration capture stays offscreen and never takes foreground focus");
            var result = new RenderTargetBitmap((int)Math.Ceiling(window.Width), (int)Math.Ceiling(window.Height), 96, 96, PixelFormats.Pbgra32);
            result.Render(window); result.Freeze(); return result;
        }
        finally
        {
            window.Opacity = 0; DisplayService.MoveWindow(window, original.X, original.Y); window.Placement.EndDrag();
        }
    }
    private static Dictionary<string, string?> RealPreferences()
    {
        string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }.ToDictionary(name => name,
            name => File.Exists(System.IO.Path.Combine(root, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(System.IO.Path.Combine(root, name)))) : null);
    }
    private static string FindRepository()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "build.ps1"))) return directory.FullName;
        return Directory.GetCurrentDirectory();
    }
    private static void Check(bool result, string description)
    {
        if (!result) throw new InvalidOperationException("FAIL: " + description);
        Checks.Add(description);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
