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
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static string Output = null!;
    private static readonly ComponentSettings All = new(Cpu: true, Gpu: true, Memory: true, Fps: true,
        GpuTemperature: true, CpuTemperature: true, MemoryTemperature: true, OnePercentLow: true, FrameTime: true);
    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : "work/hud-layout-ui");
        Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(); }
            catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(exception); }
            finally
            {
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks, failure,
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))),
                    isolation = "Production HUD controls; isolated preferences; no App startup, SDK, collectors, media initialization, global input or real preference writes; invisible nonactivating HWND. Preview data is synthetic."
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} HUD layout UI checks.");
        return failure is null ? 0 : 1;
    }
    private static async Task Run()
    {
        var original = PreferenceHashes();
        string fixtures = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var appearance = new AppearanceStore(Path.Combine(fixtures, "appearance.json"));
        var components = new ComponentStore(Path.Combine(fixtures, "components.json"));
        var placement = new PlacementStore(Path.Combine(fixtures, "placement.json"));
        appearance.Save(new AppearanceSettings(IslandAppearance.Glass, .3)); components.Save(All);
        using var media = new MediaService(); using var system = new SystemService(); system.Dispose();
        var window = new MainWindow(media, system, placement, appearance, components, new AutostartService(readOnly: true))
            { Opacity = 0, Topmost = false, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show(); await Settle(window); Inject(window);
            Check(window.Appearance.Layout == HudLayoutSettings.Default, "Old appearance files retain original HUD arrangement");
            Check(Text(window, "CompactFps").FontSize == 34 && Text(window, "CompactCpu").FontSize == 12,
                "Default normal fonts retain their exact pre-editor sizes");
            Check(((UniformGrid)window.FindName("CompactPerformance")).Columns == 3 && Math.Abs(window.CollapsedWindowHeight - 186) < .01,
                "Default normal hardware geometry and host height are unchanged");

            string[] blockOrders = ["header,metrics,music", "header,music,metrics", "metrics,header,music", "metrics,music,header", "music,header,metrics", "music,metrics,header"];
            string[] metricOrders = ["cpu,gpu,memory", "cpu,memory,gpu", "gpu,cpu,memory", "gpu,memory,cpu", "memory,cpu,gpu", "memory,gpu,cpu"];
            for (int i = 0; i < blockOrders.Length; i++)
            {
                window.SetHudLayout(new(blockOrders[i], metricOrders[i])); await Settle(window);
                var grid = (Grid)window.FindName("CompactContentGrid");
                string[] expectedBlocks = blockOrders[i].Split(',');
                var blocks = new Dictionary<string, FrameworkElement> { ["header"] = (FrameworkElement)window.FindName("HudHeader"), ["metrics"] = (FrameworkElement)window.FindName("CompactPerformance"), ["music"] = (FrameworkElement)window.FindName("CompactMusicRow") };
                Check(expectedBlocks.Select((key, index) => Grid.GetRow(blocks[key]) == index).All(value => value), "Requested groups occupy the requested rows: " + blockOrders[i]);
                var performance = (UniformGrid)window.FindName("CompactPerformance");
                Check(performance.Children.Cast<TextBlock>().Select(t => t.Name).SequenceEqual(metricOrders[i].Split(',').Select(MetricName)), "Metrics retain original controls in requested order: " + metricOrders[i]);
                Rect[] bounds = expectedBlocks.Select(key => Bounds(blocks[key], grid)).ToArray();
                Check(bounds[0].Bottom <= bounds[1].Top + .1 && bounds[1].Bottom <= bounds[2].Top + .1, "Reordered visible groups never overlap");
                Check(appearance.Load().Layout == window.Appearance.Layout, "Layout edits persist through the appearance store");
            }

            foreach (bool compact in new[] { false, true })
                foreach (int columns in new[] { 1, 2, 3 })
                    foreach (var values in new[] { (Text: .8, Gap: 2d, Scale: .75), (Text: 1d, Gap: 6d, Scale: 1d), (Text: 1.3, Gap: 12d, Scale: 1.5) })
                    {
                        window.SetCompactMode(compact); window.SetHudScale(values.Scale);
                        window.SetHudLayout(new("music,header,metrics", "memory,gpu,cpu", columns, values.Text, values.Gap)); Inject(window);
                        await Settle(window);
                        var performance = (UniformGrid)window.FindName("CompactPerformance");
                        Check(performance.Columns == columns && performance.ActualHeight > 0, "Hardware row count follows column choice");
                        foreach (string name in new[] { "CompactCpu", "CompactGpu", "CompactMemory" }) CheckText(Text(window, name), performance);
                        foreach (string name in new[] { "HudHeader", "CompactPerformance", "CompactMusicRow" })
                            Check(Inside(Bounds((FrameworkElement)window.FindName(name), (FrameworkElement)window.FindName("IslandSurface")), ((FrameworkElement)window.FindName("IslandSurface")).RenderSize), "Custom group remains inside the capsule: " + name);
                        double font = Text(window, "CompactCpu").FontSize;
                        window.SetComponents(All); window.SetHudScale(values.Scale); await Settle(window);
                        Check(Math.Abs(Text(window, "CompactCpu").FontSize - font) < .001, "Repeated component and scale application never compounds font scale");
                        window.ToggleExpanded(); await window.WaitForTransitionAsync(); await Settle(window);
                        foreach (string name in new[] { "ExpandedCpu", "ExpandedGpu", "ExpandedMemory", "ExpandedFps" }) CheckText(Text(window, name), (FrameworkElement)window.FindName("ExpandedPerformance"));
                        Check(window.Height <= window.Placement.CurrentDisplay.WorkArea.Height / DisplayService.GetWindowScale(window) + 1,
                            "Custom expanded height fits the monitor without changing saved user scale");
                        if (compact && columns == 2 && values.Text > 1) Save(MainWindow.RenderHudPreview(window.Appearance, All), "layout-custom.png");
                        window.ToggleExpanded(); await window.WaitForTransitionAsync(); await Settle(window);
                    }
            foreach (var subset in new[]
            {
                new ComponentSettings(Music: false, Cpu: false, Gpu: true, Memory: false),
                new ComponentSettings(Music: false, Memory: true, Fps: true),
                new ComponentSettings(Music: false, CpuTemperature: true),
                All with { Cpu = false, CpuTemperature = false, Gpu = false, GpuTemperature = false, Charts = true }
            })
                foreach (int columns in new[] { 1, 2, 3 })
                {
                    window.SetHudLayout(new(MetricOrder: "memory,gpu,cpu", MetricColumns: columns, TextScale: 1.3, Spacing: 12));
                    window.SetComponents(subset); Inject(window); await Settle(window);
                    window.ToggleExpanded(); await window.WaitForTransitionAsync(); await Settle(window);
                    var band = (FrameworkElement)window.FindName("ExpandedPerformance");
                    foreach (string name in new[] { "ExpandedCpu", "ExpandedGpu", "ExpandedMemory", "ExpandedFps" })
                        if (Text(window, name).Visibility == Visibility.Visible) CheckText(Text(window, name), band);
                    var expanded = (Grid)window.FindName("ExpandedView");
                    Rect bandBounds = Bounds(band, expanded);
                    double top = expanded.RowDefinitions.Take(4).Sum(row => row.ActualHeight);
                    Check(bandBounds.Top >= top - 1 && bandBounds.Bottom <= top + expanded.RowDefinitions[4].ActualHeight + 1,
                        "Custom visible-only wrap fills its allocated band without hidden slots or overflow");
                    if (subset.Charts)
                        foreach (string name in new[] { "MemoryChartValue", "FpsChartValue", "FpsChartScale" })
                            CheckText(Text(window, name), (FrameworkElement)Text(window, name).Parent);
                    if (subset.FrameTime)
                        foreach (string name in new[] { "FrameTimeValue", "FrameTimeScale", "FrameTimePeak" })
                            CheckText(Text(window, name), (FrameworkElement)Text(window, name).Parent);
                    window.ToggleExpanded(); await window.WaitForTransitionAsync(); await Settle(window);
                }
            window.SetHudLayout(new(MetricColumns: 1));
            window.SetComponents(All with { Cpu = false, CpuTemperature = false, Gpu = false, GpuTemperature = false, Memory = false, MemoryTemperature = false });
            await Settle(window);
            Check(((FrameworkElement)window.FindName("CompactPerformance")).Visibility == Visibility.Collapsed, "All hidden hardware metrics collapse their custom rows");
            window.SetHudLayout(HudLayoutSettings.Default); window.SetHudScale(1); window.SetCompactMode(false); window.SetComponents(All); Inject(window); await Settle(window);
            Check(((Grid)window.FindName("CompactContentGrid")).RowDefinitions[0] == window.FindName("HudHeaderRow") && Text(window, "CompactCpu").FontSize == 12,
                "Reset restores original row ownership and base font sizes");
            Check(PreferenceHashes().OrderBy(p => p.Key).SequenceEqual(original.OrderBy(p => p.Key)), "All actual user preferences remain byte-for-byte unchanged");
        }
        finally { window.Close(); }
    }
    private static void Inject(MainWindow window)
    {
        window.ApplyPerformance(new(150, 100, 99, 31UL << 30, 32UL << 30, 91, "Fixture", 98, "Fixture", 77, "Fixture"));
        window.ApplyFps(new(999, "Fixture", "Synthetic", 850, 1.4));
    }
    private static string MetricName(string key) => key switch { "cpu" => "CompactCpu", "gpu" => "CompactGpu", _ => "CompactMemory" };
    private static async Task Settle(MainWindow window) { await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle); }
    private static TextBlock Text(MainWindow window, string name) => (TextBlock)window.FindName(name);
    private static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(), element.RenderSize));
    private static bool Inside(Rect bounds, Size parent) => bounds.Left >= -.1 && bounds.Top >= -.1 && bounds.Right <= parent.Width + .1 && bounds.Bottom <= parent.Height + .1;
    private static void CheckText(TextBlock text, FrameworkElement panel)
    {
        var formatted = new FormattedText(text.Text, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.White, 1);
        Check(text.ActualWidth + .5 >= formatted.Width && text.ActualHeight + 1 >= formatted.Height, "Scaled metric text is not clipped: " + text.Name);
        Check(Inside(Bounds(text, panel), panel.RenderSize), "Scaled metric stays inside its hardware panel: " + text.Name);
    }
    private static Dictionary<string, string?> PreferenceHashes()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }.ToDictionary(name => name,
            name => File.Exists(Path.Combine(dir, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name)))) : null);
    }
    private static void Save(BitmapSource bitmap, string name) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(Output, name)); encoder.Save(file); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Checks.Add(message); }
}
