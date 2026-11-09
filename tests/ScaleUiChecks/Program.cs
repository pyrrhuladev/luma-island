using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static readonly List<object> Geometry = new();
    private static string Output = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length == 0 ? "work/scale-ui-checks" : args[0]);
        Directory.CreateDirectory(Output);
        var preferences = Preferences();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await RunAsync();
                Check(preferences.OrderBy(p => p.Key).SequenceEqual(Preferences().OrderBy(p => p.Key)), "Real preference files were not changed");
            }
            catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(ex); }
            finally
            {
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks, geometry = Geometry,
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))),
                    isolation = "Isolated preferences and read-only autostart; no SMTC initialization, media commands, collectors, hardware or service access. Native windows have zero opacity; captures are offscreen. DPI rendering does not change display settings.",
                    failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        Console.WriteLine(failure is null ? $"PASS: {Checks.Count} HUD scale UI checks." : "FAIL: HUD scale UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        string fixtures = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var store = new AppearanceStore(Path.Combine(fixtures, "appearance.json"));
        File.WriteAllText(Path.Combine(fixtures, "appearance.json"), "{\"mode\":\"Glass\",\"glassOpacity\":0.35,\"compact\":true,\"clickThrough\":false}");
        Check(store.Load().Scale == 1 && store.Load().GlassOpacity == .35, "Legacy appearance preferences keep their values and default to 100% HUD scale");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check(AppearanceStore.Normalize(store.Load() with { Scale = invalid }).Scale == 1, "Non-finite HUD scale falls back to 100%: " + invalid);
        Check(AppearanceStore.Normalize(store.Load() with { Scale = 0 }).Scale == .75 &&
            AppearanceStore.Normalize(store.Load() with { Scale = 10 }).Scale == 1.5, "Requested scale is constrained to 75–150%");
        Check(store.Save(new AppearanceSettings(.4)), "Fixture appearance saved");
        var components = new ComponentStore(Path.Combine(fixtures, "components.json"));
        var metrics = ComponentSettings.Default with { Battery = false, Cpu = true, Gpu = true, Memory = true, Fps = true,
            Charts = true, CpuTemperature = true, GpuTemperature = true, MemoryTemperature = true };
        Check(components.Save(metrics), "Fixture components saved");
        var placement = new PlacementStore(Path.Combine(fixtures, "placement.json"));
        using var media = new MediaService();
        using var system = new SystemService();
        system.Dispose();
        MainWindow Create() => new(media, system, placement, store, components, new AutostartService(readOnly: true))
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        var window = Create();
        try
        {
            window.Show();
            await Settle(window);
            Check(window.HudScale == 1 && window.EffectiveHudScale == 1 && window.Width == 408,
                "Default 100% retains the original native host width");
            foreach (double requested in new[] { .75, 1d, 1.25, 1.5 })
            {
                window.SetHudScale(requested);
                Check(store.Load().Scale == requested && !store.Load().ClickThrough,
                    "Saved scale preserves the input preference: " + requested);
                foreach (IslandPlacement preset in new[] { IslandPlacement.TopLeft, IslandPlacement.BottomRight })
                {
                    window.Placement.SetPreset(preset);
                    var saved = window.Placement.Settings;
                    await Settle(window);
                    CheckState(window, requested + "/" + preset + "/charts");
                    window.SetComponents(metrics with { Charts = false });
                    await Settle(window);
                    CheckState(window, requested + "/" + preset + "/no charts");
                    window.SetComponents(metrics);
                    await Settle(window);
                    CheckState(window, requested + "/" + preset + "/restored");
                    Check(window.Placement.Settings == saved, "Changing scaled content preserves the saved anchor: " + requested + "/" + preset);
                }
            }
            window.SetHudScale(1.5);
            window.SetHudScale(.75);
            await Settle(window);
            CheckState(window, "Successive scale edits settle at the latest request");
            window.SetHudScale(1.25);
            await Settle(window);
            CheckState(window, "Final scale before closing");
            var savedAppearance = window.Appearance;
            window.Close();
            window = Create();
            window.Show(); await Settle(window);
            Check(window.Appearance == savedAppearance && window.HudScale == 1.25,
                "Window recreation restores the requested scale with all other appearance values");
            CheckState(window, "Recreated scaled window");
            CheckSyntheticFit();

            // Keep normal-opacity captures offscreen and prevent placement from moving them onscreen.
            window.Placement.BeginDrag();
            DisplayService.MoveWindow(window, -30000, -30000);
            window.Opacity = 1;
            window.ApplyPerformance(new PerformanceSnapshot(42, 65, 37, 1, 2, 61, "Fixture", 54, "ok", 43, "ok"));
            window.ApplyFps(new FpsSnapshot(184, "Fixture", "Fixture"));
            foreach (double requested in new[] { .75, 1.5 })
            {
                window.SetHudScale(requested); await Settle(window);
                foreach (double outputScale in new[] { 1d, 2d })
                {
                    var image = new RenderTargetBitmap((int)Math.Ceiling(window.Width * outputScale),
                        (int)Math.Ceiling(window.Height * outputScale), 96 * outputScale, 96 * outputScale, PixelFormats.Pbgra32);
                    image.Render(window);
                    Check(image.PixelWidth == (int)Math.Ceiling(window.Width * outputScale) &&
                        image.PixelHeight == (int)Math.Ceiling(window.Height * outputScale),
                        "Scaled render bounds at HUD/DPI " + requested + "/" + outputScale);
                    if (requested == 1.5 && outputScale == 2)
                    {
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                        using var file = File.Create(Path.Combine(Output, "scale-normal.png")); encoder.Save(file);
                    }
                }
            }
            Check(DisplayService.GetWindowBounds(window).Right < 0, "All nonzero-opacity captures stayed offscreen");
        }
        finally { window.Opacity = 0; window.Close(); }
    }

    private static void CheckState(MainWindow window, string label)
    {
        double effective = window.EffectiveHudScale;
        Check(effective > 0 && effective <= window.HudScale + .000001, label + ": effective scale respects saved request");
        double expectedWidth = 408 * effective;
        double expectedHeight = window.CollapsedWindowHeight;
        Check(Math.Abs(window.Width - expectedWidth) < .01 && Math.Abs(window.Height - expectedHeight) < .01,
            label + $": host dimensions scale while base control geometry stays unchanged; actual={window.Width}x{window.Height}, expected={expectedWidth}x{expectedHeight}, effective={effective}");
        var root = (FrameworkElement)window.Content;
        Check(Math.Abs(root.LayoutTransform.Value.M11 - effective) < .000001 &&
            Math.Abs(root.LayoutTransform.Value.M22 - effective) < .000001 && root.LayoutTransform.IsFrozen,
            label + ": one frozen layout transform uniformly scales actual WPF content");
        var surface = (Border)window.FindName("IslandSurface");
        var renderedSurface = surface.TransformToAncestor(window).TransformBounds(new Rect(new Point(), surface.RenderSize));
        Check(Math.Abs(renderedSurface.Width - surface.ActualWidth * effective) <= 2 &&
            renderedSurface.Left >= -1 && renderedSurface.Top >= -1 && renderedSurface.Right <= window.Width + 1 && renderedSurface.Bottom <= window.Height + 1,
            label + ": the complete surface fits inside its scaled native host");
        double dpi = DisplayService.GetWindowScale(window);
        Rect actual = DisplayService.GetWindowBounds(window);
        Rect desired = window.Placement.DesiredBounds(expectedHeight, expectedWidth);
        Check(Math.Abs(actual.X - desired.X) <= 1.5 && Math.Abs(actual.Y - desired.Y) <= 1.5 &&
            Math.Abs(actual.Width - expectedWidth * dpi) <= 1.5 && Math.Abs(actual.Height - expectedHeight * dpi) <= 1.5,
            label + ": physical native bounds preserve the chosen anchor and monitor DPI");
        Rect work = window.Placement.CurrentDisplay.WorkArea;
        Check(actual.Left >= work.Left - 1 && actual.Top >= work.Top - 1 && actual.Right <= work.Right + 1 && actual.Bottom <= work.Bottom + 1,
            label + ": auto-fit keeps the complete window inside the current monitor work area");
        Check(((FrameworkElement)window.FindName("CompactView")).IsVisible,
            label + ": the HUD remains available at the requested scale");
        Geometry.Add(new { label, requested = window.HudScale, effective, dpi, actual.X, actual.Y, actual.Width, actual.Height });
    }

    private static void CheckSyntheticFit()
    {
        var fit = typeof(MainWindow).GetMethod("FitScale", BindingFlags.Static | BindingFlags.NonPublic)!;
        double Apply(double requested, Rect area, double dpi) => (double)fit.Invoke(null, new object[] { requested, new Size(408, 612), area, dpi })!;
        double capped = Apply(1.5, new Rect(0, 0, 800, 600), 1);
        Check(Math.Abs(capped - 584d / 612) < .000001 && capped * 612 <= 584,
            "A short monitor caps 150% to a size that fits tall HUD content");
        Check(Math.Abs(capped - Apply(1.5, new Rect(-1600, -1200, 1600, 1200), 2)) < .000001,
            "Equivalent physical work areas at 100% and 200% monitor DPI yield identical HUD auto-fit");
        Check(Apply(.75, new Rect(0, 0, 3840, 2160), 1) == .75,
            "A large display never enlarges a smaller requested scale");
    }

    private static async Task Settle(MainWindow window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException("FAIL: " + name); Checks.Add(name); }
    private static Dictionary<string, string?> Preferences()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json" }.ToDictionary(name => name, name => File.Exists(Path.Combine(root, name)) ?
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))) : null);
    }
}
