using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly MainWindow? _islandOwner;
    private MainWindow? _musicIsland;
    private PlacementSettings? _automaticMusicPlacement;
    private bool _synchronizingMusicIsland;

    public bool IsMusicIsland => _islandOwner is not null;
    public MainWindow? MusicIsland => _musicIsland;
    public bool IsHudPresentationVisible => _islandOwner?.IsHudPresentationVisible
        ?? (IsVisible || _musicIsland?.IsVisible == true);
    public event Action? PresentationVisibilityChanged;

    private bool DetachMusic => _islandOwner is null && LayoutSettings.DetachedMusic;
    private bool MusicInThisIsland => Components.Music && !DetachMusic;
    private bool VolumeInThisIsland => Components.Volume && !DetachMusic;
    private bool IsPositionLocked => (_islandOwner?._placement ?? _placement)?.Settings.Locked == true;

    private void InitializeIslandLifecycle()
    {
        IsVisibleChanged += OnIslandVisibilityChanged;
        if (_islandOwner is null) ProfileEditorVisibilityChanged += OnIslandEditorVisibilityChanged;
    }

    private void OnIslandVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_islandOwner is not null)
        {
            _islandOwner.NotifyPresentationVisibilityChanged();
            return;
        }
        SynchronizeMusicIsland();
        NotifyPresentationVisibilityChanged();
    }

    private void NotifyPresentationVisibilityChanged()
    {
        if (!_closed) PresentationVisibilityChanged?.Invoke();
    }

    private void OnIslandEditorVisibilityChanged(bool open)
    {
        // A modal settings draft must not race an independent music-window drag.
        if (_musicIsland is not null) _musicIsland.IsEnabled = !open;
    }

    private void SynchronizeMusicIsland()
    {
        // Settings previews are never shown and have no native placement controller.
        if (_islandOwner is not null || _closed || !IsLoaded || _placement is null || _synchronizingMusicIsland) return;
        _synchronizingMusicIsland = true;
        try
        {
            if (!DetachMusic || !(Components.Music || Components.Volume))
            {
                CloseMusicIsland();
                return;
            }
            if (!IsVisible)
            {
                _musicIsland?.Hide();
                return;
            }
            if (_musicIsland is null)
            {
                var music = new MainWindow(_media, _system, new MusicIslandPlacementStore(this),
                    new MusicIslandAppearanceStore(this), new MusicIslandComponentStore(this),
                    new AutostartService(readOnly: true), islandOwner: this)
                {
                    Owner = this,
                    Title = "Luma Island · Музыка",
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Topmost = Topmost,
                    Opacity = Opacity,
                    IsEnabled = !IsProfileEditorOpen
                };
                _musicIsland = music;
                music.Closed += OnMusicIslandClosed;
            }

            var child = _musicIsland;
            var appearance = MusicIslandAppearance();
            var components = MusicIslandComponents();
            if (child.Appearance != appearance || child.Components != components)
            {
                child.Appearance = appearance;
                child.Components = components;
                child.ApplyComponentVisibility();
                child.ApplyAppearance();
                child.OnMediaChanged(_lastMedia ?? _media.Current);
                child._savedAppearance = appearance;
            }
            // Null also represents an explicit reset or a newly selected profile.
            // Resolve it against this primary island, rather than an earlier profile.
            if (LayoutSettings.MusicPosition is null) _automaticMusicPlacement = CreateAutomaticMusicPlacement(child);
            if (child._placement is not null)
            {
                var position = MusicIslandPlacement();
                if (child._placement.Settings != position) child._placement.ApplySettings(position);
            }
            if (!child.IsVisible) child.Show();
            if (LayoutSettings.MusicPosition is null && child._placement is not null)
            {
                var position = child._placement.Settings;
                // Keep this resolved position in the normal draft until its existing
                // save boundary. Never write while a profile snapshot is being applied.
                Appearance = Appearance with { Layout = LayoutSettings with
                {
                    MusicPosition = new HudIslandPosition(position.MonitorId, position.XFraction, position.YFraction)
                } };
            }
            if (child.IsClickThrough != IsClickThrough)
            {
                if (IsClickThrough) child.PrepareForIslandClickThrough();
                if (child.TrySetClickThroughWindowStyle(IsClickThrough)) child.IsClickThrough = IsClickThrough;
                else SetClickThroughCore(false, persist: false); // Keep recovery consistent across both surfaces.
            }
        }
        finally { _synchronizingMusicIsland = false; }
    }

    private AppearanceSettings MusicIslandAppearance() => Appearance with
    {
        Layout = LayoutSettings with { DetachedMusic = false, MusicPosition = null },
        Alerts = AlertSettings.Default,
        ClickThrough = IsClickThrough
    };

    private ComponentSettings MusicIslandComponents() => new(
        Music: Components.Music, Volume: Components.Volume, Time: false, Date: false, Battery: false);

    private PlacementSettings MusicIslandPlacement()
    {
        var position = HudIslandPosition.Normalize(LayoutSettings.MusicPosition);
        var settings = position is null
            ? _automaticMusicPlacement ?? new PlacementSettings(_placement?.Settings.MonitorId,
                IslandPlacement.Custom, .5, .22)
            : new PlacementSettings(position.MonitorId, IslandPlacement.Custom, position.XFraction, position.YFraction);
        return settings with { Locked = _placement?.Settings.Locked == true };
    }

    private PlacementSettings CreateAutomaticMusicPlacement(MainWindow music)
    {
        var display = Placement.CurrentDisplay;
        var primary = DisplayService.GetWindowBounds(this);
        double scale = DisplayService.GetWindowScale(this);
        var size = new Size(music.Width * scale, music.Height * scale);
        double gap = 8 * scale;
        double y = primary.Bottom + gap;
        if (y + size.Height + gap > display.WorkArea.Bottom) y = primary.Top - size.Height - gap;
        var bounds = new Rect(primary.Left + (primary.Width - size.Width) / 2, y, size.Width, size.Height);
        var fractions = PlacementMath.Capture(display.WorkArea, bounds, gap);
        return new PlacementSettings(display.Id, IslandPlacement.Custom, fractions.X, fractions.Y);
    }

    private bool SaveMusicIslandPlacement(PlacementSettings settings)
    {
        if (_closed || IsProfileEditorOpen) return false;
        var fractions = PlacementMath.Fractions(PlacementStore.Normalize(settings));
        var position = HudIslandPosition.Normalize(new(settings.MonitorId, fractions.X, fractions.Y));
        Appearance = Appearance with { Layout = LayoutSettings with { MusicPosition = position } };
        SaveAppearance();
        return Appearance == _savedAppearance;
    }

    private void InitializeMusicIslandMenu()
    {
        var owner = _islandOwner!;
        var menu = IslandSurface.ContextMenu;
        menu.Items.Clear();
        var visibility = new MenuItem();
        visibility.Click += (_, _) => owner.ToggleTrayVisibility();
        var exit = new MenuItem { Header = "Выход" };
        QuickMenuTheme.SetIcon(exit, QuickMenuIcon.Exit);
        exit.Click += OnExit;
        menu.Items.Add(visibility);
        menu.Items.Add(owner.CreateInputMenuItem());
        menu.Items.Add(owner.CreateSettingsMenuItem());
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        QuickMenuTheme.Apply(menu);
        void RefreshActions()
        {
            QuickMenuTheme.UpdateVisibility(visibility, owner.IsVisible, owner.TrayHotkeyAvailable);
        }
        RefreshActions();
        menu.Opened += (_, _) => RefreshActions();
    }

    private void PrepareForIslandClickThrough()
    {
        IslandSurface.ContextMenu.IsOpen = false;
        if (Mouse.Captured is DependencyObject captured && Window.GetWindow(captured) == this) Mouse.Capture(null);
        if (IsKeyboardFocusWithin) Keyboard.ClearFocus();
    }

    private void PrepareMusicIslandForClickThrough() => _musicIsland?.PrepareForIslandClickThrough();

    private bool TrySetAllIslandClickThroughStyles(bool enabled)
    {
        bool previous = IsClickThrough;
        if (!TrySetClickThroughWindowStyle(enabled)) return false;
        if (_musicIsland is not { } music) return true;
        if (!music.TrySetClickThroughWindowStyle(enabled))
        {
            TrySetClickThroughWindowStyle(previous);
            return false;
        }
        music.IsClickThrough = enabled;
        music.Appearance = music.Appearance with { ClickThrough = enabled };
        return true;
    }

    private void CloseMusicIsland()
    {
        if (_musicIsland is not { } music) return;
        _musicIsland = null;
        _automaticMusicPlacement = null;
        music.Closed -= OnMusicIslandClosed;
        music.Close();
        NotifyPresentationVisibilityChanged();
    }

    private void OnMusicIslandClosed(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(_musicIsland, sender)) return;
        _musicIsland!.Closed -= OnMusicIslandClosed;
        _musicIsland = null;
        _automaticMusicPlacement = null;
        NotifyPresentationVisibilityChanged();
    }

    private void DisposeIslandLifecycle()
    {
        IsVisibleChanged -= OnIslandVisibilityChanged;
        ProfileEditorVisibilityChanged -= OnIslandEditorVisibilityChanged;
        CloseMusicIsland();
    }

    // These adapters never read or write a second ordinary preferences file. Only a
    // completed secondary drag is routed into the owner's existing profile store.
    private sealed class MusicIslandAppearanceStore(MainWindow owner) : AppearanceStore
    {
        public override AppearanceSettings Load() => owner.MusicIslandAppearance();
        public override bool Save(AppearanceSettings settings) => true;
    }
    private sealed class MusicIslandComponentStore(MainWindow owner) : ComponentStore
    {
        public override ComponentSettings Load() => owner.MusicIslandComponents();
        public override bool Save(ComponentSettings settings) => true;
    }
    private sealed class MusicIslandPlacementStore(MainWindow owner) : PlacementStore
    {
        public override PlacementSettings Load() => owner.MusicIslandPlacement();
        public override bool Save(PlacementSettings settings) => owner.SaveMusicIslandPlacement(settings);
    }
}
