using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow : Window
{
    private readonly MediaService _media;
    private readonly SystemService _system;
    private readonly PlacementStore _placementStore;
    private readonly AutostartService _autostart;
    private PlacementController? _placement;
    public PlacementController Placement => _placement ?? throw new InvalidOperationException("Окно ещё не загружено.");
    private readonly CultureInfo _culture = CultureInfo.GetCultureInfo("ru-RU");
    private bool _settingVolume;
    private bool _closed;
    private bool _busy;
    private MediaSnapshot? _lastMedia;

    public MainWindow(MediaService media, SystemService system, PlacementStore? placementStore = null, AppearanceStore? appearanceStore = null,
        ComponentStore? componentStore = null, AutostartService? autostart = null, GameProfileController? profiles = null,
        MainWindow? islandOwner = null, string? capsuleId = null, string? capsuleItems = null, string? capsuleRows = null)
    {
        _media = media;
        _system = system;
        _placementStore = placementStore ?? new PlacementStore();
        _autostart = autostart ?? new AutostartService();
        _profiles = profiles;
        _islandOwner = islandOwner;
        _capsuleId = capsuleId;
        _capsuleItems = capsuleItems;
        _capsuleRows = capsuleRows;
        _cpuHistory = islandOwner?._cpuHistory ?? new MetricHistory();
        _gpuHistory = islandOwner?._gpuHistory ?? new MetricHistory();
        _memoryHistory = islandOwner?._memoryHistory ?? new MetricHistory();
        _fpsHistory = islandOwner?._fpsHistory ?? new MetricHistory();
        _frameTimeHistory = islandOwner?._frameTimeHistory ?? new FrameTimeHistory();
        InitializeComponent();
        InitializeAppearance(appearanceStore ?? new AppearanceStore());
        InitializeComponents(componentStore ?? new ComponentStore());
        _media.Changed += OnMediaChanged;
        _system.VolumeChanged += OnSystemVolumeChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
        UpdateClockAndBattery();
        ApplyVolume(_system.Volume, _system.IsMuted);
        InitializeIslandLifecycle();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_placement is not null) return;
        _placement = new PlacementController(this, _placementStore, FitHudToDisplay);
        _placement.DragCompleted += OnProfileDragCompleted;
        if (_islandOwner is not null) InitializeMusicIslandMenu();
        else
        {
            InitializeTrayVisibility();
            IslandSurface.ContextMenu.Items.Insert(1, CreateInputMenuItem());
            IslandSurface.ContextMenu.Items.Insert(2, CreateCapsuleEditMenuItem());
            IslandSurface.ContextMenu.Items.Insert(3, CreateSettingsMenuItem());
            QuickMenuTheme.Apply(IslandSurface.ContextMenu);
            SynchronizeMusicIsland();
        }
    }

    public MenuItem CreatePositionMenu()
    {
        var menu = new MenuItem { Header = "Положение" };
        void Populate()
        {
            menu.Items.Clear();
            var presets = new (IslandPlacement Placement, string Label)[]
            {
                (IslandPlacement.TopLeft, "Сверху слева"), (IslandPlacement.TopCenter, "Сверху по центру"),
                (IslandPlacement.TopRight, "Сверху справа"), (IslandPlacement.MiddleLeft, "Слева по центру"),
                (IslandPlacement.Center, "В центре экрана"), (IslandPlacement.MiddleRight, "Справа по центру"),
                (IslandPlacement.BottomLeft, "Снизу слева"), (IslandPlacement.BottomCenter, "Снизу по центру"),
                (IslandPlacement.BottomRight, "Снизу справа")
            };
            foreach (var preset in presets)
            {
                if (preset.Placement is IslandPlacement.MiddleLeft or IslandPlacement.BottomLeft) menu.Items.Add(new Separator());
                var item = new MenuItem { Header = preset.Label, IsCheckable = true,
                    IsChecked = Placement.Settings.Placement == preset.Placement };
                item.Click += (_, _) => { Placement.SetPreset(preset.Placement); };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            if (Placement.Settings.Placement == IslandPlacement.Custom)
                menu.Items.Add(new MenuItem { Header = "Произвольное положение", IsCheckable = true, IsChecked = true, IsEnabled = false });
            if (Placement.Displays.Count > 1)
            {
                var displays = new MenuItem { Header = "Экран" };
                foreach (var display in Placement.Displays)
                {
                    var item = new MenuItem { Header = display.Label, IsCheckable = true,
                        IsChecked = display.Id == Placement.CurrentDisplay.Id };
                    item.Click += (_, _) => { Placement.SetMonitor(display.Id); };
                    displays.Items.Add(item);
                }
                menu.Items.Add(displays);
            }
            var locked = new MenuItem { Header = "Закрепить положение", IsCheckable = true, IsChecked = Placement.Settings.Locked };
            locked.Click += (_, _) => { Placement.SetLocked(locked.IsChecked); };
            menu.Items.Add(locked);
        }
        Populate();
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) Populate(); };
        return menu;
    }

    public void UpdateClockAndBattery()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(UpdateClockAndBattery));
            return;
        }

        if (_closed) return;
        DateTime now = DateTime.Now;
        UpdateClockText(now);
        if (_islandOwner is not null) return;
        foreach (var child in _capsuleWindows.Values)
            if (child.IsVisible) child.UpdateClockText(now);
        ApplyBattery(Components.Battery ? SystemService.ReadBattery() : new BatterySnapshot(false, -1, false));
    }

    private void UpdateClockText(DateTime now)
    {
        CompactClock.Text = now.ToString("HH:mm", _culture);
        DateLabel.Text = now.ToString("ddd, d MMMM", _culture);
    }

    private void OnMediaChanged(MediaSnapshot state)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => OnMediaChanged(state)));
            return;
        }

        _lastMedia = state;
        if (!MusicInThisIsland)
        {
            CompactTitle.Text = string.Empty;
            CompactArtist.Visibility = Visibility.Collapsed;
            CompactArtwork.Source = null;
            CompactMusicRow.ToolTip = null;
            return;
        }
        CompactArtist.Visibility = Visibility.Visible;
        CompactTitle.Text = state.HasSession && !string.IsNullOrWhiteSpace(state.Title)
            ? state.Title : "Откройте музыку";
        CompactArtist.Text = state.HasSession && !string.IsNullOrWhiteSpace(state.Artist) ? state.Artist : "Системный плеер";
        CompactArtwork.Source = state.Artwork;
        CompactArtwork.Visibility = state.Artwork is null ? Visibility.Collapsed : Visibility.Visible;
        CompactFallback.Visibility = state.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
        CompactPlayIcon.Data = Geometry.Parse(state.IsPlaying ? "M 2,1 L 6,1 L 6,15 L 2,15 Z M 10,1 L 14,1 L 14,15 L 10,15 Z" : "M 2,1 L 2,15 L 13,8 Z");
        string playLabel = state.IsPlaying ? "Пауза" : "Воспроизвести";
        AutomationProperties.SetName(CompactPlayButton, playLabel);
        CompactPlayButton.ToolTip = playLabel;
        UpdateMediaAvailability();
        string fullTitle = state.HasSession ? $"{CompactTitle.Text} — {CompactArtist.Text}" : CompactTitle.Text;
        AutomationProperties.SetName(CompactMusicRow, fullTitle);
        CompactMusicRow.ToolTip = fullTitle;
    }

    private void UpdateMediaAvailability()
    {
        CompactPlayButton.IsEnabled = !_busy && _lastMedia?.HasSession == true && _lastMedia.CanToggle;
        CompactPreviousButton.IsEnabled = !_busy && _lastMedia?.HasSession == true && _lastMedia.CanPrevious;
        CompactNextButton.IsEnabled = !_busy && _lastMedia?.HasSession == true && _lastMedia.CanNext;
    }

    private void OnSystemVolumeChanged(float volume, bool muted)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => OnSystemVolumeChanged(volume, muted)));
            return;
        }
        ApplyVolume(volume, muted);
    }

    private void ApplyVolume(float volume, bool muted)
    {
        _settingVolume = true;
        try
        {
            VolumeControl.IsEnabled = MuteButton.IsEnabled = _system.IsAvailable;
            VolumeControl.Value = Math.Clamp(volume * 100, 0, 100);
            VolumeLabel.Text = _system.IsAvailable ? $"{Math.Round(VolumeControl.Value):0}%" : "—";
            VolumeIcon.Text = muted ? "\uE74F" : "\uE767";
            AutomationProperties.SetName(MuteButton, muted ? "Включить звук" : "Выключить звук");
        }
        finally { _settingVolume = false; }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingVolume || !IsInitialized || VolumeLabel is null) return;
        VolumeLabel.Text = $"{Math.Round(e.NewValue):0}%";
        try { _system.SetVolume((float)e.NewValue / 100); }
        catch (Exception ex) { App.Log(ex); VolumeLabel.Text = "—"; }
    }

    private void OnMute(object sender, RoutedEventArgs e)
    {
        try { _system.ToggleMute(); }
        catch (Exception ex) { App.Log(ex); VolumeLabel.Text = "—"; }
    }

    private void OnSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Components.VolumeWheel || IsClickThrough || _closed || !IsVisible || !IsEnabled ||
            e.Delta == 0 || IslandSurface.InputHitTest(e.GetPosition(IslandSurface)) is null) return;

        // A relative adjustment needs the live level even if audio notifications are still queued.
        _system.RefreshVolume();
        if (!_system.IsAvailable) return;
        _system.SetVolume(VolumeAfterWheel(_system.Volume, e.Delta));
        e.Handled = true;
    }

    private static float VolumeAfterWheel(float volume, int delta)
        => Math.Clamp(volume + .02f * delta / Mouse.MouseWheelDeltaForOneLine, 0f, 1f);

    private async Task RunMediaActionAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateMediaAvailability();
        try { await action(); }
        catch (Exception ex) { App.Log(ex); if (!_closed) CompactMusicRow.ToolTip = "Не удалось связаться с плеером"; }
        finally
        {
            _busy = false;
            if (!_closed) UpdateMediaAvailability();
        }
    }

    private async void OnTogglePlayback(object sender, RoutedEventArgs e) => await RunMediaActionAsync(_media.ToggleAsync);
    private async void OnPrevious(object sender, RoutedEventArgs e) => await RunMediaActionAsync(_media.PreviousAsync);
    private async void OnNext(object sender, RoutedEventArgs e) => await RunMediaActionAsync(_media.NextAsync);

    private bool CanStartSurfaceDrag(DependencyObject? source)
    {
        if (_closed || _placement is null || _placement.IsDragging || !IslandSurface.IsEnabled ||
            IsPositionLocked || IsClickThrough) return false;

        for (DependencyObject? node = source; node is not null;
             node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or RangeBase) return false;
            if (ReferenceEquals(node, IslandSurface)) return true;
        }
        return false;
    }

    private void OnSurfacePointerDown(object sender, MouseButtonEventArgs e)
    {
        if (TryHandleCapsulePointerDown(e)) return;
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed ||
            !CanStartSurfaceDrag(e.OriginalSource as DependencyObject)) return;

        e.Handled = true;
        Placement.BeginDrag();
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { Placement.EndDrag(); }
    }

    public RenderTargetBitmap CapturePreview()
    {
        VerifyAccess();
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Width), (int)Math.Ceiling(Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnExit(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!IsCapsuleLayoutEditing) SaveCurrentProfileEdits();
        if (_islandOwner is null)
        {
            CloseLayoutEditorPanels();
            CloseCapsuleRemovalMenu();
        }
        CancelCapsuleGesture();
        _closed = true;
        DisposeIslandLifecycle();
        ResetAlerts();
        if (_placement is not null) _placement.DragCompleted -= OnProfileDragCompleted;
        _placement?.Dispose();
        _media.Changed -= OnMediaChanged;
        _system.VolumeChanged -= OnSystemVolumeChanged;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
