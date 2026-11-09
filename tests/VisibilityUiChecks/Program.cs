using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static string Output = null!;
    private static bool? ShellRegistered;

    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(FindRepository(), "work", "visibility-checks"));
        Directory.CreateDirectory(Output);
        var preferencesBefore = RealPreferences();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await RunAsync();
                Check(preferencesBefore.OrderBy(p => p.Key).SequenceEqual(RealPreferences().OrderBy(p => p.Key)),
                    "Real user preference files remain unchanged");
            }
            catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(ex); }
            finally
            {
                var assembly = typeof(MainWindow).Assembly;
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks,
                    version = assembly.GetName().Version?.ToString(), assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                    shellIconRegistered = ShellRegistered,
                    isolation = "Invisible nonactivating HWND; isolated preferences; read-only autostart; no SMTC initialization, playback/audio commands, global hotkey registration, broadcast messages, visible context menus or real mouse/keyboard input. One temporary tray icon is removed at disposal; actual callbacks are synthesized to the test HWND only.",
                    failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        Console.WriteLine(failure is null ? $"PASS: {Checks.Count} visibility UI checks." : "FAIL: visibility UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        string fixtures = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var appearance = new AppearanceStore(Path.Combine(fixtures, "appearance.json"));
        var components = new ComponentStore(Path.Combine(fixtures, "components.json"));
        var placement = new PlacementStore(Path.Combine(fixtures, "placement.json"));
        Check(components.Save(ComponentSettings.Default with { Music = false, Battery = false }), "Isolated component preferences saved");
        using var media = new MediaService();
        using var system = new SystemService();
        system.Dispose();
        var window = new MainWindow(media, system, placement, appearance, components,
            new AutostartService(readOnly: true)) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        TrayService? tray = null;
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            uint sourceStyle = 0;
            Rect sourceBounds = Rect.Empty;
            window.SourceInitialized += (_, _) =>
            {
                sourceStyle = ExtendedStyle(new WindowInteropHelper(window).Handle);
                sourceBounds = DisplayService.GetWindowBounds(window);
            };
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            uint hiddenStyle = ExtendedStyle(hwnd);
            Check(!window.IsVisible && (hiddenStyle & 0x80) != 0 && (hiddenStyle & 0x40000) == 0,
                "Tool-window style excludes the capsule from Alt+Tab before its first Show");
            Check(hiddenStyle == ((sourceStyle | 0x80u) & ~0x40000u),
                "Only TOOLWINDOW and APPWINDOW bits change during native source initialization");
            SameBounds(sourceBounds, DisplayService.GetWindowBounds(window), "Applying the initial style preserves hidden native window bounds");
            var playButton = (UIElement)window.FindName("CompactPlayButton");
            var dragSurface = (UIElement)window.FindName("IslandSurface");
            Check((hiddenStyle & 0x08000000) == 0 && window.IsEnabled && playButton.Focusable && dragSurface.IsHitTestVisible,
                "The capsule retains keyboard and pointer input without WS_EX_NOACTIVATE");
            window.Show();
            await Settle();
            CheckToolWindow(window, "after first Show");
            var surface = (Border)window.FindName("IslandSurface");
            MenuItem capsule = surface.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Скрыть в трей"));
            Check(capsule is not null, "The production Loaded hook installs the capsule visibility command");
            // Exercise idempotence after first checking the production Loaded hook.
            var initialize = typeof(MainWindow).GetMethod("InitializeTrayVisibility", BindingFlags.Instance | BindingFlags.NonPublic)!;
            initialize.Invoke(window, null);
            initialize.Invoke(window, null);
            Check(surface.ContextMenu.Items.OfType<MenuItem>().Count(item => Equals(item.Header, "Скрыть в трей")) == 1,
                "Repeated initialization never duplicates the visibility command");
            Check(Equals(capsule.InputGestureText, "Ctrl+Alt+Space"), "Capsule hide command advertises its visibility hotkey");
            Check(surface.ContextMenu.Items.OfType<MenuItem>().Select(item => (string)item.Header)
                .SequenceEqual(new[] { "Скрыть в трей", "Пропускать клики в игру", "Редактировать HUD…", "Настройки…", "Выход" }),
                "The capsule menu includes the live layout action");
            Check(window.IsVisible && window.Opacity == 0 && !window.ShowActivated && !window.ShowInTaskbar,
                "The native test window stays transparent, nonactivating and absent from taskbar");
            tray = new TrayService(window, window.ToggleTrayVisibility, () => { }, showVisible: window.RestoreFromTray);
            ShellRegistered = tray.IsRegistered;
            var trayMenu = (ContextMenu)typeof(TrayService).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
            MenuItem trayItem = (MenuItem)trayMenu.Items[0];
            Check(Equals(trayItem.Header, "Скрыть в трей") && Equals(trayItem.InputGestureText, "Ctrl+Alt+Space"),
                "Tray menu clearly labels the hide action and hotkey");
            window.SetTrayHotkeyStatus(false);
            tray.SetHotkeyStatus(false);
            Check(!window.TrayHotkeyAvailable && string.IsNullOrEmpty(capsule.InputGestureText) && string.IsNullOrEmpty(trayItem.InputGestureText),
                "An unavailable hotkey is not advertised as usable in either menu");
            Check(capsule.ToolTip is string unavailable && unavailable.Contains("занято", StringComparison.Ordinal) &&
                trayItem.ToolTip is string trayUnavailable && trayUnavailable.Contains("занято", StringComparison.Ordinal),
                "Both menus explain hotkey conflicts without a modal dialog");
            window.SetTrayHotkeyStatus(true);
            tray.SetHotkeyStatus(true);
            Check(window.TrayHotkeyAvailable && Equals(capsule.InputGestureText, "Ctrl+Alt+Space") && Equals(trayItem.InputGestureText, "Ctrl+Alt+Space"),
                "The hotkey status can be refreshed after successful registration");

            window.Placement.SetPreset(IslandPlacement.BottomRight);
            Rect original = DisplayService.GetWindowBounds(window);
            Click(capsule);
            Check(!window.IsVisible && new WindowInteropHelper(window).Handle == hwnd, "Capsule menu hides without destroying its HWND");
            Check(tray.IsRegistered == ShellRegistered, "Hiding retains notification-icon registration state");
            Check(Equals(capsule.Header, "Показать капсулу") && Equals(trayItem.Header, "Показать капсулу"),
                "Both menus immediately advertise restoration while hidden");
            Click(trayItem);
            await Settle();
            Check(window.IsVisible && new WindowInteropHelper(window).Handle == hwnd, "Tray command restores the same window");
            CheckToolWindow(window, "after tray restoration");
            SameBounds(original, DisplayService.GetWindowBounds(window), "Restoration preserves the saved bottom-right position");

            foreach (var preset in new[] { IslandPlacement.TopLeft, IslandPlacement.Center, IslandPlacement.BottomRight })
            {
                window.Placement.SetPreset(preset);
                Rect before = DisplayService.GetWindowBounds(window);
                for (int i = 0; i < 3; i++)
                {
                    window.ToggleTrayVisibility();
                    Check(!window.IsVisible, $"Toggle hides preset={preset} cycle={i}");
                    window.ToggleTrayVisibility();
                    Check(window.IsVisible, $"Toggle restores preset={preset} cycle={i}");
                }
                SameBounds(before, DisplayService.GetWindowBounds(window), $"Visibility cycles keep preset={preset} geometry");
                CheckToolWindow(window, $"after visibility cycles preset={preset}");
            }

            Rect beforeHide = DisplayService.GetWindowBounds(window);
            window.HideToTray();
            Check(!window.IsVisible && Math.Abs(window.Height - window.CollapsedWindowHeight) < .01,
                "Hiding keeps the HUD sized to its selected components");
            window.RestoreFromTray();
            await Settle();
            Check(((FrameworkElement)window.FindName("CompactView")).IsVisible,
                "Restoring displays the compact HUD directly");
            SameBounds(beforeHide, DisplayService.GetWindowBounds(window), "Restoring keeps native position and dimensions");
            CheckToolWindow(window, "after compact HUD restoration");
            window.HideToTray();
            CheckToolWindow(window, "while hidden");

            var version4 = typeof(TrayService).GetField("_usingVersion4", BindingFlags.Instance | BindingFlags.NonPublic)!;
            version4.SetValue(tray, true);
            Notify(hwnd, 0x0400); // NIN_SELECT: synthesize only to this HWND, not the real shell/input.
            Check(window.IsVisible, "A version-4 mouse selection restores the hidden capsule");
            Notify(hwnd, 0x0400);
            Check(window.IsVisible, "Repeated mouse selection is idempotent and cannot hide on double click");
            window.HideToTray();
            Notify(hwnd, 0x0401);
            Check(window.IsVisible, "Keyboard tray selection restores the capsule");
            version4.SetValue(tray, false);
            window.HideToTray();
            Notify(hwnd, 0x0202);
            Check(window.IsVisible, "Legacy tray callback restores with one mouse-button release");

            window.HideToTray();
            uint restoreMessage = RegisterWindowMessage("LumaIsland.Restore.v1");
            Check(restoreMessage != 0, "Single-instance restore message is registered");
            SendMessage(hwnd, restoreMessage, IntPtr.Zero, IntPtr.Zero);
            Check(window.IsVisible, "The existing-instance IPC callback restores its own hidden HWND");
            SendMessage(hwnd, restoreMessage, IntPtr.Zero, IntPtr.Zero);
            Check(window.IsVisible, "Repeated instance-restoration requests keep one visible window");
            CheckToolWindow(window, "after instance-restoration requests");

            window.HideToTray();
            window.HideToTray();
            Check(!window.IsVisible, "Explicit hide is idempotent");
            window.RestoreFromTray();
            window.RestoreFromTray();
            Check(window.IsVisible && !trayMenu.IsOpen && !surface.ContextMenu.IsOpen, "Explicit restoration is idempotent and leaves menus closed");
            tray.Dispose();
            Check(!tray.IsRegistered, "Tray disposal unregisters the notification icon");
            tray.Dispose();
            window.Close();
            Check(closed, "The test window closes normally after visibility cycles");
            window.RestoreFromTray();
            window.HideToTray();
            window.ToggleTrayVisibility();
            Check(!window.IsVisible, "Visibility APIs do not recreate a closed window");
            CheckWindowStyleVariants(media, system, placement, appearance, components);
        }
        finally
        {
            tray?.Dispose();
            if (!closed) window.Close();
        }
    }

    private static void CheckWindowStyleVariants(MediaService media, SystemService system, PlacementStore placement,
        AppearanceStore appearance, ComponentStore components)
    {
        var debug = new MainWindow(media, system, placement, appearance, components, new AutostartService(readOnly: true))
            { ShowInTaskbar = true, ShowActivated = false, Topmost = false, Opacity = 0 };
        try
        {
            uint source = 0;
            debug.SourceInitialized += (_, _) => source = ExtendedStyle(new WindowInteropHelper(debug).Handle);
            IntPtr handle = new WindowInteropHelper(debug).EnsureHandle();
            Check(ExtendedStyle(handle) == source && (source & 0x80) == 0 && (source & 0x40000) != 0 && !debug.IsVisible,
                "An explicit taskbar/debug window keeps its original APPWINDOW style and remains unshown");
        }
        finally { debug.Close(); }

        var topmost = new MainWindow(media, system, placement, appearance, components, new AutostartService(readOnly: true))
            { ShowActivated = false, ShowInTaskbar = false, Topmost = true, Opacity = 0 };
        try
        {
            new WindowInteropHelper(topmost).EnsureHandle();
            topmost.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, topmost));
            topmost.Placement.BeginDrag();
            DisplayService.MoveWindow(topmost, -30000, -30000);
            topmost.Show();
            uint style = ExtendedStyle(new WindowInteropHelper(topmost).Handle);
            Check(topmost.Topmost && (style & 0x8) != 0 && (style & 0x80) != 0 && (style & 0x08000000) == 0 &&
                DisplayService.GetWindowBounds(topmost).X < -10000,
                "The real overlay's topmost flag survives TOOLWINDOW styling without activating or moving its offscreen fixture");
            topmost.HideToTray(); topmost.RestoreFromTray();
            Check(topmost.Topmost && (ExtendedStyle(new WindowInteropHelper(topmost).Handle) & 0x8) != 0,
                "Hide and restore preserve topmost behavior");
        }
        finally { topmost.Close(); }
    }

    private static uint ExtendedStyle(IntPtr window) => unchecked((uint)GetWindowLongPtr(window, -20).ToInt64());
    private static void CheckToolWindow(MainWindow window, string stage)
    {
        uint style = ExtendedStyle(new WindowInteropHelper(window).Handle);
        Check((style & 0x80) != 0 && (style & 0x40000) == 0 && (style & 0x08000000) == 0 && (style & 0x8) == 0 &&
            window.IsEnabled && !window.Topmost, "Tool-window exclusion and enabled nonactivating input persist " + stage);
    }

    private static void Notify(IntPtr hwnd, int notification) => SendMessage(hwnd, 0x8000 + 42, IntPtr.Zero, new IntPtr((42 << 16) | notification));
    private static async Task Settle()
    {
        await Task.Delay(25);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    private static void SameBounds(Rect expected, Rect actual, string text) => Check(
        Math.Abs(expected.X - actual.X) <= 1 && Math.Abs(expected.Y - actual.Y) <= 1 &&
        Math.Abs(expected.Width - actual.Width) <= 1 && Math.Abs(expected.Height - actual.Height) <= 1, text);
    private static void Check([DoesNotReturnIf(false)] bool passed, string text)
    {
        if (!passed) throw new InvalidOperationException("FAIL: " + text);
        Checks.Add(text);
    }
    private static Dictionary<string, string> RealPreferences()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json" }.ToDictionary(name => name, name =>
        {
            string path = Path.Combine(folder, name);
            return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
        });
    }
    private static string FindRepository()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "src", "Luma.Island", "MainWindow.xaml.cs"))) return folder.FullName;
        throw new DirectoryNotFoundException("Overlay repository root was not found.");
    }
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
