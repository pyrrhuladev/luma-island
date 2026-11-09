using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static readonly ComponentSettings Empty = new(Music: false, Volume: false, Time: false, Date: false, Battery: false);
    private static string Output = null!;
    private static object? Service;

    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(FindRepository(), "work", "cpu-memory-temperature-ui"));
        Directory.CreateDirectory(Output);
        var before = RealPreferences();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await RunUiAsync();
                Service = await RunServiceAsync();
                Check(before.OrderBy(p => p.Key).SequenceEqual(RealPreferences().OrderBy(p => p.Key)), "Real user preferences remain unchanged");
            }
            catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(ex); }
            finally
            {
                var assembly = typeof(MainWindow).Assembly;
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks,
                    version = assembly.GetName().Version?.ToString(), assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                    service = Service, dpiScope = "Offscreen native WPF text/layout and RenderTargetBitmap at 96/144/192 DPI; display settings unchanged.",
                    isolation = "Isolated stores, read-only autostart, no App startup, SMTC initialization, audio/playback actions, FPS/GPU probes, global hotkey registration or installer execution. Setup window is only constructed, never shown or clicked. Optional service client sends only read requests.",
                    failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        Console.WriteLine(failure is null ? $"PASS: {Checks.Count} CPU/RAM temperature UI/service checks." : "FAIL: CPU/RAM temperature UI/service checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunUiAsync()
    {
        string fixtures = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        string componentPath = Path.Combine(fixtures, "components.json");
        var store = new ComponentStore(componentPath);
        Check(!ComponentSettings.Default.CpuTemperature && !ComponentSettings.Default.MemoryTemperature,
            "Both new temperatures are opt-in by default");
        File.WriteAllText(componentPath, "{\"music\":false,\"cpu\":true,\"memory\":true,\"gpuTemperature\":true}");
        var legacy = store.Load();
        Check(legacy.Cpu && legacy.Memory && legacy.GpuTemperature && !legacy.CpuTemperature && !legacy.MemoryTemperature,
            "Legacy preferences preserve earlier flags and leave new sensors disabled");
        Check(store.Save(Empty with { CpuTemperature = true, MemoryTemperature = true }) && store.Load().CpuTemperature && store.Load().MemoryTemperature,
            "CPU and RAM temperature settings round-trip independently");
        store.Save(Empty);
        using var media = new MediaService();
        using var system = new SystemService();
        system.Dispose();
        var window = new MainWindow(media, system, new PlacementStore(Path.Combine(fixtures, "placement.json")),
            new AppearanceStore(Path.Combine(fixtures, "appearance.json")), store, new AutostartService(readOnly: true))
            { Topmost = false, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
            window.Placement.BeginDrag();
            DisplayService.MoveWindow(window, -30000, -30000);
            window.Show();
            DisplayService.MoveWindow(window, -30000, -30000);
            var menu = window.CreateComponentsMenu();
            Toggle(menu, "Температура CPU", true);
            Toggle(menu, "Температура RAM", true);
            Check(store.Load().CpuTemperature && store.Load().MemoryTemperature && !store.Load().Cpu && !store.Load().Memory,
                "Temperature menu checkboxes persist without enabling load monitoring");
            Toggle(menu, "Температура CPU", false);
            Check(!window.Components.CpuTemperature && window.Components.MemoryTemperature, "Disabling CPU temperature leaves RAM temperature enabled");
            Check(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Настроить датчики CPU / RAM…")),
                "Components menu exposes explicit optional sensor setup");

            var cases = new[]
            {
                (Name: "cpu-only", Settings: Empty with { CpuTemperature = true, Charts = true }, Cpu: "CPU68°C", Ram: "RAM", Columns: 1),
                (Name: "ram-only", Settings: Empty with { MemoryTemperature = true, Charts = true }, Cpu: "CPU", Ram: "RAM41°C", Columns: 1),
                (Name: "temperatures", Settings: Empty with { CpuTemperature = true, MemoryTemperature = true, Charts = true }, Cpu: "CPU68°C", Ram: "RAM41°C", Columns: 2),
                (Name: "cpu-load-temp", Settings: Empty with { Cpu = true, CpuTemperature = true }, Cpu: "CPU18%68°C", Ram: "RAM", Columns: 1),
                (Name: "ram-load-temp", Settings: Empty with { Memory = true, MemoryTemperature = true }, Cpu: "CPU", Ram: "RAM42%41°C", Columns: 1),
                (Name: "all-metrics", Settings: Empty with { Cpu = true, Gpu = true, Memory = true, Fps = true, CpuTemperature = true, GpuTemperature = true, MemoryTemperature = true, Charts = true }, Cpu: "CPU18%68°C", Ram: "RAM42%41°C", Columns: 3)
            };
            foreach (bool compact in new[] { false, true })
            {
                window.SetCompactMode(compact);
                foreach (var item in cases)
                {
                    window.SetComponents(item.Settings);
                    window.ApplyPerformance(Snapshot(68, 41));
                    window.ApplyFps(new FpsSnapshot(144, "Synthetic", "Fixture"));
                    await Settle(window);
                    var panel = (UniformGrid)window.FindName("CompactPerformance");
                    bool expectCharts = item.Settings.Charts &&
                        (item.Settings.Cpu || item.Settings.Memory || item.Settings.Gpu || item.Settings.Fps);
                    Check(window.HasPerformance && panel.Columns == item.Columns &&
                        Normalize(Text(window, "CompactCpu").Text) == item.Cpu &&
                        Normalize(Text(window, "CompactMemory").Text) == item.Ram &&
                        ((FrameworkElement)window.FindName("ChartsPanel")).IsVisible == expectCharts &&
                        (window.ChartsHeight > 0) == expectCharts,
                        $"{item.Name} keeps labels, columns and chart visibility in dense={compact}");
                }
                // Worst-case labels exercise the three-digit values in every metric column.
                window.ApplyPerformance(Snapshot(150, 125) with { CpuPercent = 100, GpuPercent = 100, MemoryPercent = 100, GpuTemperatureCelsius = 150 });
                await Settle(window);
                foreach (double scale in new[] { 1d, 1.5, 2d })
                {
                    BitmapSource bitmap = Render(window, scale);
                    Check(LabelsFit(window, "CompactPerformance", scale) && bitmap.PixelWidth == (int)Math.Ceiling(window.Width * scale) &&
                        bitmap.PixelHeight == (int)Math.Ceiling(window.Height * scale),
                        $"Worst-case CPU/RAM/GPU labels fit at {scale * 100:0}% in dense={compact}");
                    if (scale == 2 && compact) Save(bitmap, Path.Combine(Output, "temperatures-compact.png"));
                }
            }
            window.SetComponents(Empty with { CpuTemperature = true, MemoryTemperature = true });
            foreach (var invalid in new (double? Cpu, double? Ram)[]
            {
                (null, null), (double.NaN, double.NaN), (double.PositiveInfinity, double.PositiveInfinity),
                (double.NegativeInfinity, double.NegativeInfinity), (-41, -41), (151, 126)
            })
            {
                window.ApplyPerformance(Snapshot(68, 41));
                window.ApplyPerformance(Snapshot(invalid.Cpu, invalid.Ram) with { CpuTemperatureStatus = "unavailable", MemoryTemperatureStatus = "unavailable" });
                Check(Text(window, "CompactCpu").Text.Contains('—') && Text(window, "CompactMemory").Text.Contains('—'),
                    "A missing or invalid sample clears both previously valid temperatures");
            }
            window.ApplyPerformance(Snapshot(-1, -1));
            Check(Normalize(Text(window, "CompactCpu").Text) == "CPU-1°C" && Normalize(Text(window, "CompactMemory").Text) == "RAM-1°C",
                "Valid below-zero readings share the protocol's range policy");
            window.ApplyPerformance(Snapshot(150, 125));
            Check(Normalize(Text(window, "CompactCpu").Text) == "CPU150°C" && Normalize(Text(window, "CompactMemory").Text) == "RAM125°C",
                "CPU150 and RAM125 upper boundaries remain valid independently");
            window.ApplyPerformance(Snapshot(150, 126));
            Check(!Text(window, "CompactCpu").Text.Contains('—') && Text(window, "CompactMemory").Text.Contains('—'),
                "An invalid RAM reading does not hide a valid CPU reading");
            window.ApplyPerformance(Snapshot(151, 125));
            Check(Text(window, "CompactCpu").Text.Contains('—') && !Text(window, "CompactMemory").Text.Contains('—'),
                "An invalid CPU reading does not hide a valid RAM reading");

            window.SetComponents(Empty with { Cpu = true, Charts = true });
            window.ClearMetricHistory();
            window.ApplyPerformance(Snapshot(68, 41) with { CpuPercent = 145, CpuUsageSource = CpuUsageReader.UtilitySource });
            var cpuHistory = (MetricHistory)typeof(MainWindow).GetField("_cpuHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var samples = (IEnumerable<MetricHistory.Sample>)typeof(MetricHistory).GetField("_samples", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cpuHistory)!;
            string scaleLabel = Text(window, "CpuChartScale").Text;
            Check(Normalize(Text(window, "CompactCpu").Text) == "CPU145%" &&
                Text(window, "CpuChartValue").Text == "145%" && double.TryParse(scaleLabel.Split('–').Last().TrimEnd('%'), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out double maximum) && maximum >= 145,
                "Boost-aware CPU utility above 100 remains visible in the HUD label and dynamically scaled chart");
            Check(samples.Last().Value == 145 && cpuHistory.Maximum(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency) == 145,
                "The actual CPU history retains 145 instead of clamping it to 100");
            Check(Text(window, "CompactCpu").ToolTip is string utilityTip && utilityTip.Contains("Processor Utility", StringComparison.Ordinal) &&
                utilityTip.Contains("Turbo Boost", StringComparison.Ordinal), "CPU utility tooltip identifies its frequency-aware Windows source");
            window.ApplyPerformance(Snapshot(68, 41) with { CpuPercent = double.NaN, CpuUsageSource = CpuUsageReader.UtilitySource });
            Check(Text(window, "CompactCpu").Text.Contains('—') && Text(window, "CpuChartValue").Text == "—" && samples.Last().Value is null,
                "An invalid next CPU sample clears the displayed reading and records an explicit history gap");
            window.ApplyPerformance(Snapshot(68, 41) with { CpuPercent = 29, CpuUsageSource = CpuUsageReader.BusySource });
            Check(Text(window, "CompactCpu").ToolTip is string fallbackTip && fallbackTip.Contains("Резерв", StringComparison.OrdinalIgnoreCase) &&
                fallbackTip.Contains("без учёта Turbo Boost", StringComparison.Ordinal), "The busy-time fallback tooltip explains its lower value and missing frequency weighting");
            var setup = new SensorSetupWindow();
            Check(!setup.IsVisible && new WindowInteropHelper(setup).Handle == IntPtr.Zero,
                "Constructing sensor setup neither shows a window nor creates a native HWND");
            setup.Close();
            Check(DisplayService.GetWindowBounds(window).X < -10000, "The native temperature fixture remains outside the desktop");
        }
        finally { window.Close(); }
    }

    private static async Task<object> RunServiceAsync()
    {
        using var service = new PerformanceService();
        var reading = new TaskCompletionSource<PerformanceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        int late = 0, afterDispose = 0;
        bool off = false, disposed = false;
        service.Changed += snapshot =>
        {
            if (off && snapshot != PerformanceSnapshot.Empty) late++;
            if (disposed) afterDispose++;
            if (!string.IsNullOrEmpty(snapshot.CpuTemperatureStatus) && !string.IsNullOrEmpty(snapshot.MemoryTemperatureStatus)) reading.TrySetResult(snapshot);
        };
        service.Configure(false, false, false, false, true, true);
        Check(service.IsRunning, "Temperature-only configuration starts the existing performance worker");
        var value = await reading.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Check(value.CpuPercent is null && value.GpuPercent is null && value.MemoryPercent is null && value.GpuTemperatureCelsius is null &&
            value.UsedMemoryBytes == 0 && value.TotalMemoryBytes == 0, "CPU/RAM temperature-only sampling does not collect load, GPU or memory usage");
        Check((value.CpuTemperatureCelsius is null || SensorProtocol.IsValidCelsius(value.CpuTemperatureCelsius)) &&
            (value.MemoryTemperatureCelsius is null || SensorProtocol.IsValidCelsius(value.MemoryTemperatureCelsius, memory: true)),
            "The optional client supplies bounded readings or explicit unavailable status");
        off = true;
        service.Configure(false, false, false, false, false, false);
        Check(!service.IsRunning && service.Current == PerformanceSnapshot.Empty, "Disabling temperatures stops the worker and clears its snapshot");
        await Task.Delay(2150);
        Check(late == 0 && service.Current == PerformanceSnapshot.Empty, "No late thermal reading arrives after disable");
        disposed = true;
        service.Dispose();
        await Task.Delay(100);
        Check(afterDispose == 0 && !service.IsRunning, "Disposal cannot publish a queued thermal reading");
        return new { value.CpuTemperatureCelsius, value.CpuTemperatureStatus, value.MemoryTemperatureCelsius, value.MemoryTemperatureStatus, late, afterDispose };
    }

    private static PerformanceSnapshot Snapshot(double? cpu, double? memory) => new(18, 34, 42, 16UL << 30, 64UL << 30,
        GpuTemperatureCelsius: 54, GpuTemperatureStatus: "fixture", CpuTemperatureCelsius: cpu, CpuTemperatureStatus: "ok", MemoryTemperatureCelsius: memory, MemoryTemperatureStatus: "ok");
    private static bool LabelsFit(MainWindow window, string panelName, double scale)
    {
        var panel = (Panel)window.FindName(panelName);
        foreach (var text in panel.Children.OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible))
        {
            var formatted = new FormattedText(text.Text, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.White, scale);
            if (double.IsFinite(text.LineHeight)) formatted.LineHeight = text.LineHeight;
            Rect bounds = text.TransformToAncestor(panel).TransformBounds(new Rect(new Point(), text.RenderSize));
            if (formatted.WidthIncludingTrailingWhitespace > text.ActualWidth + 1 || formatted.Height > text.ActualHeight + 1 ||
                bounds.Left < -1 || bounds.Top < -1 || bounds.Right > panel.ActualWidth + 1 || bounds.Bottom > panel.ActualHeight + 1) return false;
        }
        return true;
    }
    private static TextBlock Text(MainWindow window, string name) => (TextBlock)window.FindName(name);
    private static string Normalize(string value) => value.Replace(" ", "", StringComparison.Ordinal).Replace("\u00a0", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
    private static void Toggle(MenuItem menu, string header, bool value)
    {
        var item = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, header)); item.IsChecked = value;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    }
    private static async Task Settle(MainWindow window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static BitmapSource Render(MainWindow window, double scale)
    {
        var result = new RenderTargetBitmap((int)Math.Ceiling(window.Width * scale), (int)Math.Ceiling(window.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        result.Render(window); result.Freeze(); return result;
    }
    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
    }
    private static void Check(bool valid, string description) { if (!valid) throw new InvalidOperationException("FAIL: " + description); Checks.Add(description); }
    private static Dictionary<string, string> RealPreferences()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json" }.ToDictionary(n => n,
            n => File.Exists(Path.Combine(folder, n)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, n)))) : "absent");
    }
    private static string FindRepository()
    {
        for (DirectoryInfo? f = new(AppContext.BaseDirectory); f is not null; f = f.Parent)
            if (File.Exists(Path.Combine(f.FullName, "src", "Luma.Island", "MainWindow.xaml.cs"))) return f.FullName;
        throw new DirectoryNotFoundException("Overlay repository root was not found.");
    }
}
