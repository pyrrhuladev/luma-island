using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;

internal static class Program
{
    private const uint InputMask = 0x20 | 0x08000000;
    private static readonly List<string> Checks = new();
    private static readonly List<object> NativeHits = new();
    private static string Output = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--hit-target")
            return NativeTarget.Run(int.Parse(args[1], CultureInfo.InvariantCulture), int.Parse(args[2], CultureInfo.InvariantCulture));
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(FindRepository(), "work", "click-through-checks"));
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
                    "Real user preferences remain byte-for-byte unchanged");
            }
            catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(ex); }
            finally
            {
                var assembly = typeof(MainWindow).Assembly;
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks,
                    version = assembly.GetName().Version?.ToString(),
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                    nativeHitSelection = NativeHits,
                    isolation = "Isolated preferences, read-only autostart, no SMTC initialization, hardware access, real hotkey registration, broadcast messages or global mouse/keyboard input. Most HWNDs have zero opacity. Native routing proof briefly exposes only an 8x8 clipped opaque region away from the cursor over a task-owned child-process target; both nonactivating windows are removed in finally. WindowFromPoint proves OS window selection across processes, not a physical mouse click.",
                    failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        Console.WriteLine(failure is null ? $"PASS: {Checks.Count} click-through UI checks." : "FAIL: click-through UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        string fixtures = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        string appearancePath = Path.Combine(fixtures, "appearance.json");
        var appearance = new AppearanceStore(appearancePath);
        var components = new ComponentStore(Path.Combine(fixtures, "components.json"));
        var placement = new PlacementStore(Path.Combine(fixtures, "placement.json"));
        var initial = new AppearanceSettings(IslandAppearance.Glass, .4, false);
        Check(!AppearanceSettings.Default.ClickThrough && !appearance.Load().ClickThrough, "Click-through defaults to off");
        File.WriteAllText(appearancePath, "{\"mode\":\"Glass\",\"glassOpacity\":0.35,\"compact\":true}");
        Check(appearance.Load() == new AppearanceSettings(IslandAppearance.Glass, .35, true, false),
            "Legacy appearance JSON preserves existing values and defaults the new input preference to off");
        Check(appearance.Save(initial), "Isolated appearance fixture saved");
        Check(components.Save(ComponentSettings.Default with { Music = false, Battery = false }), "Isolated component fixture saved");
        using var media = new MediaService();
        using var system = new SystemService();
        system.Dispose();
        MainWindow Create() => new(media, system, placement, appearance, components, new AutostartService(readOnly: true))
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        var window = Create();
        TrayService? tray = null;
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            Check(!window.IsClickThrough && (Style(hwnd) & InputMask) == 0, "The initial native HWND retains normal interaction");
            var beforeRecovery = window.CreateInputMenuItem();
            Check(!beforeRecovery.IsChecked && !beforeRecovery.IsEnabled && string.IsNullOrEmpty(beforeRecovery.InputGestureText),
                "Input toggle remains disabled until a recovery route has been checked");
            Check(!window.SetClickThrough(true) && !window.IsClickThrough && !appearance.Load().ClickThrough,
                "Enabling without any initialized recovery route is refused without persisting an unsafe preference");
            window.Show();
            await Settle();
            var surface = Named<Border>(window, "IslandSurface");
            var capsuleItem = surface.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Пропускать клики в игру"));
            tray = new TrayService(window, window.ToggleTrayVisibility, () => { },
                showVisible: window.RestoreForInteraction, inputMenuFactory: window.CreateInputMenuItem);
            var trayMenu = (ContextMenu)typeof(TrayService).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
            var trayItem = trayMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Пропускать клики в игру"));
            string savedBeforeRecovery = File.ReadAllText(appearancePath);
            window.InitializeInputRecovery(true, true);
            Check(window.InputRecoveryAvailable && window.InputHotkeyAvailable && !window.IsClickThrough,
                "Available recovery routes keep a default profile interactive");
            Check(File.ReadAllText(appearancePath) == savedBeforeRecovery, "Recovery availability is transient and is not saved in appearance preferences");
            Check(capsuleItem.IsEnabled && trayItem.IsEnabled && Equals(capsuleItem.InputGestureText, "Ctrl+Alt+I") && Equals(trayItem.InputGestureText, "Ctrl+Alt+I"),
                "Both actual menus advertise the separate input-mode hotkey after successful registration");
            uint baseline = Style(hwnd);
            Rect bounds = DisplayService.GetWindowBounds(window);
            var savedPlacement = window.Placement.Settings;
            var savedComponents = window.Components;
            var notifications = new List<bool>();
            window.InputModeChanged += notifications.Add;
            capsuleItem.IsChecked = true;
            Click(capsuleItem);
            Check(window.IsClickThrough && (Style(hwnd) & InputMask) == InputMask,
                "The capsule menu enables native TRANSPARENT and NOACTIVATE bits");
            Check(Style(hwnd) == (baseline | InputMask), "Enabling changes only the two input style bits, preserving layered/tool-window and all unrelated flags");
            Check(capsuleItem.IsChecked && trayItem.IsChecked && beforeRecovery.IsChecked && notifications.Last(),
                "All input menu instances and the mode event reflect the actual applied state");
            Check(appearance.Load() == (initial with { ClickThrough = true }), "Enabling persists the input preference while preserving all existing appearance fields");
            SameBounds(bounds, DisplayService.GetWindowBounds(window), "Native input-mode changes preserve compact geometry");
            Check(window.Placement.Settings == savedPlacement && window.Components == savedComponents,
                "Input mode does not rewrite position or component choices");
            Check(Named<Button>(window, "CompactPlayButton").Focusable && surface.IsHitTestVisible && window.IsEnabled,
                "The mode uses native routing rather than disabling the WPF controls needed after unlocking");
            foreach (bool compact in new[] { false, true })
                foreach (IslandAppearance mode in Enum.GetValues<IslandAppearance>())
                {
                    window.SetCompactMode(compact);
                    window.SetAppearance(mode, .05);
                    window.UpdateClockAndBattery();
                    await Settle();
                    Check(window.IsClickThrough && appearance.Load().ClickThrough && (Style(hwnd) & InputMask) == InputMask,
                        $"Density and appearance changes preserve native routing: compact={compact}, mode={mode}");
                }
            window.ToggleClickThrough();
            Check(!window.IsClickThrough && (Style(hwnd) & InputMask) == 0 && !appearance.Load().ClickThrough && !capsuleItem.IsChecked && !trayItem.IsChecked,
                "Toggling restores native input and synchronizes saved preferences and both menus");
            Check((Style(hwnd) & ~InputMask) == (baseline & ~InputMask), "Unlocking retains every unrelated native style bit");
            SetWindowLongPtr(hwnd, -20, new IntPtr(Style(hwnd) | InputMask));
            Check(!window.IsClickThrough && (Style(hwnd) & InputMask) == InputMask && window.SetClickThrough(false) &&
                (Style(hwnd) & InputMask) == 0, "Explicit unlock clears leftover native bits even when the managed mode is already interactive");
            Rect beforeInputChange = DisplayService.GetWindowBounds(window);
            Check(window.SetClickThrough(true) && Named<Grid>(window, "CompactView").IsVisible,
                "Enabling click-through keeps the current compact view visible");
            SameBounds(beforeInputChange, DisplayService.GetWindowBounds(window), "Enabling click-through leaves the compact layout in place");
            window.HideToTray();
            window.RestoreFromTray();
            Check(window.IsVisible && window.IsClickThrough && (Style(hwnd) & InputMask) == InputMask,
                "The existing hide/show operation preserves the selected click-through mode");
            window.HideToTray();
            window.RestoreForInteraction();
            Check(window.IsVisible && !window.IsClickThrough && (Style(hwnd) & InputMask) == 0 && !appearance.Load().ClickThrough,
                "Explicit interactive restoration unlocks and shows the same HUD and persists the unlocked choice");
            Check(new WindowInteropHelper(window).Handle == hwnd, "Mode and visibility cycles retain the same native window");

            var version4 = typeof(TrayService).GetField("_usingVersion4", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (bool modern in new[] { true, false })
            {
                version4.SetValue(tray, modern);
                window.SetClickThrough(true);
                window.HideToTray();
                SendMessage(hwnd, 0x8000 + 42, IntPtr.Zero, new IntPtr((42 << 16) | (modern ? 0x0400 : 0x0202)));
                Check(window.IsVisible && !window.IsClickThrough && (Style(hwnd) & InputMask) == 0,
                    "The real tray callback restores interactive controls: version4=" + modern);
            }
            window.SetClickThrough(true);
            window.HideToTray();
            SendMessage(hwnd, RegisterWindowMessage("LumaIsland.Restore.v1"), IntPtr.Zero, IntPtr.Zero);
            Check(window.IsVisible && !window.IsClickThrough && !appearance.Load().ClickThrough,
                "A second-instance restore message unlocks the existing HUD without broadcasting to user windows");

            window.InitializeInputRecovery(false, true);
            Check(window.InputRecoveryAvailable && !window.InputHotkeyAvailable && capsuleItem.IsEnabled && trayItem.IsEnabled &&
                string.IsNullOrEmpty(capsuleItem.InputGestureText) && string.IsNullOrEmpty(trayItem.InputGestureText),
                "A hotkey conflict leaves tray recovery usable and stops advertising the unavailable shortcut");
            Check(capsuleItem.ToolTip is string text && text.Contains("недоступна", StringComparison.Ordinal) && text.Contains("трее", StringComparison.Ordinal),
                "The hotkey-conflict explanation includes the tray recovery route");
            Check(window.SetClickThrough(true), "Tray-only recovery can safely enable the mode");
            string beforeNoRecovery = File.ReadAllText(appearancePath);
            window.UpdateInputRecovery(false, false);
            Check(!window.InputRecoveryAvailable && !window.IsClickThrough && (Style(hwnd) & InputMask) == 0 && !capsuleItem.IsEnabled && !trayItem.IsEnabled,
                "Losing both recovery routes immediately returns native input and disables unsafe activation");
            Check(File.ReadAllText(appearancePath) == beforeNoRecovery && appearance.Load().ClickThrough,
                "Temporary recovery failure preserves the requested saved mode for a later successful startup");
            Check(!window.SetClickThrough(true) && !window.IsClickThrough, "The mode cannot be re-enabled while both recovery routes are unavailable");
            window.UpdateInputRecovery(true, false);
            Check(window.InputRecoveryAvailable && !window.IsClickThrough && (Style(hwnd) & InputMask) == 0 &&
                File.ReadAllText(appearancePath) == beforeNoRecovery,
                "A returning recovery route restores the menu without unexpectedly re-locking the HUD or clobbering the preference");
            Check(window.SetClickThrough(true) && (Style(hwnd) & InputMask) == InputMask,
                "After recovery returns, an explicit action can enable the mode again");
            window.RestoreForInteraction();
            var registrations = new List<bool>();
            tray.RegistrationChanged += available => { registrations.Add(available); window.UpdateInputRecovery(true, available); };
            bool registeredBeforeRestart = tray.IsRegistered;
            window.UpdateInputRecovery(true, registeredBeforeRestart);
            window.SetClickThrough(true);
            SendMessage(hwnd, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
            Check(!registeredBeforeRestart || registrations.Count > 0 && !registrations[0],
                "The production Explorer-restart callback invalidates an old registered icon before attempting to re-register it");
            Check((registrations.Count == 0 || registrations.Last() == tray.IsRegistered) &&
                !registrations.Zip(registrations.Skip(1)).Any(pair => pair.First == pair.Second),
                "Tray registration notifications reflect actual state changes without duplicate availability events");
            Check(window.IsClickThrough && window.InputHotkeyAvailable && window.InputRecoveryAvailable,
                "A tray restart does not unnecessarily unlock or change the preference when a working hotkey remains available");
            registrations.Clear();
            bool registeredBeforeDispose = tray.IsRegistered;
            tray.Dispose();
            Check(!tray.IsRegistered && (registeredBeforeDispose ? registrations.SequenceEqual(new[] { false }) : registrations.Count == 0),
                "Tray disposal reports loss of an existing registration exactly once");
            tray.Dispose();
            Check(registrations.Count == (registeredBeforeDispose ? 1 : 0), "Repeated tray disposal does not emit another registration-loss event");
            tray = null;
            window.RestoreForInteraction();
            window.Close();

            var retained = appearance.Load() with { Mode = IslandAppearance.Glass, GlassOpacity = .4, Compact = false, ClickThrough = true };
            Check(appearance.Save(retained), "Saved-enabled startup fixture prepared");
            string beforeStartup = File.ReadAllText(appearancePath);
            window = Create();
            hwnd = new WindowInteropHelper(window).EnsureHandle();
            Check(window.Appearance.ClickThrough && !window.IsClickThrough && (Style(hwnd) & InputMask) == 0,
                "A saved enabled choice cannot lock the window before recovery is initialized");
            window.Show(); await Settle();
            window.InitializeInputRecovery(false, false);
            Check(!window.IsClickThrough && File.ReadAllText(appearancePath) == beforeStartup,
                "A cold startup with no recovery leaves input available without destroying the saved preference");
            window.InitializeInputRecovery(true, false);
            Check(window.IsClickThrough && (Style(hwnd) & InputMask) == InputMask && File.ReadAllText(appearancePath) == beforeStartup,
                "A cold startup applies the saved mode only after a working recovery route is confirmed");
            window.RestoreForInteraction();
            await CheckNativeHitSelection(window);
        }
        finally
        {
            tray?.Dispose();
            window.Close();
        }
        Check(!window.SetClickThrough(true) && !window.IsVisible, "A closed window cannot be re-created or locked by a later mode request");
    }

    private static async Task CheckNativeHitSelection(MainWindow window)
    {
        window.SetClickThrough(false);
        window.SetAppearance(IslandAppearance.Matte, 1);
        await Settle();
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        var area = window.Placement.CurrentDisplay.WorkArea;
        GetCursorPos(out NativePoint cursor);
        var corners = new[] { new Point(area.Left + 24, area.Top + 24), new Point(area.Right - 24, area.Top + 24),
            new Point(area.Left + 24, area.Bottom - 24), new Point(area.Right - 24, area.Bottom - 24) };
        Point chosen = corners.OrderByDescending(p => Math.Pow(p.X - cursor.X, 2) + Math.Pow(p.Y - cursor.Y, 2)).First();
        var point = new NativePoint((int)Math.Round(chosen.X), (int)Math.Round(chosen.Y));
        var surface = Named<Border>(window, "IslandSurface");
        Point local = surface.TransformToAncestor(window).Transform(new Point(surface.ActualWidth / 2, 3));
        double scale = DisplayService.GetWindowScale(window);
        int localX = (int)Math.Round(local.X * scale), localY = (int)Math.Round(local.Y * scale);
        IntPtr region = CreateRectRgn(localX - 4, localY - 4, localX + 4, localY + 4);
        Check(region != IntPtr.Zero, "An eight-pixel native clipping region was allocated");
        Process? target = null;
        IntPtr targetHandle = IntPtr.Zero;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (SetWindowRgn(hwnd, region, true) == 0) throw new InvalidOperationException("Could not clip the test HWND before making it opaque.");
            region = IntPtr.Zero; // Windows owns the region after successful SetWindowRgn.
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--hit-target");
            start.ArgumentList.Add((point.X - 4).ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add((point.Y - 4).ToString(CultureInfo.InvariantCulture));
            target = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native target fixture.");
            string? ready = await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (ready is null) throw new InvalidOperationException("Native target fixture exited: " + await target.StandardError.ReadToEndAsync());
            targetHandle = new IntPtr(long.Parse(ready, CultureInfo.InvariantCulture));
            GetWindowThreadProcessId(targetHandle, out uint targetPid);
            Check(targetPid == target.Id && targetPid != Environment.ProcessId, "The tiny underlying target belongs to a separate task-owned process");
            window.Placement.BeginDrag();
            Check(SetWindowPos(hwnd, new IntPtr(-1), point.X - localX, point.Y - localY, 0, 0, 0x01 | 0x10),
                "The clipped fixture is positioned above its own target without activation");
            window.Opacity = 1;
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Task.Delay(60);
            Check(window.IsVisible && window.IsEnabled && window.Opacity == 1 && surface.Background is SolidColorBrush brush && brush.Color.A == 255,
                "Native routing uses a visible enabled opaque HUD, not transparency-by-zero-alpha or a hidden/disabled window");
            for (int cycle = 0; cycle < 2; cycle++)
            {
                CheckHit(point, hwnd, "interactive", cycle, hwnd);
                Check(window.SetClickThrough(true), "Native proof enables the actual production input-mode API: " + cycle);
                CheckHit(point, targetHandle, "passthrough", cycle, hwnd);
                Check(window.SetClickThrough(false), "Native proof unlocks through the actual production input-mode API: " + cycle);
                CheckHit(point, hwnd, "restored", cycle, hwnd);
            }
        }
        finally
        {
            window.Opacity = 0;
            if (region != IntPtr.Zero) DeleteObject(region);
            SetWindowRgn(hwnd, IntPtr.Zero, false);
            if (target is not null)
            {
                if (targetHandle != IntPtr.Zero) PostMessage(targetHandle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                if (!target.WaitForExit(1000)) { target.Kill(); target.WaitForExit(1000); }
                target.Dispose();
            }
        }
        Check(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "The task-owned eight-pixel fixture is removed within a bounded short verification interval");
    }

    private static void CheckHit(NativePoint point, IntPtr expected, string mode, int cycle, IntPtr hud)
    {
        IntPtr actual = WindowFromPoint(point);
        GetWindowThreadProcessId(actual, out uint pid);
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) throw new InvalidOperationException("Could not inspect the native fixture clipping region.");
        NativeRect clip;
        bool clipped;
        try { clipped = GetWindowRgn(hud, region) > 0 && GetRgnBox(region, out clip) > 0 &&
            clip.Right - clip.Left == 8 && clip.Bottom - clip.Top == 8; }
        finally { DeleteObject(region); }
        NativeHits.Add(new { mode, cycle, expected = expected.ToInt64(), actual = actual.ToInt64(), processId = pid, eightPixelClipRetained = clipped });
        Check(actual == expected && clipped, $"OS hit selection chooses the expected cross-process HWND while retaining the 8x8 clip in {mode}, cycle {cycle}; expected={expected}, actual={actual}");
    }

    private static T Named<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
    private static uint Style(IntPtr hwnd) => unchecked((uint)GetWindowLongPtr(hwnd, -20).ToInt64());
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    private static void SameBounds(Rect expected, Rect actual, string text) => Check(Math.Abs(expected.X - actual.X) <= 1 &&
        Math.Abs(expected.Y - actual.Y) <= 1 && Math.Abs(expected.Width - actual.Width) <= 1 && Math.Abs(expected.Height - actual.Height) <= 1, text);
    private static void Check([DoesNotReturnIf(false)] bool passed, string description)
    { if (!passed) throw new InvalidOperationException("FAIL: " + description); Checks.Add(description); }
    private static Dictionary<string, string?> RealPreferences()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json" }.ToDictionary(name => name, name =>
            File.Exists(Path.Combine(root, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))) : null);
    }
    private static string FindRepository()
    {
        for (DirectoryInfo? path = new(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "src", "Luma.Island", "MainWindow.xaml.cs"))) return path.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NativePoint
    { public int X, Y; public NativePoint(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect bounds);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
