using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string Output = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(FindRepository(), "work", "collapsed-media"));
        Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(); }
            catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(exception); }
            finally
            {
                var assembly = typeof(MainWindow).Assembly;
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks,
                    version = assembly.GetName().Version?.ToString(),
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                    isolation = "Offscreen HWND, synthetic metadata only. No media service initialization or playback/volume commands; isolated preference files and read-only autostart provider.",
                    failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        Console.WriteLine(failure is null ? $"PASS: {Checks.Count} collapsed media UI checks." : "FAIL: collapsed media UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task Run()
    {
        var preferences = RealPreferences();
        string fixtures = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var settings = ComponentSettings.Default with { Cpu = true, Gpu = true, Memory = true, Fps = true,
            GpuTemperature = true, CpuTemperature = true, MemoryTemperature = true };
        var components = new ComponentStore(Path.Combine(fixtures, "components.json"));
        Check(components.Save(settings), "Component settings use an isolated fixture");
        var appearance = new AppearanceStore(Path.Combine(fixtures, "appearance.json"));
        Check(appearance.Save(new AppearanceSettings(.65)), "Appearance uses an isolated fixture");
        using var media = new MediaService();
        using var system = new SystemService();
        system.Dispose();
        var autostart = new AutostartService(runKey: @"Software\LumaIsland\Tests\CollapsedMedia\Run",
            approvalKey: @"Software\LumaIsland\Tests\CollapsedMedia\Approval", readOnly: true);
        var window = new MainWindow(media, system, new PlacementStore(Path.Combine(fixtures, "placement.json")), appearance, components, autostart)
        { Opacity = 0, Topmost = false, ShowActivated = false, ShowInTaskbar = false };
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
            window.Show();
            await Settle(window);
            CheckDragTargets(window);
            window.Placement.BeginDrag();
            DisplayService.MoveWindow(window, -30000, -30000);
            await Settle(window);
            var previous = Named<Button>(window, "CompactPreviousButton");
            var play = Named<Button>(window, "CompactPlayButton");
            var next = Named<Button>(window, "CompactNextButton");
            var controls = Named<StackPanel>(window, "CompactPlaybackControls");
            foreach (Button button in new[] { previous, play, next })
            {
                Check(!Ancestors(button).OfType<Button>().Any(), button.Name + " owns an independent control");
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), button.Name + " has an accessible name");
                Check(button.Focusable && button.FocusVisualStyle is not null, button.Name + " retains a keyboard focus visual");
                button.ApplyTemplate();
                Check(button.Template.FindName("ButtonSurface", button) is Border border && border.BorderThickness == new Thickness(),
                    button.Name + " template does not draw a mouse-focus outline");
                Check(!button.Template.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty),
                    button.Name + " has no persistent IsKeyboardFocused border trigger");
            }
            for (int flags = 0; flags < 16; flags++)
            {
                bool hasSession = (flags & 8) != 0, canToggle = (flags & 1) != 0, canPrevious = (flags & 2) != 0, canNext = (flags & 4) != 0;
                Inject(window, new MediaSnapshot("A deliberately long synthetic track title for truncation", "Synthetic artist", false,
                    hasSession, canToggle, canNext, canPrevious, null));
                await Settle(window);
                Check(play.IsEnabled == (hasSession && canToggle) && previous.IsEnabled == (hasSession && canPrevious) && next.IsEnabled == (hasSession && canNext),
                    "All collapsed transport buttons honor exact session capabilities: " + flags);
            }
            var playGlyph = Named<System.Windows.Shapes.Path>(window, "CompactPlayIcon").Data.ToString();
            Inject(window, new MediaSnapshot("Night Drive · synthetic preview", "Synthetic artist", true, true, true, true, true, null));
            await Settle(window);
            Check(Named<System.Windows.Shapes.Path>(window, "CompactPlayIcon").Data.ToString() != playGlyph,
                "The transport glyph changes from play to pause with the session state");
            Check(AutomationProperties.GetName(play) == "Пауза",
                "The accessible play action follows the pause state");
            typeof(MainWindow).GetField("_busy", Private)!.SetValue(window, true);
            typeof(MainWindow).GetMethod("UpdateMediaAvailability", Private)!.Invoke(window, null);
            await Settle(window);
            Check(!play.IsEnabled && !previous.IsEnabled && !next.IsEnabled, "An in-flight media command disables transport");
            typeof(MainWindow).GetField("_busy", Private)!.SetValue(window, false);
            typeof(MainWindow).GetMethod("UpdateMediaAvailability", Private)!.Invoke(window, null);

            foreach (bool battery in new[] { false, true })
            {
                typeof(MainWindow).GetMethod("ApplyBattery", Private)!.Invoke(window, new object[] { new BatterySnapshot(battery, 100, false, false) });
                Named<TextBlock>(window, "CompactClock").Text = "23:59";
                window.ApplyPerformance(new PerformanceSnapshot(100, 100, 100, 1, 2, 99, "Synthetic fixture", 99, "Synthetic fixture", 99, "Synthetic fixture"));
                window.ApplyFps(new FpsSnapshot(999, "Synthetic fixture", "Synthetic fixture"));
                await Settle(window);
                Check(controls.Visibility == Visibility.Visible && previous.ActualWidth == 24,
                    "Transport buttons fit the capsule: " + battery);
                var title = Named<TextBlock>(window, "CompactTitle");
                Check(title.ActualWidth >= 60 && title.TextTrimming == TextTrimming.CharacterEllipsis,
                    "Track title retains readable width and ellipsis with clock/battery: " + battery);
                var surface = Named<Border>(window, "IslandSurface");
                foreach (Button button in new[] { previous, play, next })
                {
                    Rect bounds = button.TransformToAncestor(surface).TransformBounds(new Rect(new Point(), button.RenderSize));
                    Check(bounds.Left >= 0 && bounds.Right <= surface.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= surface.ActualHeight,
                        button.Name + " stays inside the capsule: " + battery);
                    Point center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
                    var hit = window.InputHitTest(center) as DependencyObject;
                    Check(hit is not null && (ReferenceEquals(hit, button) || Ancestors(hit).Contains(button)),
                        button.Name + " owns its own hit target: " + battery);
                }
            }
            Inject(window, new MediaSnapshot("Night Drive", "Luma Sessions", true, true, true, true, true, Artwork()));
            Named<TextBlock>(window, "CompactClock").Text = "21:48";
            typeof(MainWindow).GetMethod("ApplyBattery", Private)!.Invoke(window, new object[] { new BatterySnapshot(true, 82, false, false) });
            window.ApplyPerformance(new PerformanceSnapshot(18, 34, 42, 28862180229, 68719476736, 54, "Synthetic fixture", 58, "Synthetic fixture", 41, "Synthetic fixture"));
            window.ApplyFps(new FpsSnapshot(144, "Synthetic fixture", "Synthetic fixture"));
            await Settle(window);
            window.Opacity = 1;
            Save(window, Path.Combine(Output, "normal-controls.png"), 2);
            window.Opacity = 0;
            Check(DisplayService.GetWindowBounds(window).Right < 0, "Capture remains offscreen");
            window.SetComponents(settings with { Music = false });
            await Settle(window);
            Check(controls.Visibility == Visibility.Collapsed && controls.DesiredSize.Width == 0, "Disabling music removes collapsed controls and returns their width to the title");
            window.SetComponents(settings);
            await Settle(window);
            Check(controls.Visibility == Visibility.Visible, "Re-enabling music restores transport controls");

            Check(window.FindName("CompactExpandButton") is null && window.FindName("ExpandedView") is null &&
                  Named<Grid>(window, "CompactView").IsVisible,
                "Music is presented in a single available view");
            Check(Named<FrameworkElement>(window, "CompactVolume").IsVisible && Named<Slider>(window, "VolumeControl").IsVisible &&
                  Named<Button>(window, "MuteButton").IsVisible,
                "Volume and mute are available alongside the transport");
            double heightWithVolume = window.CollapsedWindowHeight;
            window.SetComponents(settings with { Volume = false, VolumeWheel = true });
            await Settle(window);
            Check(!Named<FrameworkElement>(window, "CompactVolume").IsVisible &&
                  !Named<Slider>(window, "VolumeControl").IsVisible && !Named<Button>(window, "MuteButton").IsVisible &&
                  window.CollapsedWindowHeight < heightWithVolume,
                "Hiding the volume row removes both controls and reclaims its height");
            Check(controls.IsVisible && play.IsVisible && window.Components.VolumeWheel,
                "Music transport and wheel preference remain available without the volume row");

            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            play.RaiseEvent(wheel);
            Check(!wheel.Handled, "The wheel event is left unhandled with the isolated audio endpoint unavailable");

            window.SetComponents(settings);
            await Settle(window);
            Check(Named<FrameworkElement>(window, "CompactVolume").IsVisible && !window.Components.VolumeWheel,
                "The volume row can be restored with wheel control disabled");
            CheckWheelStep();
            Check(media.Current == MediaSnapshot.Empty && typeof(MediaService).GetField("_initialization", Private)!.GetValue(media) is null,
                "Synthetic UI updates never initialize or change the real media session");
        }
        finally { window.Opacity = 0; window.Close(); }
        Check(closed, "The offscreen window closed cleanly");
        Check(preferences.OrderBy(x => x.Key).SequenceEqual(RealPreferences().OrderBy(x => x.Key)), "Real user preferences are unchanged");
    }

    private static void CheckWheelStep()
    {
        var calculate = typeof(MainWindow).GetMethod("VolumeAfterWheel", BindingFlags.Static | BindingFlags.NonPublic)!;
        float Adjust(float current, int delta) => (float)calculate.Invoke(null, new object[] { current, delta })!;
        Check(Math.Abs(Adjust(.50f, 120) - .52f) < .0001f && Math.Abs(Adjust(.50f, -120) - .48f) < .0001f,
            "One wheel notch increases or decreases volume by two percentage points");
        Check(Math.Abs(Adjust(.50f, 60) - .51f) < .0001f && Math.Abs(Adjust(.50f, -240) - .46f) < .0001f,
            "Fractional and multiple wheel notches scale the adjustment");
        Check(Adjust(.99f, 120) == 1f && Adjust(.01f, -120) == 0f,
            "Wheel volume is clamped to the endpoint range");
    }

    private static void CheckDragTargets(MainWindow window)
    {
        var canDrag = typeof(MainWindow).GetMethod("CanStartSurfaceDrag", Private)!;
        bool Allowed(DependencyObject source) => (bool)canDrag.Invoke(window, new object[] { source })!;
        var surface = Named<Border>(window, "IslandSurface");
        Check(Allowed(surface) && Allowed(Named<Grid>(window, "CompactMusicRow")),
            "The capsule background and music area are drag origins");
        Check(!Allowed(Named<Button>(window, "CompactPlayButton")) &&
              !Allowed(Named<Button>(window, "MuteButton")) && !Allowed(Named<Slider>(window, "VolumeControl")),
            "Playback and volume inputs do not initiate a window drag");
        Check(!Allowed(new Border()), "A visual outside the capsule cannot initiate a drag");
        window.Placement.SetLocked(true);
        Check(!Allowed(surface), "The placement lock blocks dragging the background");
        window.Placement.SetLocked(false);
        window.InitializeInputRecovery(true, true);
        Check(window.SetClickThrough(true) && !Allowed(surface), "Click-through mode blocks dragging the background");
        Check(window.SetClickThrough(false) && Allowed(surface), "Interactive recovery restores dragging");
    }

    private static T Named<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Inject(MainWindow window, MediaSnapshot state) => typeof(MainWindow).GetMethod("OnMediaChanged", Private)!.Invoke(window, new object[] { state });
    private static async Task Settle(MainWindow window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject value)
    { for (var parent = VisualTreeHelper.GetParent(value); parent is not null; parent = VisualTreeHelper.GetParent(parent)) yield return parent; }
    private static void Save(MainWindow window, string path, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width * scale), (int)Math.Ceiling(window.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window); bitmap.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static BitmapSource Artwork()
    {
        // Original vector fixture; no player metadata or remote artwork is used in public previews.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(30, 66, 67), Color.FromRgb(7, 17, 29), 90), null, new Rect(0, 0, 64, 64));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(191, 233, 215)), null, new Point(45, 18), 7, 7);
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(64, 119, 116)), null, System.Windows.Media.Geometry.Parse("M 0,53 L 22,21 L 46,64 L 0,64 Z"));
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(22, 53, 63)), null, System.Windows.Media.Geometry.Parse("M 19,64 L 42,32 L 64,57 L 64,64 Z"));
        }
        var result = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }
    private static Dictionary<string, string?> RealPreferences()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json" }.ToDictionary(name => name,
            name => File.Exists(Path.Combine(root, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))) : null);
    }
    private static string FindRepository()
    { for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, "src", "Luma.Island", "MainWindow.xaml.cs"))) return directory.FullName; throw new DirectoryNotFoundException("Repository root not found."); }
    private static void Check(bool pass, string label) { if (!pass) throw new InvalidOperationException("FAIL: " + label); Checks.Add(label); }
}
