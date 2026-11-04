using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow : Window
{
    private readonly MediaService _media;
    private readonly SystemService _system;
    private readonly PlacementStore _placementStore;
    private readonly AutostartService _autostart;
    private PlacementController? _placement;
    private readonly TranslateTransform _surfaceOffset = new();
    public PlacementController Placement => _placement ?? throw new InvalidOperationException("Окно ещё не загружено.");
    private readonly CultureInfo _culture = CultureInfo.GetCultureInfo("ru-RU");
    private bool _expanded;
    private bool _settingVolume;
    private bool _closed;
    private bool _busy;
    private bool _animating;
    private TaskCompletionSource? _transitionCompletion;
    private Point _pointerStart;
    private bool _dragged;
    private int _transition;
    private MediaSnapshot? _lastMedia;

    public MainWindow(MediaService media, SystemService system, PlacementStore? placementStore = null, AppearanceStore? appearanceStore = null,
        ComponentStore? componentStore = null, AutostartService? autostart = null, GameProfileController? profiles = null)
    {
        _media = media;
        _system = system;
        _placementStore = placementStore ?? new PlacementStore();
        _autostart = autostart ?? new AutostartService();
        _profiles = profiles;
        InitializeComponent();
        InitializeAppearance(appearanceStore ?? new AppearanceStore());
        InitializeComponents(componentStore ?? new ComponentStore());
        IslandSurface.RenderTransform = _surfaceOffset;
        _media.Changed += OnMediaChanged;
        _system.VolumeChanged += OnSystemVolumeChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
        UpdateClockAndBattery();
        ApplyVolume(_system.Volume, _system.IsMuted);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_placement is not null) return;
        _placement = new PlacementController(this, _placementStore, FitHudToDisplay);
        _placement.DragCompleted += OnProfileDragCompleted;
        InitializeTrayVisibility();
        IslandSurface.ContextMenu.Items.Insert(2, CreateInputMenuItem());
        IslandSurface.ContextMenu.Items.Insert(3, CreateSettingsMenuItem());
        QuickMenuTheme.Apply(IslandSurface.ContextMenu);
    }

    public void ToggleExpanded()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ToggleExpanded));
            return;
        }

        if (_closed) return;
        _transitionCompletion?.TrySetResult();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transitionCompletion = completion;
        UpdateLayout();
        Rect oldBounds = DisplayService.GetWindowBounds(this);
        double scale = DisplayService.GetWindowScale(this);
        Point oldInset = IslandSurface.TransformToAncestor(this).Transform(new Point());
        double oldSurfaceTop = oldBounds.Top + oldInset.Y * scale;
        double oldSurfaceLeft = oldBounds.Left + oldInset.X * scale;
        double oldHeight = IslandSurface.Height;
        double oldWidth = IslandSurface.Width;
        IslandSurface.BeginAnimation(HeightProperty, null);
        IslandSurface.BeginAnimation(WidthProperty, null);
        _surfaceOffset.BeginAnimation(TranslateTransform.YProperty, null);
        _surfaceOffset.BeginAnimation(TranslateTransform.XProperty, null);
        IslandSurface.Height = oldHeight;
        IslandSurface.Width = oldWidth;
        _expanded = !_expanded;
        int transition = ++_transition;
        bool animate = SystemParameters.ClientAreaAnimation && IsVisible;
        _animating = animate;
        double targetHeight = (_expanded ? BaseExpandedWindowHeight : BaseCollapsedWindowHeight) - 24;
        double targetWidth = _expanded ? 384 : CollapsedSurfaceWidth;
        double targetWindowWidth = _expanded ? ExpandedWindowWidth : CollapsedWindowWidth;
        double targetWindowHeight = _expanded ? ExpandedWindowHeight : CollapsedWindowHeight;

        // Preserve the anchor during both transitions. The surface moves inside
        // a temporary larger host, so bottom/custom positions do not drift.
        Height = Math.Max(ExpandedWindowHeight, CollapsedWindowHeight);
        Width = Math.Max(ExpandedWindowWidth, CollapsedWindowWidth);
        Placement.Apply(Height);
        UpdateLayout();
        Rect hostBounds = DisplayService.GetWindowBounds(this);
        scale = DisplayService.GetWindowScale(this);
        _surfaceOffset.Y = (oldSurfaceTop - hostBounds.Top) / scale / EffectiveHudScale - 12;
        _surfaceOffset.X = (oldSurfaceLeft - hostBounds.Left) / scale / EffectiveHudScale - (Width / EffectiveHudScale - oldWidth) / 2;
        Rect targetBounds = Placement.DesiredBounds(targetWindowHeight, targetWindowWidth);
        double targetOffset = (targetBounds.Top - hostBounds.Top) / scale / EffectiveHudScale;
        double targetOffsetX = (targetBounds.Left - hostBounds.Left) / scale / EffectiveHudScale + (targetWindowWidth - Width) / EffectiveHudScale / 2;
        UpdateSurfaceRounding();
        if (_expanded)
        {
            CompactView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;
            _system.RefreshVolume();
        }
        else
        {
            ExpandedView.Visibility = Visibility.Collapsed;
            CompactView.Visibility = Visibility.Visible;
        }

        if (!animate)
        {
            IslandSurface.BeginAnimation(HeightProperty, null);
            IslandSurface.BeginAnimation(WidthProperty, null);
            ExpandedView.BeginAnimation(OpacityProperty, null);
            IslandSurface.Height = targetHeight;
            IslandSurface.Width = targetWidth;
            ExpandedView.Opacity = _expanded ? 1 : 0;
            Height = targetWindowHeight;
            Width = targetWindowWidth;
            _surfaceOffset.X = _surfaceOffset.Y = 0;
            Placement.Apply();
            completion.TrySetResult();
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var heightAnimation = new DoubleAnimation(targetHeight, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing };
        heightAnimation.Completed += (_, _) =>
        {
            if (_closed || transition != _transition) return;
            _animating = false;
            IslandSurface.BeginAnimation(HeightProperty, null);
            IslandSurface.BeginAnimation(WidthProperty, null);
            _surfaceOffset.BeginAnimation(TranslateTransform.YProperty, null);
            _surfaceOffset.BeginAnimation(TranslateTransform.XProperty, null);
            IslandSurface.Height = targetHeight;
            IslandSurface.Width = targetWidth;
            _surfaceOffset.X = _surfaceOffset.Y = 0;
            Height = targetWindowHeight;
            Width = targetWindowWidth;
            Placement.Apply();
            completion.TrySetResult();
        };
        IslandSurface.BeginAnimation(HeightProperty, heightAnimation, HandoffBehavior.SnapshotAndReplace);
        IslandSurface.BeginAnimation(WidthProperty,
            new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        ExpandedView.BeginAnimation(OpacityProperty,
            new DoubleAnimation(_expanded ? 1 : 0, TimeSpan.FromMilliseconds(150)),
            HandoffBehavior.SnapshotAndReplace);
        _surfaceOffset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(targetOffset, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        _surfaceOffset.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(targetOffsetX, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
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
                item.Click += (_, _) => { CompleteTransition(); Placement.SetPreset(preset.Placement); };
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
                    item.Click += (_, _) => { CompleteTransition(); Placement.SetMonitor(display.Id); };
                    displays.Items.Add(item);
                }
                menu.Items.Add(displays);
            }
            var locked = new MenuItem { Header = "Закрепить положение", IsCheckable = true, IsChecked = Placement.Settings.Locked };
            locked.Click += (_, _) => { CompleteTransition(); Placement.SetLocked(locked.IsChecked); };
            menu.Items.Add(locked);
        }
        Populate();
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) Populate(); };
        return menu;
    }

    private void CompleteTransition()
    {
        if (!_animating) return;
        ++_transition;
        _animating = false;
        IslandSurface.BeginAnimation(HeightProperty, null);
        IslandSurface.BeginAnimation(WidthProperty, null);
        ExpandedView.BeginAnimation(OpacityProperty, null);
        _surfaceOffset.BeginAnimation(TranslateTransform.YProperty, null);
        _surfaceOffset.BeginAnimation(TranslateTransform.XProperty, null);
        IslandSurface.Height = (_expanded ? BaseExpandedWindowHeight : BaseCollapsedWindowHeight) - 24;
        IslandSurface.Width = _expanded ? 384 : CollapsedSurfaceWidth;
        ExpandedView.Opacity = _expanded ? 1 : 0;
        _surfaceOffset.X = _surfaceOffset.Y = 0;
        Height = _expanded ? ExpandedWindowHeight : CollapsedWindowHeight;
        Width = _expanded ? ExpandedWindowWidth : CollapsedWindowWidth;
        UpdateSurfaceRounding();
        Placement.Apply();
        _transitionCompletion?.TrySetResult();
    }

    public Task WaitForTransitionAsync() => _transitionCompletion?.Task ?? Task.CompletedTask;

    public void UpdateClockAndBattery()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(UpdateClockAndBattery));
            return;
        }

        if (_closed) return;
        DateTime now = DateTime.Now;
        CompactClock.Text = ExpandedClock.Text = now.ToString("HH:mm", _culture);
        DateLabel.Text = now.ToString("ddd, d MMMM", _culture);
        ApplyBattery(Components.Battery ? SystemService.ReadBattery() : new BatterySnapshot(false, -1, false));
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
        if (!Components.Music)
        {
            CompactTitle.Text = string.Empty;
            CompactArtist.Visibility = Visibility.Collapsed;
            CompactArtwork.Source = ExpandedArtwork.Source = null;
            return;
        }
        CompactArtist.Visibility = Appearance.Compact ? Visibility.Collapsed : Visibility.Visible;
        CompactTitle.Text = ExpandedTitle.Text = state.HasSession && !string.IsNullOrWhiteSpace(state.Title)
            ? state.Title : "Откройте музыку";
        CompactArtist.Text = state.HasSession && !string.IsNullOrWhiteSpace(state.Artist) ? state.Artist : "Системный плеер";
        ExpandedArtist.Text = state.HasSession
            ? (string.IsNullOrWhiteSpace(state.Artist) ? "Системный плеер" : state.Artist)
            : "Плеер появится здесь";
        CompactArtwork.Source = ExpandedArtwork.Source = state.Artwork;
        CompactArtwork.Visibility = ExpandedArtwork.Visibility = state.Artwork is null ? Visibility.Collapsed : Visibility.Visible;
        CompactFallback.Visibility = ExpandedFallback.Visibility = state.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
        PlayIcon.Data = Geometry.Parse(state.IsPlaying ? "M 2,1 L 6,1 L 6,15 L 2,15 Z M 10,1 L 14,1 L 14,15 L 10,15 Z" : "M 2,1 L 2,15 L 13,8 Z");
        AutomationProperties.SetName(PlayButton, state.IsPlaying ? "Пауза" : "Воспроизвести");
        UpdateMediaAvailability();
        string fullTitle = state.HasSession ? $"{state.Title} — {state.Artist}" : "Раскрыть Luma Island";
        AutomationProperties.SetName(CompactView, fullTitle);
        CompactView.ToolTip = fullTitle;
    }

    private void UpdateMediaAvailability()
    {
        PlayButton.IsEnabled = !_busy && _lastMedia?.HasSession == true && _lastMedia.CanToggle;
        PreviousButton.IsEnabled = !_busy && _lastMedia?.HasSession == true && _lastMedia.CanPrevious;
        NextButton.IsEnabled = !_busy && _lastMedia?.HasSession == true && _lastMedia.CanNext;
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

    private async Task RunMediaActionAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateMediaAvailability();
        try { await action(); }
        catch (Exception ex) { App.Log(ex); ExpandedArtist.Text = "Не удалось связаться с плеером"; }
        finally
        {
            _busy = false;
            if (!_closed) UpdateMediaAvailability();
        }
    }

    private async void OnTogglePlayback(object sender, RoutedEventArgs e) => await RunMediaActionAsync(_media.ToggleAsync);
    private async void OnPrevious(object sender, RoutedEventArgs e) => await RunMediaActionAsync(_media.PreviousAsync);
    private async void OnNext(object sender, RoutedEventArgs e) => await RunMediaActionAsync(_media.NextAsync);

    private void OnToggleExpanded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, CompactView) && _dragged) { _dragged = false; return; }
        ToggleExpanded();
    }

    private void OnCompactPointerDown(object sender, MouseButtonEventArgs e)
    {
        _pointerStart = e.GetPosition(this);
        _dragged = false;
    }

    private void OnCompactPointerMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragged || _animating || Placement.Settings.Locked) return;
        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _pointerStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _pointerStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragged = true;
        Placement.BeginDrag();
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { Placement.EndDrag(); }
    }

    private void OnDragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _animating || Placement.Settings.Locked) return;
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
        SaveCurrentProfileEdits();
        _closed = true;
        ResetAlerts();
        _transitionCompletion?.TrySetCanceled();
        if (_placement is not null) _placement.DragCompleted -= OnProfileDragCompleted;
        _placement?.Dispose();
        _media.Changed -= OnMediaChanged;
        _system.VolumeChanged -= OnSystemVolumeChanged;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
