using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;
using ShapePath = System.Windows.Shapes.Path;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static string Output = null!;
    private static bool? ShellRegistered;
    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : "work/checks/QuickMenuUiChecks");
        Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(); }
            catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(error); }
            finally
            {
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks, failure,
                    version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))),
                    shellIconRegistered = ShellRegistered,
                    isolation = "Never-shown nonactivating fixture HWND; isolated preference paths; no App startup, real registry writes, SDK, media initialization, sensor collection, hotkey registration or input. One temporary non-balloon tray icon is disposed; no actual context menu or submenu is opened."
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} quick menu UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        string fixture = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        using var media = new MediaService(); using var system = new SystemService(); system.Dispose();
        var window = new MainWindow(media, system, new PlacementStore(Path.Combine(fixture, "placement.json")),
            new AppearanceStore(Path.Combine(fixture, "appearance.json")), new ComponentStore(Path.Combine(fixture, "components.json")),
            new AutostartService(runKey: @"Software\LumaIsland.Tests\QuickMenu\Run", approvalKey: @"Software\LumaIsland.Tests\QuickMenu\Approval", readOnly: true))
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        TrayService? tray = null;
        try
        {
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
            var capsule = ((Border)window.FindName("IslandSurface")).ContextMenu;
            var expected = new[] { "Показать капсулу", "Пропускать клики в игру", "Редактировать HUD…", "Настройки…", "Выход" };
            Check(!window.IsVisible && !window.ShowActivated && !window.ShowInTaskbar && GetForegroundWindow() != handle,
                "The production Loaded hook is exercised without displaying or activating the fixture HUD");
            Check(capsule.Items.Count == 6 && capsule.Items[4] is Separator && Labels(capsule).SequenceEqual(expected),
                "HUD exposes visibility, input, live layout, Settings and Exit");
            Check(capsule.Items.OfType<MenuItem>().All(item => !item.HasItems), "HUD quick actions have no duplicated settings submenus");
            Check(capsule.Items.OfType<MenuItem>().Count(item => Equals(item.Header, "Настройки…")) == 1,
                "HUD has a single unified Settings entry");
            Check(Equals(((MenuItem)capsule.Items[0]).InputGestureText, "Ctrl+Alt+Space"), "Visibility shortcut remains on the first action");
            var capsuleInput = (MenuItem)capsule.Items[1];
            Check(capsuleInput.IsCheckable && !capsuleInput.IsEnabled && !capsuleInput.IsChecked,
                "Click-through remains disabled until a recovery route is available");

            int legacyCalls = 0, settingsCalls = 0;
            MenuItem Legacy() { legacyCalls++; return new MenuItem { Header = "Legacy settings" }; }
            MenuItem Settings() { settingsCalls++; return window.CreateSettingsMenuItem(); }
            tray = new TrayService(window, () => { }, () => { },
                positionMenuFactory: Legacy, appearanceMenuFactory: Legacy, componentsMenuFactory: Legacy, autostartMenuFactory: Legacy,
                inputMenuFactory: window.CreateInputMenuItem, profilesMenuFactory: Legacy, hudEditorMenuFactory: Settings,
                capsuleEditorMenuFactory: window.CreateCapsuleEditMenuItem);
            ShellRegistered = tray.IsRegistered;
            var trayMenu = (ContextMenu)typeof(TrayService).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
            Check(legacyCalls == 0 && settingsCalls == 1, "Unified tray Settings avoids constructing legacy settings branches");
            Check(trayMenu.Items.Count == 6 && trayMenu.Items[4] is Separator && Labels(trayMenu).SequenceEqual(expected),
                "Tray and HUD present the same five quick actions in the same order");
            Check(capsule.Items.OfType<MenuItem>().Concat(trayMenu.Items.OfType<MenuItem>()).All(item => item.Icon is ShapePath { Data.IsFrozen: true }),
                "Each quick action has a crisp vector icon in both menus");
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            capsule.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, capsule));
            trayMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, trayMenu));
            var visibleLabels = new[] { "Скрыть в трей", "Пропускать клики в игру", "Редактировать HUD…", "Настройки…", "Выход" };
            Check(Labels(capsule).SequenceEqual(visibleLabels) && Labels(trayMenu).SequenceEqual(visibleLabels),
                "The visible HUD offers the hide action in both menus");
            window.HideToTray();
            capsule.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, capsule));
            trayMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, trayMenu));
            Check(Labels(capsule).SequenceEqual(expected) && Labels(trayMenu).SequenceEqual(expected),
                "Reopening the menus while hidden offers the show action");
            Check(!ReferenceEquals(capsule.Items[0], trayMenu.Items[0]) && !ReferenceEquals(capsuleInput, trayMenu.Items[1]),
                "HUD and tray own distinct MenuItem instances");
            Check(ReferenceEquals(capsule.Style, trayMenu.Style) && capsule.Style is not null,
                "Both owners use the same shared quick-menu style");
            int resources = capsule.Resources.MergedDictionaries.Count;
            QuickMenuTheme.Apply(capsule); QuickMenuTheme.Apply(capsule);
            Check(capsule.Resources.MergedDictionaries.Count == resources, "Applying the shared theme is idempotent");

            window.InitializeInputRecovery(hotkeyAvailable: true, trayAvailable: true);
            Check(capsuleInput.IsEnabled && Equals(capsuleInput.InputGestureText, "Ctrl+Alt+I") &&
                  Equals(((MenuItem)trayMenu.Items[1]).InputGestureText, "Ctrl+Alt+I"), "Input recovery enables and labels the action in both owners");
            Check(window.SetClickThrough(true) && capsuleInput.IsChecked && ((MenuItem)trayMenu.Items[1]).IsChecked &&
                  AutomationProperties.GetHelpText(capsuleInput).Contains("HUD виден", StringComparison.Ordinal),
                "Enabling the isolated HUD input mode updates both menu checkmarks and the spoken recovery help");
            Check(window.SetClickThrough(false) && !capsuleInput.IsChecked && !((MenuItem)trayMenu.Items[1]).IsChecked,
                "Restoring the isolated HUD input mode clears both menu checkmarks");
            window.SetTrayHotkeyStatus(false); tray.SetHotkeyStatus(false);
            Check(string.IsNullOrEmpty(((MenuItem)capsule.Items[0]).InputGestureText) && string.IsNullOrEmpty(((MenuItem)trayMenu.Items[0]).InputGestureText),
                "Unavailable visibility shortcuts are removed from both menus");
            window.SetTrayHotkeyStatus(true); tray.SetHotkeyStatus(true);
            Check(Equals(((MenuItem)capsule.Items[0]).InputGestureText, "Ctrl+Alt+Space") &&
                  Equals(((MenuItem)trayMenu.Items[0]).InputGestureText, "Ctrl+Alt+Space"), "Restored visibility shortcuts are advertised again");

            Materialize(capsule); Materialize(trayMenu);
            Check(!capsule.IsOpen && !trayMenu.IsOpen && PresentationSource.FromVisual(capsule) is null,
                "Theme measurement never opens a real global context menu");
            Check(capsule.DesiredSize.Width > 200 && capsule.DesiredSize.Width < 600 && capsule.DesiredSize.Height > 150,
                "The compact themed menu measures to a bounded readable size");
            Check(BrushColor(capsule.Background) == "#FF141A21" && BrushColor(capsule.Foreground) == "#FFEDF3F7" &&
                  BrushColor(capsule.Background) == BrushColor(trayMenu.Background), "Both menu surfaces share the dark palette and readable foreground");
            Check(FindVisuals<Border>((Separator)capsule.Items[4]).Any(border => BrushColor(border.Background) == "#FF2D3944"),
                "The actual menu separator uses the muted shared palette rather than the default Windows line");
            var row = (Border)capsuleInput.Template.FindName("Row", capsuleInput)!;
            var marker = (ShapePath)capsuleInput.Template.FindName("Check", capsuleInput)!;
            var statusBadge = (Border)capsuleInput.Template.FindName("ActiveBadge", capsuleInput)!;
            Check(row.CornerRadius.TopLeft == 8 && row.ActualHeight >= 38 && marker.Visibility == Visibility.Collapsed && statusBadge.Visibility == Visibility.Collapsed,
                "The unselected action has rounded readable geometry and no false checkmark");
            capsuleInput.IsChecked = true; capsuleInput.ApplyTemplate();
            Check(marker.Visibility == Visibility.Visible && BrushColor(marker.Stroke) == "#FF9DE6D1" && statusBadge.Visibility == Visibility.Visible,
                "Selected actions expose a mint checkmark and a visible enabled badge");
            capsuleInput.IsChecked = false; capsuleInput.IsEnabled = false;
            Check(Math.Abs(row.Opacity - .50) < .001, "Disabled action styling is visible without changing its text");
            capsuleInput.IsEnabled = true;
            Check(Math.Abs(row.Opacity - 1) < .001, "Re-enabled actions regain normal contrast");
            var highlightKey = (DependencyPropertyKey?)typeof(MenuItem).GetField("IsHighlightedPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                ?? throw new InvalidOperationException("MenuItem highlight state key is unavailable.");
            capsuleInput.SetValue(highlightKey, true);
            Check(BrushColor(row.Background) == "#FF253540", "Highlighted keyboard/mouse selection has a clear themed row background");
            capsuleInput.SetValue(highlightKey, false);
            Check(BrushColor(row.Background) == "#00FFFFFF" || row.Background == Brushes.Transparent,
                "Clearing selection restores the transparent row background");
            Check(capsuleInput.Focusable && KeyboardNavigation.GetDirectionalNavigation(FindVisual<ItemsPresenter>(capsule)!) == KeyboardNavigationMode.Cycle,
                "Quick actions retain focusability and cyclic directional navigation");
            var shortcut = (Border)capsuleInput.Template.FindName("ShortcutBadge", capsuleInput)!;
            var exit = capsule.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Выход"));
            Check(FindVisuals<TextBlock>(shortcut).Any(text => text.Text == "Ctrl+Alt+I" && text.ActualWidth > 0) &&
                  ((Border)exit.Template.FindName("ShortcutBadge", exit)!).Visibility == Visibility.Collapsed,
                "The template renders the input shortcut and hides the badge when the action has no shortcut");
            Save(capsule, "quick-menu.png");

            var submenuFixture = new ContextMenu(); QuickMenuTheme.Apply(submenuFixture);
            var branch = new MenuItem { Header = "Дополнительно" }; branch.Items.Add(new MenuItem { Header = "Пример действия" }); submenuFixture.Items.Add(branch);
            Materialize(submenuFixture);
            var arrow = (ShapePath)branch.Template.FindName("Arrow", branch)!;
            var popup = (Popup)branch.Template.FindName("PART_Popup", branch)!;
            Check(arrow.Visibility == Visibility.Visible && popup.Placement == PlacementMode.Right,
                "The shared theme still supports submenu indicators for compatible callers");
            Check(!popup.IsOpen && !popup.Focusable && popup.AllowsTransparency && popup.PopupAnimation == PopupAnimation.None,
                "Submenu template stays closed and avoids animation or focus capture during measurement");
            Check(branch.GetBindingExpression(MenuItem.IsSubmenuOpenProperty) is null &&
                  popup.GetValue(Popup.IsOpenProperty) is false, "No submenu is opened by theme construction");
            Check(!window.IsVisible && GetForegroundWindow() != handle && !capsule.IsOpen && !trayMenu.IsOpen,
                "All checks finish with no visible fixture, activation or global menus");
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally { tray?.Dispose(); window.Close(); }
    }

    private static string[] Labels(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(item => item.Header.ToString()!).ToArray();
    private static string? BrushColor(Brush? brush) => (brush as SolidColorBrush)?.Color.ToString();
    private static void Materialize(ContextMenu menu)
    {
        menu.Visibility = Visibility.Visible; menu.ApplyTemplate();
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); menu.Arrange(new Rect(menu.DesiredSize)); menu.UpdateLayout();
        foreach (MenuItem item in menu.Items.OfType<MenuItem>()) item.ApplyTemplate();
    }
    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (T child in FindVisuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject => FindVisuals<T>(root).FirstOrDefault();
    private static void Save(ContextMenu menu, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(menu); bitmap.Freeze(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, name)); encoder.Save(stream);
    }
    private static void Check(bool passed, string description) { if (!passed) throw new InvalidOperationException(description); Checks.Add(description); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
