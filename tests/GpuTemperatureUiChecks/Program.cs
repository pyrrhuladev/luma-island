using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private static readonly List<object> DpiChecks = new();
    private static readonly ComponentSettings Empty = new(Music: false, Volume: false, Time: false, Date: false, Battery: false);
    private static string Output = null!;
    private static object? ServiceResult;
    private static bool WindowClosed;

    [STAThread]
    private static int Main(string[] args)
    {
        string repository = FindRepository();
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(repository, "work", "temperature"));
        Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckUiAsync(); ServiceResult = await CheckServiceAsync(); }
            catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(exception); }
            finally
            {
                var assembly = typeof(MainWindow).Assembly;
                File.WriteAllText(Path.Combine(Output, "ui-verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null && WindowClosed, checks = Checks.Count, completedChecks = Checks,
                    version = assembly.GetName().Version?.ToString(),
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                    windowClosed = WindowClosed, dpiChecks = DpiChecks,
                    dpiScope = "WPF text/layout bounds and RenderTargetBitmap at 96/144/192 DPI; no monitor settings changed",
                    service = ServiceResult, failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} GPU temperature UI/service checks.");
        return failure is null && WindowClosed ? 0 : 1;
    }

    private static async Task CheckUiAsync()
    {
        string fixtures = Path.Combine(Output, "ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var componentStore = new ComponentStore(Path.Combine(fixtures, "components.json"));
        Check(componentStore.Save(Empty), "Isolated component fixture was saved");
        var appearance = new AppearanceStore(Path.Combine(fixtures, "appearance.json"));
        Check(appearance.Save(new AppearanceSettings(.65)), "Isolated appearance fixture was saved");
        using var media = new MediaService();
        using var system = new SystemService();
        var autostart = new AutostartService(runKey: @"Software\LumaIsland\Tests\GpuTemperatureUi\Run",
            approvalKey: @"Software\LumaIsland\Tests\GpuTemperatureUi\Approval", readOnly: true);
        var window = new MainWindow(media, system, new PlacementStore(Path.Combine(fixtures, "placement.json")),
            appearance, componentStore, autostart) { Topmost = false, ShowActivated = false, ShowInTaskbar = false };
        window.Closed += (_, _) => WindowClosed = true;
        try
        {
            system.Dispose(); // The harness never sends volume commands or listens to changing user audio state.
            new WindowInteropHelper(window).EnsureHandle();
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
            window.Placement.BeginDrag();
            DisplayService.MoveWindow(window, -30000, -30000);
            window.Show();
            await SettleAsync(window);
            DisplayService.MoveWindow(window, -30000, -30000);
            var compact = (UniformGrid)window.FindName("CompactPerformance");
            var compactGpu = Text(window, "CompactGpu");

            window.SetComponents(Empty with { GpuTemperature = true, Charts = true });
            window.ApplyPerformance(Snapshot(54));
            await SettleAsync(window);
            Check(window.HasPerformance && compact.Columns == 1 && VisibleTextCount(compact) == 1,
                "Temperature-only mode has exactly one GPU column");
            Check(Normalize(compactGpu.Text) == "GPU54°C",
                "The temperature-only label shows GPU 54 °C without a percentage");
            Check(window.ChartsHeight == 0 && ((Border)window.FindName("GpuChartCard")).Visibility == Visibility.Collapsed,
                "Temperature-only mode never creates a GPU percentage chart");

            foreach (double? missing in new double?[] { null, double.NaN, double.PositiveInfinity, -1, 151 })
            {
                window.ApplyPerformance(Snapshot(missing, "fixture sensor unavailable"));
                Check(compactGpu.Text.Contains('—') && compactGpu.Text.Contains("°C", StringComparison.Ordinal) &&
                    compactGpu.ToolTip is string reason && reason.Contains("fixture sensor unavailable", StringComparison.Ordinal),
                    "Missing/invalid temperature shows an em dash and source explanation");
            }

            window.SetComponents(Empty with { Gpu = true });
            double loadHeight = window.CollapsedWindowHeight;
            window.SetComponents(Empty with { Gpu = true, GpuTemperature = true });
            window.ApplyPerformance(Snapshot(54));
            Check(window.CollapsedWindowHeight > loadHeight,
                "The hardware band grows to fit both GPU readings");
            Check(compactGpu.Text.Contains('\n') &&
                Normalize(compactGpu.Text) == "GPU72%54°C", "GPU load and temperature occupy two lines");

            window.SetComponents(Empty);
            MenuItem menu = window.CreateComponentsMenu();
            Toggle(menu, "Температура GPU", true);
            Check(window.Components.GpuTemperature && !window.Components.Gpu && componentStore.Load().GpuTemperature,
                "The temperature checkbox persists independently from GPU load");
            Toggle(menu, "Нагрузка GPU", true);
            Toggle(menu, "Температура GPU", false);
            Check(window.Components.Gpu && !window.Components.GpuTemperature && componentStore.Load().Gpu &&
                !componentStore.Load().GpuTemperature, "Disabling temperature leaves GPU load enabled and persisted");

            var all = Empty with { Music = true, Time = true, Cpu = true, Gpu = true, Memory = true, Fps = true, Charts = true, GpuTemperature = true };
            window.SetComponents(all);
            window.ApplyPerformance(Snapshot(54));
            window.ApplyFps(new FpsSnapshot(144, "Test fixture", "Synthetic UI fixture"));
            Text(window, "CompactClock").Text = "21:48";
            await SettleAsync(window);
            Check(compact.Columns == 3 && VisibleTextCount(compact) == 3 &&
                ((FrameworkElement)window.FindName("CompactFpsPanel")).Visibility == Visibility.Visible,
                "Three hardware columns and a separate FPS headline remain visible with GPU temperature");
            Check(((FrameworkElement)window.FindName("GpuChartCard")).IsVisible,
                "GPU load history is visible in the same HUD as the temperature");
            await SettleAsync(window);
            foreach (double scale in new[] { 1d, 1.5, 2d })
            {
                CheckLabels(window, "CompactPerformance", scale);
                BitmapSource bitmap = Render(window, scale);
                Check(bitmap.PixelWidth == (int)Math.Ceiling(window.Width * scale) &&
                    bitmap.PixelHeight == (int)Math.Ceiling(window.Height * scale),
                    $"HUD render has complete bounds at {scale * 100:0}% DPI");
                DpiChecks.Add(new { dpi = scale * 96, width = bitmap.PixelWidth, height = bitmap.PixelHeight, clipped = false });
                if (scale == 2) Save(bitmap, Path.Combine(Output, "gpu-temperature.png"));
            }
            Check(DisplayService.GetWindowBounds(window).X < -10000, "The temporary UI stayed outside the desktop");
        }
        finally { window.Close(); }
        Check(WindowClosed, "The temporary WPF window and its native hooks were closed");
    }

    private static async Task<object> CheckServiceAsync()
    {
        var watch = Stopwatch.StartNew();
        using var service = new PerformanceService();
        var first = new TaskCompletionSource<PerformanceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool off = false, disposed = false;
        int lateReadings = 0, disposedEvents = 0;
        service.Changed += snapshot =>
        {
            if (disposed) disposedEvents++;
            if (off && snapshot != PerformanceSnapshot.Empty) lateReadings++;
            if (snapshot.GpuTemperatureCelsius.HasValue || !string.IsNullOrWhiteSpace(snapshot.GpuTemperatureStatus)) first.TrySetResult(snapshot);
        };
        service.Configure(false, false, false, true);
        Check(service.IsRunning, "Temperature-only service configuration starts the existing worker");
        PerformanceSnapshot value = await first.Task.WaitAsync(TimeSpan.FromSeconds(7));
        Check(value.CpuPercent is null && value.GpuPercent is null && value.MemoryPercent is null &&
            value.UsedMemoryBytes == 0 && value.TotalMemoryBytes == 0, "Temperature-only service does not collect CPU/GPU load or RAM");
        Check(value.GpuTemperatureCelsius is double celsius ? double.IsFinite(celsius) && celsius >= 0 && celsius <= 150 :
            !string.IsNullOrWhiteSpace(value.GpuTemperatureStatus), "The provider supplies either a real numeric temperature or an explicit unavailable status");
        off = true;
        service.Configure(false, false, false, false);
        Check(!service.IsRunning && service.Current == PerformanceSnapshot.Empty, "Disabling all metrics stops sampling and clears the current snapshot");
        await Task.Delay(2150);
        Check(lateReadings == 0 && service.Current == PerformanceSnapshot.Empty, "No late temperature reading is delivered after disabling");
        disposed = true;
        service.Dispose();
        await Task.Delay(100);
        Check(disposedEvents == 0 && !service.IsRunning, "Disposal cannot deliver pending callbacks or restart sampling");
        Check(watch.Elapsed < TimeSpan.FromSeconds(10), "The service integration check is bounded to ten seconds");
        return new { value.GpuTemperatureCelsius, value.GpuTemperatureStatus, elapsedSeconds = watch.Elapsed.TotalSeconds, lateReadings, disposedEvents };
    }

    private static void CheckLabels(MainWindow window, string panelName, double scale)
    {
        var panel = (Panel)window.FindName(panelName);
        var surface = (Border)window.FindName("IslandSurface");
        var roundedSurface = new RectangleGeometry(new Rect(new Point(), surface.RenderSize), 28, 28);
        foreach (TextBlock text in panel.Children.OfType<TextBlock>().Where(item => item.Visibility == Visibility.Visible))
        {
            var formatted = new FormattedText(text.Text, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.White, scale);
            if (double.IsFinite(text.LineHeight)) formatted.LineHeight = text.LineHeight;
            Check(formatted.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1 && formatted.Height <= text.ActualHeight + 1,
                $"{text.Name} text fits at {scale * 100:0}% DPI");
            Rect panelBounds = text.TransformToAncestor(panel).TransformBounds(new Rect(new Point(), text.RenderSize));
            Check(panelBounds.Left >= -1 && panelBounds.Top >= -1 && panelBounds.Right <= panel.ActualWidth + 1 && panelBounds.Bottom <= panel.ActualHeight + 1,
                $"{text.Name} stays inside its metric row at {scale * 100:0}% DPI");
            Rect surfaceBounds = text.TransformToAncestor(surface).TransformBounds(new Rect(new Point(), text.RenderSize));
            Check(roundedSurface.FillContains(surfaceBounds.TopLeft) && roundedSurface.FillContains(surfaceBounds.BottomRight),
                $"{text.Name} avoids the capsule's rounded clipping at {scale * 100:0}% DPI");
        }
    }

    private static PerformanceSnapshot Snapshot(double? temperature, string status = "Synthetic UI fixture") =>
        new(24, 72, 38, 13056700580, 34359738368, temperature, status);
    private static int VisibleTextCount(Panel panel) => panel.Children.OfType<TextBlock>().Count(item => item.Visibility == Visibility.Visible);
    private static string Normalize(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\u00a0", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
    private static TextBlock Text(MainWindow window, string name) => (TextBlock)window.FindName(name);
    private static async Task SettleAsync(MainWindow window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static void Toggle(MenuItem menu, string header, bool enabled)
    {
        MenuItem item = menu.Items.OfType<MenuItem>().Single(candidate => Equals(candidate.Header, header));
        item.IsChecked = enabled;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    }
    private static BitmapSource Render(MainWindow window, double scale)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width * scale), (int)Math.Ceiling(window.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        bitmap.Freeze();
        return bitmap;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path); encoder.Save(stream);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        Checks.Add(description);
    }
    private static string FindRepository()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "src", "Luma.Island", "MainWindow.xaml.cs"))) return folder.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
