using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island.Services;

namespace Luma.Island;

public partial class App : System.Windows.Application
{
    private MediaService? _media;
    private SystemService? _system;
    private PerformanceService? _performance;
    private FpsService? _fps;
    private MainWindow? _island;
    private TrayService? _tray;
    private DispatcherTimer? _clock;
    private Mutex? _instance;
    private HwndSource? _windowSource;
    private bool _ownsMutex;
    private bool _inputHotkeyAvailable;
    private const int HotkeyId = 0x4C55;
    private const int InputHotkeyId = 0x4C56;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            // Unexpected UI failures must not leave an invisible background process.
            Shutdown(1);
            args.Handled = true;
        };
        bool verifying = e.Args.Length == 2 && e.Args[0] == "--verify";
        bool measuring = e.Args.Length == 2 && e.Args[0] == "--measure-appearance";
        bool measuringResources = e.Args.Length == 2 && e.Args[0] == "--measure-resources";
        bool resourceScenarios = e.Args.Length == 2 && e.Args[0] == "--resource-scenarios";
        bool diagnostic = verifying || measuring || measuringResources || resourceScenarios;
        if (!diagnostic)
        {
            _instance = new Mutex(true, "Local\\LumaIsland-0.1", out _ownsMutex);
            if (!_ownsMutex) { Luma.Island.MainWindow.RequestRestoreExistingInstance(); Shutdown(); return; }
        }
        try
        {
            if (diagnostic) Directory.CreateDirectory(Path.GetFullPath(e.Args[1]));
            var autostart = new AutostartService(readOnly: diagnostic);
            if (!diagnostic && Array.IndexOf(e.Args, "--startup") < 0)
            {
                try { autostart.RefreshExecutablePath(); }
                catch (Exception ex) { Log(ex); }
            }
            _media = new MediaService();
            _system = new SystemService();
            _island = new MainWindow(_media, _system, diagnostic
                ? new PlacementStore(Path.Combine(Path.GetFullPath(e.Args[1]), "test-placement.json")) : null,
                diagnostic ? new AppearanceStore(Path.Combine(Path.GetFullPath(e.Args[1]), "test-appearance.json")) : null,
                diagnostic ? new ComponentStore(Path.Combine(Path.GetFullPath(e.Args[1]), "test-components.json")) : null, autostart);
            _performance = new PerformanceService();
            _fps = new FpsService();
            _performance.Changed += _island.ApplyPerformance;
            _fps.Changed += _island.ApplyFps;
            _island.ComponentsChanged += ConfigureComponents;
            _island.IsVisibleChanged += (_, _) =>
            {
                if (_island.IsVisible) _island.UpdateClockAndBattery();
                ConfigureComponents(_island.Components);
            };
            if (Array.IndexOf(e.Args, "--ui-check") >= 0)
            {
                _island.ShowInTaskbar = true;
                _island.ShowActivated = true;
            }
            MainWindow = _island;
            _island.Closed += (_, _) => Shutdown();
            _island.Show();
            ConfigureComponents(_island.Components);
            if (resourceScenarios)
            {
                CreateTray();
                ScheduleClock();
                await _media.InitializeAsync();
                await RunResourceScenariosAsync(Path.GetFullPath(e.Args[1]));
                Shutdown();
                return;
            }
            if (measuringResources)
            {
                CreateTray();
                ScheduleClock();
                await _media.InitializeAsync();
                await MeasureResourcesAsync(Path.GetFullPath(e.Args[1]));
                Shutdown();
                return;
            }
            if (measuring)
            {
                CreateTray();
                ScheduleClock();
                await _media.InitializeAsync();
                await MeasureAppearanceAsync(Path.GetFullPath(e.Args[1]));
                Shutdown();
                return;
            }
            if (verifying)
            {
                _island.Topmost = false;
                CreateTray();
                await VerifyAsync(Path.GetFullPath(e.Args[1]));
                Shutdown();
                return;
            }
            CreateTray();
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(_island).Handle);
            _windowSource?.AddHook(WindowMessage);
            bool hotkeyAvailable = RegisterHotKey(_windowSource!.Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, 0x20);
            _island.SetTrayHotkeyStatus(hotkeyAvailable);
            _tray?.SetHotkeyStatus(hotkeyAvailable);
            _inputHotkeyAvailable = RegisterHotKey(_windowSource.Handle, InputHotkeyId, 0x0001 | 0x0002 | 0x4000, 0x49);
            _island.InitializeInputRecovery(_inputHotkeyAvailable, _tray?.IsRegistered == true);
            ScheduleClock();
            try { await _media.InitializeAsync(); }
            catch (Exception ex) { Log(ex); }
        }
        catch (Exception ex)
        {
            Log(ex);
            if (diagnostic) File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString());
            else System.Windows.MessageBox.Show("Не удалось запустить Luma Island. Подробности: " + LogPath,
                "Luma Island", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ScheduleClock()
    {
        _clock?.Stop();
        if (_island is null || !(_island.Components.Time || _island.Components.Date || _island.Components.Battery)) return;
        if (_clock is null)
        {
            _clock = new DispatcherTimer(DispatcherPriority.Background);
            _clock.Tick += (_, _) =>
            {
                _island?.UpdateClockAndBattery();
                _clock!.Interval = TimeSpan.FromMinutes(1);
            };
        }
        _clock.Interval = TimeSpan.FromSeconds(60 - DateTime.Now.Second);
        _clock.Start();
    }

    private async void ConfigureComponents(ComponentSettings settings)
    {
        bool visible = _island?.IsVisible == true;
        _performance?.Configure(visible && settings.Cpu, visible && settings.Gpu, visible && settings.Memory, visible && settings.GpuTemperature,
            visible && settings.CpuTemperature, visible && settings.MemoryTemperature);
        _fps?.Configure(visible && settings.Fps);
        ScheduleClock();
        try { if (_media is not null) await _media.SetEnabledAsync(settings.Music); }
        catch (Exception ex) { Log(ex); }
    }

    private void CreateTray()
    {
        _tray = new TrayService(_island!, ToggleVisible, () =>
        {
            _island!.RestoreForInteraction(); _island.ToggleExpanded();
        }, () => Shutdown(), _island!.CreatePositionMenu, _island!.CreateAppearanceMenu, _island!.CreateComponentsMenu,
            _island!.CreateAutostartMenu, _island!.RefreshAutostartMenus, _island!.RestoreForInteraction,
            _island!.CreateInputMenuItem);
        _tray.RegistrationChanged += OnTrayRegistrationChanged;
        _island.InputModeChanged += OnInputModeChanged;
    }

    private void OnTrayRegistrationChanged(bool registered) => _island?.UpdateInputRecovery(_inputHotkeyAvailable, registered);
    private void OnInputModeChanged(bool clickThrough) { if (clickThrough) _tray?.CloseMenu(); }

    private void ToggleVisible()
    {
        _island?.ToggleTrayVisibility();
    }

    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0218 && wParam.ToInt32() == 0x000A) _island?.UpdateClockAndBattery();
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            ToggleVisible();
            handled = true;
        }
        if (msg == 0x0312 && wParam.ToInt32() == InputHotkeyId)
        {
            _island?.ToggleClickThrough();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async Task VerifyAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var errors = new System.Collections.Generic.List<string>();
        if (_tray?.IsRegistered != true) errors.Add("Не удалось зарегистрировать значок в трее Windows.");
        var restoredBounds = DisplayService.GetWindowBounds(_island!);
        var expectedRestored = _island!.Placement.DesiredBounds(_island.CollapsedWindowHeight);
        bool restoredPosition = Math.Abs(restoredBounds.X - expectedRestored.X) <= 1 && Math.Abs(restoredBounds.Y - expectedRestored.Y) <= 1;
        if (!restoredPosition) errors.Add("Сохранённое положение не восстановилось при запуске.");
        int mediaEvents = 0;
        bool hasSession = false;
        _media!.Changed += snapshot => { Interlocked.Increment(ref mediaEvents); hasSession = snapshot.HasSession; };
        try { await _media.InitializeAsync(); } catch (Exception ex) { errors.Add(ex.ToString()); }
        _system!.RefreshVolume();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        SavePreview(_island!.CapturePreview(), Path.Combine(directory, "collapsed.png"));
        _island.ToggleExpanded();
        await _island.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));
        SavePreview(_island.CapturePreview(), Path.Combine(directory, "expanded.png"));
        _island.ToggleExpanded();
        await _island.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));
        _island.UpdateClockAndBattery();
        string initialAppearance = _island.Appearance.Mode.ToString();
        var appearanceChecks = await AppearanceVerification.RunAsync(_island, directory, errors);
        var componentChecks = await ComponentsVerification.RunAsync(_island, _performance!, _fps!, _media!, directory, errors);
        var storedAppearance = new AppearanceStore(Path.Combine(directory, "test-appearance.json")).Load();
        if (storedAppearance != _island.Appearance) errors.Add("Оформление не сохранилось.");
        string initialPlacement = _island.Placement.Settings.Placement.ToString();
        var menu = _island.CreatePositionMenu();
        if (!menu.HasItems || menu.Items.Count < 10) errors.Add("Меню положения пустое.");
        _island.Placement.SetPreset(IslandPlacement.BottomRight);
        var expectedBottom = DisplayService.GetWindowBounds(_island);
        var savedPlacement = _island.Placement.Settings;
        for (int cycle = 0; cycle < 12; cycle++)
        {
            _island.ToggleExpanded();
            await _island.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));
            _island.ToggleExpanded();
            await _island.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var after = DisplayService.GetWindowBounds(_island);
            if (Math.Abs(after.X - expectedBottom.X) > 1 || Math.Abs(after.Y - expectedBottom.Y) > 1)
                errors.Add($"Положение сместилось после раскрытия {cycle}: expected={expectedBottom}, actual={after}, height={_island.Height}");
        }
        if (_island.Placement.Settings != savedPlacement) errors.Add("Анимация изменила сохранённую позицию.");
        _island.ToggleExpanded();
        await Task.Delay(30);
        _island.ToggleExpanded();
        await Task.Delay(30);
        _island.ToggleExpanded();
        await _island.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));
        _island.ToggleExpanded();
        await _island.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var rapidBounds = DisplayService.GetWindowBounds(_island);
        if (Math.Abs(rapidBounds.X - expectedBottom.X) > 1 || Math.Abs(rapidBounds.Y - expectedBottom.Y) > 1)
            errors.Add("Быстрое переключение изменило положение.");
        _island.Placement.BeginDrag();
        _island.Placement.EndDrag();
        if (_island.Placement.Settings != savedPlacement) errors.Add("Щелчок без перемещения заменил готовую позицию.");
        _island.Placement.SetLocked(false);
        var workArea = _island.Placement.CurrentDisplay.WorkArea;
        var customBounds = PlacementMath.Bounds(workArea, DisplayService.GetWindowBounds(_island).Size, new Point(.31, .64),
            8 * DisplayService.GetWindowScale(_island));
        _island.Placement.BeginDrag();
        DisplayService.MoveWindow(_island, customBounds.X, customBounds.Y);
        _island.Placement.EndDrag();
        if (_island.Placement.Settings.Placement != IslandPlacement.Custom ||
            Math.Abs(_island.Placement.Settings.XFraction - .31) > .003 || Math.Abs(_island.Placement.Settings.YFraction - .64) > .003)
            errors.Add("Произвольное положение не сохранилось после перемещения.");
        _island.Placement.SetPreset(IslandPlacement.BottomRight);
        _island.Placement.SetLocked(true);
        _island.Placement.CaptureDrag();
        if (_island.Placement.Settings.Placement != IslandPlacement.BottomRight) errors.Add("Блокировка перетаскивания не сработала.");
        var persistedPlacement = new PlacementStore(Path.Combine(directory, "test-placement.json")).Load();
        if (persistedPlacement != _island.Placement.Settings) errors.Add("Настройки не сохранились.");
        var battery = SystemService.ReadBattery();
        File.WriteAllText(Path.Combine(directory, "verification.json"), JsonSerializer.Serialize(new
        {
            succeeded = errors.Count == 0,
            mediaEvents, hasSession,
            volume = _system.Volume, muted = _system.IsMuted,
            trayRegistered = _tray?.IsRegistered,
            initialAppearance, appearanceChecks,
            componentChecks,
            positionChecks = new { restoredPosition, initialPlacement, current = persistedPlacement, cycles = 12,
                rapidToggle = true, customDrag = true, menuItems = menu.Items.Count },
            battery,
            renderWidth = _island.ActualWidth, renderHeight = _island.ActualHeight,
            processCount = Process.GetProcessesByName("Luma.Island").Length,
            errors
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (errors.Count > 0) throw new InvalidOperationException("Startup verification failed: " + errors[0]);
    }

    private static void SavePreview(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland", "errors.log");
    internal static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, DateTimeOffset.Now + " " + ex + Environment.NewLine);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clock?.Stop();
        if (_tray is not null) _tray.RegistrationChanged -= OnTrayRegistrationChanged;
        if (_island is not null) _island.InputModeChanged -= OnInputModeChanged;
        if (_windowSource != null)
        {
            UnregisterHotKey(_windowSource.Handle, HotkeyId);
            UnregisterHotKey(_windowSource.Handle, InputHotkeyId);
            if (!_windowSource.IsDisposed) _windowSource.RemoveHook(WindowMessage);
        }
        _tray?.Dispose();
        _fps?.Dispose();
        try
        {
            if (_fps is not null && !_fps.Completion.Wait(TimeSpan.FromSeconds(5)))
                Log(new TimeoutException("Источник FPS не завершил очистку за 5 секунд."));
        }
        catch (Exception ex) { Log(ex); }
        _performance?.Dispose();
        _media?.Dispose();
        _system?.Dispose();
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
