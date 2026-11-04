using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
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
    private static readonly List<object> Geometry = new(), Captures = new();
    private static readonly ComponentSettings Basic = ComponentSettings.Default with { Battery = false };
    private static readonly ComponentSettings Metrics = Basic with { Cpu = true, Gpu = true, Memory = true, Fps = true, GpuTemperature = true, Charts = false };
    private static readonly MethodInfo ApplyMedia = typeof(MainWindow).GetMethod("OnMediaChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static string Output = null!;
    private static int Opened, Closed;
    private static bool ExercisedNativeAnimation;

    [STAThread]
    private static int Main(string[] args)
    {
        string root = FindRepository();
        Output = Path.GetFullPath(args.Length == 0 ? Path.Combine(root, "work", "compact-fix") : args[0]);
        Directory.CreateDirectory(Output);
        var preferencesBefore = RealPreferences();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await RunAsync();
                Check(preferencesBefore.OrderBy(p => p.Key).SequenceEqual(RealPreferences().OrderBy(p => p.Key)), "Real user preference files are unchanged");
            }
            catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(exception); }
            finally
            {
                var assembly = typeof(MainWindow).Assembly;
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null && Opened == Closed, checks = Checks.Count, completedChecks = Checks,
                    version = assembly.GetName().Version?.ToString(), assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                    windowsOpened = Opened, windowsClosed = Closed, animationPolicyEnabled = SystemParameters.ClientAreaAnimation,
                    exercisedNativeAnimation = ExercisedNativeAnimation, geometry = Geometry, captures = Captures,
                    dpiScope = "Native HWND placement at the current monitor DPI; text/layout and RenderTargetBitmap at 96/144/192 DPI. No display scaling settings changed.",
                    isolation = "No App startup, SMTC initialization, FPS/performance collectors, audio commands, autostart writes or real preference writes. Geometry windows have Opacity=0; captures are offscreen.",
                    failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        Console.WriteLine(failure is null ? $"PASS: {Checks.Count} compact UI checks." : "FAIL: compact UI checks.");
        return failure is null && Opened == Closed ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        string fixtures = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var appearance = new AppearanceStore(Path.Combine(fixtures, "appearance.json"));
        var components = new ComponentStore(Path.Combine(fixtures, "components.json"));
        var placement = new PlacementStore(Path.Combine(fixtures, "placement.json"));
        Check(appearance.Save(new AppearanceSettings(IslandAppearance.Glass, .76)), "Isolated appearance fixture saved");
        Check(components.Save(Basic), "Isolated component fixture saved");
        using var media = new MediaService(); // Never initialized: no system media session is requested.
        using var system = new SystemService();
        system.Dispose(); // Constructor reads volume once; all callbacks are removed before creating windows.
        MainWindow Create()
        {
            var autostart = new AutostartService(runKey: @"Software\LumaIsland\Tests\CompactUi\Run", approvalKey: @"Software\LumaIsland\Tests\CompactUi\Approval", readOnly: true);
            var result = new MainWindow(media, system, placement, appearance, components, autostart)
                { Opacity = 0, Topmost = false, ShowActivated = false, ShowInTaskbar = false };
            result.Closed += (_, _) => Closed++;
            Opened++;
            return result;
        }
        var window = Create();
        try
        {
            window.Show();
            await Settle(window);
            Check(!window.Topmost && !window.ShowActivated && window.Opacity == 0, "Native test HWND is invisible and never requests activation");
            Check(window.CollapsedWindowWidth == 408 && Surface(window).Width == 376, "Default density retains the 408/376 DIP widths");
            double normalBasicHeight = Surface(window).Height;
            MenuItem menu = window.CreateAppearanceMenu();
            var compactItem = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Компактная капсула"));
            Check(compactItem.IsCheckable && !compactItem.IsChecked, "Appearance menu exposes an unchecked compact checkbox");
            compactItem.IsChecked = true;
            compactItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, compactItem));
            await Settle(window);
            Check(window.Appearance.Compact && appearance.Load().Compact, "The real checkbox enables and persists compact mode");
            Check(window.Width == 352 && Surface(window).Width == 320 && Surface(window).Height < normalBasicHeight,
                "Compact HUD reduces the content height and keeps its 320 DIP surface inside a 352 DIP host");
            InjectMedia(window, "Synthetic long track title retained in compact mode", "Synthetic artist");
            Check(Text(window, "CompactTitle").Text == "Synthetic long track title retained in compact mode", "Compact mode preserves the music title");
            Check(Text(window, "CompactArtist").Visibility == Visibility.Collapsed, "Compact mode hides the artist line");
            foreach (var mode in new[] { IslandAppearance.Matte, IslandAppearance.Glass })
            {
                window.SetAppearance(mode, .25);
                InjectMedia(window, "Updated synthetic track", "Updated artist");
                Check(window.Appearance.Compact && Text(window, "CompactArtist").Visibility == Visibility.Collapsed, "Theme/media updates cannot restore the compact artist line: " + mode);
            }
            window.SetAppearance(IslandAppearance.Glass, .76);
            window.Close();
            window = Create();
            window.Show();
            await Settle(window);
            Check(window.Appearance.Compact && window.Width == 352, "A recreated native window restores compact mode from isolated preferences");
            Check(window.CreateAppearanceMenu().Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Компактная капсула")).IsChecked,
                "The compact checkbox is checked after window recreation");
            window.SetCompactMode(false);
            InjectMedia(window, "Synthetic fixture track", "Synthetic artist");
            Check(Text(window, "CompactArtist").Visibility == Visibility.Visible && window.Width == 408 && Surface(window).Width == 376,
                "Disabling compact mode restores the artist and original widths");
            window.SetCompactMode(true);
            window.SetComponents(Metrics);
            InjectMetrics(window);
            await Settle(window);
            Check(((Border)window.FindName("CompactArtworkFrame")).Width == 24 && ((Image)window.FindName("CompactArtwork")).Width == 24, "Compact artwork frame and image are 24 DIP");
            Check(new[] { "CompactCpu", "CompactGpu", "CompactMemory" }.All(name => Text(window, name).FontSize == 11), "Compact CPU/GPU/RAM metrics retain readable 11 DIP text");
            Check(((UniformGrid)window.FindName("CompactPerformance")).Columns == 3 && Text(window, "CompactGpu").Text.Contains('\n'),
                "CPU/GPU/RAM use three columns while FPS has its own headline");
            await CheckQuietHud(window);
            await CheckTransparency(window, appearance);

            foreach (IslandPlacement preset in Enum.GetValues<IslandPlacement>().Where(value => value != IslandPlacement.Custom))
            {
                window.Placement.SetPreset(preset);
                await Settle(window);
                var saved = window.Placement.Settings;
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    await Toggle(window);
                    CheckState(window, true, preset + " expanded " + cycle);
                    await Toggle(window);
                    CheckState(window, false, preset + " collapsed " + cycle);
                    Check(window.Placement.Settings == saved, "Animation never rewrites preset coordinates: " + preset + "/" + cycle);
                }
            }
            window.Placement.SetLocked(false);
            double scale = DisplayService.GetWindowScale(window);
            Rect target = PlacementMath.Bounds(window.Placement.CurrentDisplay.WorkArea, new Size(window.Width * scale, window.Height * scale), new Point(.31, .64), 8 * scale);
            window.Placement.BeginDrag();
            DisplayService.MoveWindow(window, target.X, target.Y);
            window.Placement.EndDrag();
            await Settle(window);
            Check(window.Placement.Settings.Placement == IslandPlacement.Custom && Math.Abs(window.Placement.Settings.XFraction - .31) < .005 && Math.Abs(window.Placement.Settings.YFraction - .64) < .005,
                "Synthetic native movement saves normalized custom coordinates");
            var custom = window.Placement.Settings;
            for (int cycle = 0; cycle < 2; cycle++)
            {
                await Toggle(window); CheckState(window, true, "Custom expanded " + cycle);
                await Toggle(window); CheckState(window, false, "Custom collapsed " + cycle);
                Check(window.Placement.Settings == custom, "Custom fractions do not accumulate drift: " + cycle);
            }
            window.ToggleExpanded(); await Task.Delay(35);
            ExercisedNativeAnimation |= ReadPrivate<bool>(window, "_animating");
            window.ToggleExpanded(); await Task.Delay(25);
            window.ToggleExpanded(); await window.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await Settle(window); CheckState(window, true, "Rapid three-toggle expanded endpoint");
            await Toggle(window); CheckState(window, false, "Rapid-toggle return endpoint");

            window.ToggleExpanded(); await Task.Delay(35); window.SetCompactMode(false);
            await Settle(window); CheckState(window, true, "Density change during expansion finishes at full expanded dimensions");
            await Toggle(window); CheckState(window, false, "Noncompact collapse after interrupted expansion");
            window.ToggleExpanded(); await Task.Delay(35); window.SetCompactMode(true);
            await Settle(window); CheckState(window, true, "Compact density change during expansion keeps the full expanded width");
            window.ToggleExpanded(); await Task.Delay(35); window.SetCompactMode(false);
            await Settle(window); CheckState(window, false, "Density change during collapse freezes at the correct noncompact endpoint");
            window.SetCompactMode(true); CheckState(window, false, "Compact endpoint restored after interrupted transitions");
            Check(window.Placement.Settings == custom, "Interrupted transitions preserve custom placement settings");

            // Freeze native placement while rendering normal-opacity content offscreen.
            window.Placement.BeginDrag();
            DisplayService.MoveWindow(window, -30000, -30000);
            window.Opacity = 1;
            InjectMedia(window, "Synthetic fixture track", "Synthetic artist"); InjectMetrics(window);
            foreach (bool expanded in new[] { false, true })
            {
                if (expanded) await Toggle(window);
                await Settle(window);
                foreach (double outputScale in new[] { 1d, 1.5, 2d })
                {
                    CheckTextBounds(window, expanded ? "ExpandedPerformance" : "CompactPerformance", outputScale);
                    var bitmap = Render(window, outputScale);
                    Check(bitmap.PixelWidth == (int)Math.Ceiling(window.Width * outputScale) && bitmap.PixelHeight == (int)Math.Ceiling(window.Height * outputScale), "Complete render bounds: " + expanded + "/" + outputScale);
                    Check(HasVisiblePixels(bitmap), "WPF capture contains visible pixels: " + expanded + "/" + outputScale);
                    string file = (expanded ? "expanded" : "compact") + "-" + (outputScale * 100).ToString("0", CultureInfo.InvariantCulture) + ".png";
                    Save(bitmap, Path.Combine(Output, file));
                    Captures.Add(new { mode = expanded ? "expanded" : "compact", dpi = outputScale * 96, bitmap.PixelWidth, bitmap.PixelHeight, file });
                }
                Check(DisplayService.GetWindowBounds(window).Right < 0, "Capture HWND stays offscreen");
                Check(GetForegroundWindow() != new WindowInteropHelper(window).Handle, "Test HWND never owns foreground focus");
            }
            await CaptureQuietHud(window);
            Check(media.Current == MediaSnapshot.Empty, "Synthetic UI updates never initialize or change the real media service");
            Check(typeof(MediaService).GetField("_initialization", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(media) is null,
                "No media initialization is invoked by this harness");
        }
        finally { window.Opacity = 0; window.Close(); }
    }

    private static void CheckState(MainWindow window, bool expanded, string label)
    {
        double width = expanded ? 408 : window.Appearance.Compact ? 352 : 408;
        double height = expanded ? window.ExpandedWindowHeight : window.CollapsedWindowHeight;
        double scale = DisplayService.GetWindowScale(window);
        Rect expected = PlacementMath.Bounds(window.Placement.CurrentDisplay.WorkArea, new Size(width * scale, height * scale), PlacementMath.Fractions(window.Placement.Settings), 8 * scale);
        Rect actual = DisplayService.GetWindowBounds(window);
        Rect desired = window.Placement.DesiredBounds(height, width);
        Check(Near(actual, expected, 1.1), label + ": native rectangle equals target without X/Y drift");
        Check(Near(desired, expected, .01), label + ": DesiredBounds uses the supplied target width");
        Check(Math.Abs(window.Width - width) < .01 && Math.Abs(window.Height - height) < .01, label + ": final host DIP size");
        Check(Math.Abs(Surface(window).Width - (expanded ? 384 : window.Appearance.Compact ? 320 : 376)) < .01, label + ": final surface width");
        var offset = (TranslateTransform)Surface(window).RenderTransform;
        Check(Math.Abs(offset.X) < .01 && Math.Abs(offset.Y) < .01 && !offset.HasAnimatedProperties, label + ": both animated offsets are cleared");
        Check(!ReadPrivate<bool>(window, "_animating"), label + ": no transition remains active");
        Geometry.Add(new { label, scale, actual = Rectangle(actual), expected = Rectangle(expected), compact = window.Appearance.Compact, expanded });
    }
    private static async Task CheckQuietHud(MainWindow window)
    {
        var hero = (FrameworkElement)window.FindName("CompactFpsPanel");
        var metrics = (UniformGrid)window.FindName("CompactPerformance");
        var music = (FrameworkElement)window.FindName("CompactMusicText");
        foreach (bool compact in new[] { false, true })
        {
            window.SetCompactMode(compact);
            window.SetComponents(Metrics);
            InjectMedia(window, "Synthetic fixture track", "Synthetic artist");
            InjectMetrics(window);
            await Settle(window);
            Check(Text(window, "CompactFps").Text == "999" && Text(window, "ExpandedFps").Text == "999 FPS" &&
                Text(window, "CompactFpsUnit").Text == "FPS", "Quiet HUD splits headline FPS value and unit without changing the expanded metric: " + compact);
            Check(Text(window, "CompactFps").FontSize >= 2 * Text(window, "CompactCpu").FontSize &&
                !metrics.Children.Contains(Text(window, "CompactFps")), "FPS remains a separate prominent headline: " + compact);
            Rect fpsBounds = hero.TransformToAncestor(Surface(window)).TransformBounds(new Rect(new Point(), hero.RenderSize));
            Rect metricBounds = metrics.TransformToAncestor(Surface(window)).TransformBounds(new Rect(new Point(), metrics.RenderSize));
            Rect musicBounds = music.TransformToAncestor(Surface(window)).TransformBounds(new Rect(new Point(), music.RenderSize));
            Check(fpsBounds.Bottom <= metricBounds.Top + 1 && metricBounds.Bottom <= musicBounds.Top + 1,
                "Quiet HUD places FPS above hardware metrics and music without overlap: " + compact);
            Rect clockBounds = Text(window, "CompactClock").TransformToAncestor(Surface(window)).TransformBounds(
                new Rect(new Point(), Text(window, "CompactClock").RenderSize));
            Check(fpsBounds.Left >= 0 && fpsBounds.Top >= 0 && fpsBounds.Right <= clockBounds.Left + 1 &&
                musicBounds.Bottom <= Surface(window).ActualHeight,
                "Headline FPS, clock and music remain inside the HUD without overlap: " + compact);
            Check(((Image)window.FindName("CompactArtwork")).Source is not null &&
                ((Border)window.FindName("CompactArtworkFrame")).Visibility == Visibility.Visible,
                "Quiet HUD retains actual album artwork rather than replacing it with branding: " + compact);

            window.SetComponents(Metrics with { Fps = false });
            await Settle(window);
            Check(hero.Visibility == Visibility.Collapsed && metrics.Visibility == Visibility.Visible && metrics.Columns == 3,
                "Disabling FPS removes its headline while retaining the three hardware metrics: " + compact);
            window.SetComponents(Basic with { Fps = true });
            await Settle(window);
            Check(hero.Visibility == Visibility.Visible && metrics.Visibility == Visibility.Collapsed &&
                !Text(window, "CompactCpu").IsVisible && !Text(window, "CompactGpu").IsVisible && !Text(window, "CompactMemory").IsVisible,
                "FPS-only mode removes the unused hardware row: " + compact);
            window.ApplyFps(new FpsSnapshot(null, null, "Synthetic unavailable source"));
            Check(Text(window, "CompactFps").Text == "—" && Text(window, "ExpandedFps").Text.Contains('—') &&
                Text(window, "CompactFps").ToolTip is string reason && reason.Contains("Synthetic unavailable source", StringComparison.Ordinal),
                "Unavailable headline FPS shows a dash and its real source status: " + compact);
        }
        window.SetComponents(new ComponentSettings(false, false, false, false, false, false, false, false, false));
        await Settle(window);
        Check(!window.HasPerformance && hero.Visibility == Visibility.Collapsed && metrics.Visibility == Visibility.Collapsed &&
            ((Border)window.FindName("CompactArtworkFrame")).Visibility == Visibility.Collapsed &&
            ((StackPanel)window.FindName("CompactPlaybackControls")).Visibility == Visibility.Collapsed,
            "Disabling components leaves no FPS, hardware, artwork or music controls in the collapsed HUD");
        window.SetCompactMode(true);
        window.SetComponents(Metrics);
        InjectMedia(window, "Synthetic fixture track", "Synthetic artist");
        InjectMetrics(window);
        await Settle(window);
    }
    private static async Task CheckTransparency(MainWindow window, AppearanceStore store)
    {
        AppearanceSettings original = window.Appearance;
        var placement = window.Placement.Settings;
        Rect bounds = DisplayService.GetWindowBounds(window);
        MenuItem menu = window.CreateAppearanceMenu();
        MenuItem control = menu.Items.OfType<MenuItem>().Single(item => item.Header is StackPanel panel &&
            panel.Children.OfType<Slider>().Any(slider => slider.Name == "BackgroundTransparencySlider"));
        var panel = (StackPanel)control.Header;
        Slider slider = panel.Children.OfType<Slider>().Single();
        Check(control.StaysOpenOnClick && slider.Minimum == 0 && slider.Maximum == 95 && slider.IsSnapToTickEnabled && slider.TickFrequency == 1,
            "The real appearance menu exposes a precise 0–95% background transparency slider");
        Check(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(slider)),
            "The background transparency slider has an accessible name");
        slider.Value = 46;
        await Settle(window);
        Check(window.Appearance.Mode == IslandAppearance.Glass && Math.Abs(window.Appearance.GlassOpacity - .54) < .0001 &&
            store.Load() == original && panel.Children.OfType<TextBlock>().Single().Text.Contains("46%", StringComparison.Ordinal),
            "Slider movement previews arbitrary transparency immediately without writing preferences");
        Check(window.Resources["SurfaceFill"] is SolidColorBrush preview && preview.Color.A == 138 && Surface(window).Opacity == 1 &&
            new[] { "CompactFps", "CompactTitle", "CompactCpu", "CompactClock" }.All(name => Text(window, name).Opacity == 1 &&
                Text(window, name).Foreground is SolidColorBrush brush && brush.Color.A == 255) &&
            ((System.Windows.Shapes.Path)window.FindName("CompactPlayIcon")).Opacity == 1,
            "Transparency changes only surface alpha while text and playback glyph opacity remain intact");
        slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Check(Math.Abs(store.Load().GlassOpacity - .54) < .0001 && store.Load().Compact == original.Compact,
            "Finishing a slider drag persists arbitrary 46% transparency without changing density");

        slider.Value = 95;
        Check(window.Resources["SurfaceFill"] is SolidColorBrush airy && airy.Color.A == 13 && Math.Abs(store.Load().GlassOpacity - .54) < .0001,
            "The 95% preview uses real background alpha and retains the last committed preference until release");
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Right) { RoutedEvent = Keyboard.KeyUpEvent };
        slider.RaiseEvent(input);
        Check(Math.Abs(store.Load().GlassOpacity - .05) < .0001, "Keyboard adjustment commits 95% transparency on key release");
        slider.Value = 0;
        Check(window.Resources["SurfaceFill"] is SolidColorBrush opaque && opaque.Color.A == 255,
            "The 0% endpoint restores a fully opaque surface");
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuClosedEvent, menu));
        Check(store.Load().Mode == IslandAppearance.Glass && store.Load().GlassOpacity == 1,
            "Closing the appearance submenu commits its final transparency preview");
        Check(window.Placement.Settings == placement && Near(DisplayService.GetWindowBounds(window), bounds, 1.1),
            "Transparency preview and commits preserve saved placement and native window bounds");
        window.SetAppearance(original.Mode, original.GlassOpacity);
    }
    private static async Task CaptureQuietHud(MainWindow window)
    {
        if (((FrameworkElement)window.FindName("ExpandedView")).Visibility == Visibility.Visible) await Toggle(window);
        window.SetComponents(ComponentSettings.Default with
        {
            Battery = false, Cpu = true, Gpu = true, Memory = true, Fps = true, Charts = true,
            CpuTemperature = true, GpuTemperature = true, MemoryTemperature = true
        });
        window.SetAppearance(IslandAppearance.Glass, .40);
        InjectMedia(window, "Night Drive", "Luma Sessions");
        ApplyMedia.Invoke(window, new object[] { new MediaSnapshot("Night Drive", "Luma Sessions", true, true, true, true, true,
            ((Image)window.FindName("CompactArtwork")).Source as BitmapSource) });
        window.ApplyPerformance(new PerformanceSnapshot(18, 65, 42, 28862180229, 68719476736, 61, "Synthetic fixture",
            58, "ok", 41, "ok"));
        window.ApplyFps(new FpsSnapshot(184, "Synthetic game", "Synthetic fixture"));
        Text(window, "CompactClock").Text = Text(window, "ExpandedClock").Text = "21:48";
        Text(window, "DateLabel").Text = "4 ноября";
        foreach (bool compact in new[] { false, true })
        {
            window.SetCompactMode(compact);
            await Settle(window);
            string file = compact ? "quiet-hud-compact.png" : "quiet-hud-normal.png";
            Save(Render(window, 2), Path.Combine(Output, file));
            Captures.Add(new { mode = compact ? "quiet-hud-compact" : "quiet-hud-normal", demonstrationData = true, backgroundTransparency = 60, file });
        }
        await Toggle(window);
        Save(Render(window, 2), Path.Combine(Output, "quiet-hud-expanded.png"));
        Captures.Add(new { mode = "quiet-hud-expanded", demonstrationData = true, backgroundTransparency = 60, file = "quiet-hud-expanded.png" });
        Check(DisplayService.GetWindowBounds(window).Right < 0, "All Quiet HUD demonstration captures remain offscreen");
    }
    private static void CheckTextBounds(MainWindow window, string panelName, double scale)
    {
        var panel = (Panel)window.FindName(panelName);
        var surface = Surface(window);
        var rounded = new RectangleGeometry(new Rect(new Point(), surface.RenderSize), surface.CornerRadius.TopLeft, surface.CornerRadius.TopLeft);
        foreach (TextBlock text in panel.Children.OfType<TextBlock>().Where(value => value.Visibility == Visibility.Visible))
        {
            var measured = new FormattedText(text.Text, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.White, scale);
            if (double.IsFinite(text.LineHeight)) measured.LineHeight = text.LineHeight;
            Check(measured.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1 && measured.Height <= text.ActualHeight + 1, text.Name + " fits at " + scale * 100 + "% output DPI");
            Rect bounds = text.TransformToAncestor(panel).TransformBounds(new Rect(new Point(), text.RenderSize));
            Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= panel.ActualWidth + 1 && bounds.Bottom <= panel.ActualHeight + 1, text.Name + " remains inside its metric row");
            Rect inside = text.TransformToAncestor(surface).TransformBounds(new Rect(new Point(), text.RenderSize));
            Check(rounded.FillContains(inside.TopLeft) && rounded.FillContains(inside.BottomRight), text.Name + " avoids rounded clipping");
        }
    }
    private static void InjectMetrics(MainWindow window)
    {
        window.ApplyPerformance(new PerformanceSnapshot(100, 100, 100, 34359738368, 34359738368, 99, "Synthetic fixture"));
        window.ApplyFps(new FpsSnapshot(999, "Synthetic fixture", "Synthetic fixture"));
        Text(window, "CompactClock").Text = Text(window, "ExpandedClock").Text = "21:48";
    }
    private static void InjectMedia(MainWindow window, string title, string artist)
    {
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, Enumerable.Range(0, 32 * 32).SelectMany(_ => new byte[] { 100, 190, 100, 255 }).ToArray(), 128);
        bitmap.Freeze();
        ApplyMedia.Invoke(window, new object[] { new MediaSnapshot(title, artist, true, true, false, false, false, bitmap) });
    }
    private static async Task Toggle(MainWindow window) { window.ToggleExpanded(); await window.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3)); await Settle(window); }
    private static async Task Settle(MainWindow window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static T ReadPrivate<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static Border Surface(MainWindow window) => (Border)window.FindName("IslandSurface");
    private static TextBlock Text(MainWindow window, string name) => (TextBlock)window.FindName(name);
    private static bool Near(Rect actual, Rect expected, double tolerance) => Math.Abs(actual.X - expected.X) <= tolerance && Math.Abs(actual.Y - expected.Y) <= tolerance && Math.Abs(actual.Width - expected.Width) <= tolerance && Math.Abs(actual.Height - expected.Height) <= tolerance;
    private static object Rectangle(Rect value) => new { value.X, value.Y, value.Width, value.Height };
    private static BitmapSource Render(MainWindow window, double scale) { var image = new RenderTargetBitmap((int)Math.Ceiling(window.Width * scale), (int)Math.Ceiling(window.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); image.Render(window); image.Freeze(); return image; }
    private static bool HasVisiblePixels(BitmapSource image) { var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0); for (int index = 3; index < pixels.Length; index += 4) if (pixels[index] != 0) return true; return false; }
    private static void Save(BitmapSource image, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Check(bool passed, string description) { if (!passed) throw new InvalidOperationException("FAIL: " + description); Checks.Add(description); }
    private static Dictionary<string, string?> RealPreferences()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json" }.ToDictionary(name => name, name => File.Exists(Path.Combine(root, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))) : null);
    }
    private static string FindRepository() { for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, "src", "Luma.Island", "MainWindow.xaml.cs"))) return directory.FullName; throw new DirectoryNotFoundException("Repository root not found."); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
