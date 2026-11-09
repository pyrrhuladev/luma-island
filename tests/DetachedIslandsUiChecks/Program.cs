using System.Globalization;
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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;
using Luma.Island.Settings;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const long InputMask = 0x20 | 0x08000000;
    private static readonly List<string> Checks = new();
    private static readonly ComponentSettings Full = new(Cpu: true, Gpu: true, Memory: true, Fps: true,
        CpuTemperature: true, GpuTemperature: true, MemoryTemperature: true, OnePercentLow: true, FrameTime: true);
    private static string Output = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : "work/checks/DetachedIslandsUiChecks");
        Directory.CreateDirectory(Output);
        string? failure = null;
        var preferences = RealPreferences();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await RunDraftChecks();
                await RunPresentationChecks();
                await RunPositionLifecycleChecks();
                await RunProfileChecks();
                Check(preferences.OrderBy(p => p.Key).SequenceEqual(RealPreferences().OrderBy(p => p.Key)),
                    "All actual user preference files remain byte-for-byte unchanged");
            }
            catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(error); }
            finally
            {
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks, failure,
                    version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                    assemblySha256 = Hash(typeof(MainWindow).Assembly.Location), demonstrationData = true,
                    isolation = "Only task-owned transparent, nonactivating WPF windows, fixture stores and synthetic metadata. No App startup, performance/FPS collectors, media initialization, playback or volume commands, shell modification, actual tray icon, global input, hotkey registration, sensor service changes or real preference writes. Native movement is limited to the fixture windows. Screenshots render their content offscreen."
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} detached-islands UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunDraftChecks()
    {
        var position = new HudIslandPosition(@"\\.\DISPLAY-fixture", .123456789123, .876543219876);
        var original = AppearanceSettings.Default with { Layout = new(MusicPosition: position) };
        int before = Application.Current.Windows.OfType<MainWindow>().Count();
        var cancelled = NewEditor(original);
        try
        {
            Check(!Named<CheckBox>(cancelled, "DetachedMusicCheckBox").IsChecked.GetValueOrDefault(),
                "Old or merged settings open with the separate-islands option disabled");
            Named<CheckBox>(cancelled, "DetachedMusicCheckBox").IsChecked = true;
            Check(cancelled.DraftAppearance.Layout.DetachedMusic && cancelled.HasUnsavedChanges,
                "The separate-islands checkbox changes only the editor draft and dirty indicator");
            Check(cancelled.DraftAppearance.Layout.MusicPosition == position && !original.Layout.DetachedMusic,
                "Toggling split mode preserves exact saved coordinates and the supplied immutable settings");
            Named<Slider>(cancelled, "TextScaleSlider").Value = 117;
            Check(cancelled.CommitDraft() && cancelled.DraftAppearance.Layout.DetachedMusic &&
                  cancelled.DraftAppearance.Layout.MusicPosition == position,
                "Capturing unrelated layout edits retains the complete split-mode position");
            Check(Application.Current.Windows.OfType<MainWindow>().Count() == before,
                "The production preview of a split draft creates no detached or main native presentation window");
            var combined = (BitmapSource)Named<Image>(cancelled, "HudPreviewImage").Source;
            var onlyMetrics = MainWindow.RenderHudPreview(cancelled.DraftAppearance, Full with { Music = false, Volume = false });
            var onlyMusic = MainWindow.RenderHudPreview(cancelled.DraftAppearance with
                { Layout = cancelled.DraftAppearance.Layout with { DetachedMusic = false }, Alerts = AlertSettings.Default },
                new(Music: true, Volume: true, Time: false, Date: false, Battery: false));
            Check(combined.PixelHeight == onlyMetrics.PixelHeight + 12 + onlyMusic.PixelHeight &&
                  combined.PixelWidth == Math.Max(onlyMetrics.PixelWidth, onlyMusic.PixelWidth),
                "The split preview contains both production presentations separated by the intended gap");
            Check(HasAlpha(combined, 0, onlyMetrics.PixelHeight) &&
                  !HasAlpha(combined, onlyMetrics.PixelHeight, onlyMetrics.PixelHeight + 12) &&
                  HasAlpha(combined, onlyMetrics.PixelHeight + 12, combined.PixelHeight),
                "The split preview has distinct rendered metrics and music bands with a fully transparent separator");
        }
        finally { cancelled.Close(); }
        Check(!original.Layout.DetachedMusic && original.Layout.MusicPosition == position,
            "Closing an unaccepted settings editor leaves the original split choice and position unchanged");

        var reset = NewEditor(original with { Layout = original.Layout with { DetachedMusic = true } });
        try
        {
            Click(Named<Button>(reset, "ResetMusicPositionButton"));
            Check(reset.DraftAppearance.Layout.DetachedMusic && reset.DraftAppearance.Layout.MusicPosition is null,
                "Resetting the music position is a local draft operation that keeps split mode enabled");
            Check(reset.CommitDraft() && reset.DraftAppearance.Layout.MusicPosition is null && reset.HasUnsavedChanges,
                "The position reset survives final draft capture and marks the editor dirty");
            Check(original.Layout.MusicPosition == position, "A position reset cannot mutate the supplied settings record");
        }
        finally { reset.Close(); }

        var untouched = NewEditor(original with { Layout = original.Layout with { DetachedMusic = true } });
        try
        {
            Check(!untouched.HasUnsavedChanges && untouched.CommitDraft() && !untouched.HasUnsavedChanges,
                "Opening and accepting unchanged split settings causes no dirty-state drift");
            Check(untouched.DraftAppearance.Layout.MusicPosition == position,
                "Unchanged Save preserves fractional detached placement without rounding");
        }
        finally { untouched.Close(); }
        await Settle();
    }

    private static async Task RunPresentationChecks()
    {
        using var fixture = new Fixture("presentation", AppearanceSettings.Default, Full);
        var window = fixture.Window;
        window.Show(); await Settle();
        Check(window.MusicIsland is null && !window.IsMusicIsland && window.IsHudPresentationVisible,
            "The default mode retains one visible presentation and creates no music island");
        Check(Named<Grid>(window, "CompactMusicRow").Visibility == Visibility.Visible,
            "Merged mode still renders music in the original HUD");
        int oneSubscription = Subscribers(fixture.Media);
        var primaryPlacement = window.Placement.Settings;
        window.SetHudLayout(window.Appearance.Layout with { DetachedMusic = true }); await Settle();
        var child = RequireMusicIsland(window);
        Check(child.IsMusicIsland && child.IsVisible && child.Owner == window,
            "Split mode creates a visible owned music presentation");
        Check(child.Opacity == 0 && !child.ShowActivated && !child.Topmost && !child.ShowInTaskbar,
            "A detached fixture inherits invisible, nonactivating and non-taskbar window behavior");
        Check(ReferenceEquals(Field<MediaService>(child, "_media"), fixture.Media) &&
              ReferenceEquals(Field<SystemService>(child, "_system"), fixture.System),
            "Both islands use the same media and volume service instances");
        Check(Subscribers(fixture.Media) == oneSubscription + 1,
            "Exactly one extra media listener serves the music presentation");
        Check(window.Components == Full && window.Placement.Settings == primaryPlacement,
            "Splitting preserves the configured component selection and primary position");
        Check(Named<Grid>(window, "CompactMusicRow").Visibility == Visibility.Collapsed &&
              !Named<StackPanel>(window, "CompactPlaybackControls").IsVisible &&
              Named<FrameworkElement>(window, "CompactVolume").Visibility == Visibility.Collapsed,
            "The metrics island leaves transport and volume to the detached music island");
        Check(Named<UniformGrid>(window, "CompactPerformance").Visibility == Visibility.Visible &&
              Named<TextBlock>(window, "CompactCpu").Visibility == Visibility.Visible &&
              Named<TextBlock>(window, "CompactGpu").Visibility == Visibility.Visible &&
              Named<TextBlock>(window, "CompactMemory").Visibility == Visibility.Visible,
            "CPU, GPU and RAM stay together in the primary island");
        Check(child.Components.Music && child.Components.Volume && !child.Components.Time && !child.Components.Date &&
              !child.Components.Battery && !child.HasPerformance && !child.Appearance.Alerts.Enabled,
            "The music presentation has no duplicate hardware, clock, battery, graph or warning components");
        Check(Named<Grid>(child, "CompactMusicRow").Visibility == Visibility.Visible &&
              Named<UniformGrid>(child, "CompactPerformance").Visibility == Visibility.Collapsed,
            "The detached view renders its original music controls without a metrics row");
        Check((Style(window) & 0x80) != 0 && (Style(child) & 0x80) != 0 && (Style(child) & 0x40000) == 0,
            "Both native presentations remain tool windows excluded from Alt+Tab");

        var menu = Named<Border>(child, "IslandSurface").ContextMenu;
        Check(menu.Items.Count == 5 && menu.Items[3] is Separator &&
              menu.Items.OfType<MenuItem>().Select(item => (string)item.Header)
                .SequenceEqual(new[] { "Скрыть в трей", "Пропускать клики в игру", "Настройки…", "Выход" }),
            "The detached quick menu offers the same four actions as the main HUD");
        foreach (string name in new[] { "CompactPreviousButton", "CompactPlayButton", "CompactNextButton" })
        {
            var button = Named<Button>(child, name);
            Check(!Ancestors(button).OfType<Button>().Any() && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)),
                "Detached transport is independently clickable and accessible: " + name);
        }

        BitmapSource firstArtwork = Artwork(Colors.SeaGreen), secondArtwork = Artwork(Colors.OrangeRed);
        Publish(fixture.Media, new("Первая композиция", "Тестовый исполнитель", true, true, true, true, true, firstArtwork));
        await Settle();
        Check(Named<TextBlock>(child, "CompactTitle").Text == "Первая композиция" &&
              ReferenceEquals(Named<Image>(child, "CompactArtwork").Source, firstArtwork),
            "Shared media notifications deliver the current title and artwork to the detached island");
        Publish(fixture.Media, new("Вторая композиция", "Другой исполнитель", false, true, true, true, true, null));
        await Settle();
        Check(Named<TextBlock>(child, "CompactTitle").Text == "Вторая композиция" &&
              Named<Image>(child, "CompactArtwork").Source is null,
            "A track change immediately clears the old cover");
        Publish(fixture.Media, new("Вторая композиция", "Другой исполнитель", true, true, true, true, true, secondArtwork));
        await Settle();
        Check(ReferenceEquals(Named<Image>(child, "CompactArtwork").Source, secondArtwork),
            "A later cover notification updates the detached music island");
        Check(AutomationProperties.GetName(Named<Button>(child, "CompactPlayButton")) == "Пауза" &&
              Named<Button>(child, "CompactPreviousButton").IsEnabled && Named<Button>(child, "CompactNextButton").IsEnabled,
            "Detached transport state follows the same synthetic session capabilities");

        Rect primaryBounds = DisplayService.GetWindowBounds(window);
        var display = window.Placement.CurrentDisplay;
        child.Placement.BeginDrag();
        Check(window.IsHudDragging, "A music drag participates in the shared profile-switch interaction guard");
        DisplayService.MoveWindow(child, display.WorkArea.Left + display.WorkArea.Width * .31,
            display.WorkArea.Top + display.WorkArea.Height * .44);
        child.Placement.EndDrag(); await Settle();
        var moved = window.Appearance.Layout.MusicPosition;
        Check(moved is not null && moved.MonitorId == child.Placement.Settings.MonitorId,
            "A completed music drag captures its own monitor and position into the layout settings");
        Check(moved == fixture.Appearance.Load().Layout.MusicPosition &&
              Math.Abs(moved!.XFraction - child.Placement.Settings.XFraction) < 1e-10 &&
              Math.Abs(moved.YFraction - child.Placement.Settings.YFraction) < 1e-10,
            "Detached placement is persisted atomically through the existing appearance route");
        Check(window.Placement.Settings == primaryPlacement && Near(primaryBounds, DisplayService.GetWindowBounds(window)),
            "Dragging the music island neither moves nor rewrites the metrics island");

        Rect childBeforePrimaryMove = DisplayService.GetWindowBounds(child);
        window.Placement.SetPreset(IslandPlacement.BottomRight); await Settle();
        Check(Near(childBeforePrimaryMove, DisplayService.GetWindowBounds(child)) && window.Appearance.Layout.MusicPosition == moved,
            "Moving the primary island leaves the detached placement independent");

        window.SetAppearance(IslandAppearance.Glass, .12); window.SetCompactMode(true); window.SetHudScale(1.25);
        await Settle();
        Check(child.Appearance.Mode == IslandAppearance.Glass && child.Appearance.GlassOpacity == .12 &&
              child.Appearance.Compact && child.Appearance.Scale == 1.25,
            "Transparency, density and scale changes update the existing detached presentation");
        Check(window.Appearance.Layout.MusicPosition == moved &&
              child.Placement.Settings.XFraction == moved!.XFraction && child.Placement.Settings.YFraction == moved.YFraction,
            "Shared style changes preserve the exact detached anchor fractions");
        Check(ReferenceEquals(child, window.MusicIsland), "Style changes reuse the same music HWND instead of recreating it");

        window.ApplyPerformance(new(43, 76, 48, 16UL << 30, 32UL << 30, 68, "Пример", 62, "Пример", 51, "Пример"));
        window.ApplyFps(new(144, "Пример", "Демонстрационные данные", 120, 6.9));
        await Settle();
        CaptureComposition(window, child, "detached-islands.png");
        Check(Named<Grid>(child, "CompactView").IsVisible && Named<Grid>(window, "CompactView").IsVisible,
            "Both islands display their compact content at once");
        Check(Named<FrameworkElement>(child, "CompactVolume").IsVisible &&
              Named<Slider>(child, "VolumeControl").IsVisible &&
              child.FindName("ExpandedView") is null && child.FindName("CompactExpandButton") is null,
            "Detached volume is available alongside compact transport in the single view");

        double musicHeightWithVolume = child.CollapsedWindowHeight;
        window.SetComponents(Full with { Volume = false, VolumeWheel = true }); await Settle();
        Check(ReferenceEquals(child, window.MusicIsland) && child.IsVisible &&
              Named<Grid>(child, "CompactMusicRow").IsVisible && !Named<FrameworkElement>(child, "CompactVolume").IsVisible &&
              child.CollapsedWindowHeight < musicHeightWithVolume,
            "Hiding the volume row shortens the existing music island and keeps its player visible");
        Check(window.Components.VolumeWheel && child.Components.VolumeWheel,
            "The independent wheel preference reaches both islands with the volume row hidden");
        window.SetComponents(Full); await Settle();
        Check(Named<FrameworkElement>(child, "CompactVolume").IsVisible && !child.Components.VolumeWheel,
            "Restoring the detached volume row leaves wheel control independently disabled");

        window.InitializeInputRecovery(true, true);
        Check(window.SetClickThrough(true) && window.IsClickThrough && child.IsClickThrough &&
              (Style(window) & InputMask) == InputMask && (Style(child) & InputMask) == InputMask,
            "One input-mode action enables native click-through on both islands");
        Check(Named<Grid>(child, "CompactView").IsVisible && Named<Slider>(child, "VolumeControl").IsVisible,
            "Click-through keeps the detached music presentation visible");
        Check(window.SetClickThrough(false) && !window.IsClickThrough && !child.IsClickThrough &&
              (Style(window) & InputMask) == 0 && (Style(child) & InputMask) == 0,
            "One recovery action restores native interaction to both islands");
        window.SetClickThrough(true); window.UpdateInputRecovery(false, false);
        Check(!window.IsClickThrough && !child.IsClickThrough &&
              (Style(window) & InputMask) == 0 && (Style(child) & InputMask) == 0,
            "Losing all recovery routes unlocks both native presentations together");
        window.UpdateInputRecovery(true, true);

        int visibilityEvents = 0; window.PresentationVisibilityChanged += () => visibilityEvents++;
        window.HideToTray(); await Settle();
        Check(!window.IsVisible && !child.IsVisible && !window.IsHudPresentationVisible,
            "Hiding to the tray hides both islands and reports no visible presentation");
        window.RestoreFromTray(); await Settle();
        Check(window.IsVisible && child.IsVisible && window.IsHudPresentationVisible && visibilityEvents >= 2,
            "Restoring shows both saved islands and notifies the shared collector gate");
        Check(window.Appearance.Layout.MusicPosition == moved && ReferenceEquals(child, window.MusicIsland),
            "A visibility cycle preserves the detached position and existing HWND");
        child.HideToTray(); await Settle();
        Check(!window.IsVisible && !child.IsVisible, "The music island's hide action routes to the whole HUD");
        window.RestoreForInteraction(); await Settle();
        Check(window.IsVisible && child.IsVisible && !window.IsClickThrough && !child.IsClickThrough,
            "Explicit tray restoration returns both islands in interactive mode");

        bool childClosed = false; child.Closed += (_, _) => childClosed = true;
        window.SetHudLayout(window.Appearance.Layout with { DetachedMusic = false }); await Settle();
        Check(window.MusicIsland is null && childClosed && !child.IsVisible,
            "Disabling split mode closes and releases the detached window");
        Check(Named<Grid>(window, "CompactMusicRow").Visibility == Visibility.Visible &&
              ReferenceEquals(Named<Image>(window, "CompactArtwork").Source, secondArtwork),
            "Merging restores the latest track and cover in the original HUD");
        Check(window.Appearance.Layout.MusicPosition == moved && Subscribers(fixture.Media) == oneSubscription,
            "Merging remembers the detached position and removes the extra media subscription");

        for (int i = 0; i < 3; i++)
        {
            window.SetHudLayout(window.Appearance.Layout with { DetachedMusic = true }); await Settle();
            var again = RequireMusicIsland(window);
            Check(again.Placement.Settings.XFraction == moved!.XFraction && again.Placement.Settings.YFraction == moved.YFraction &&
                  Subscribers(fixture.Media) == oneSubscription + 1,
                "Repeated split restores the independent anchor with exactly one listener: cycle " + i);
            window.SetHudLayout(window.Appearance.Layout with { DetachedMusic = false }); await Settle();
            Check(Subscribers(fixture.Media) == oneSubscription && window.MusicIsland is null,
                "Repeated merge releases the listener and detached window: cycle " + i);
        }

        window.SetHudLayout(window.Appearance.Layout with { DetachedMusic = true }); await Settle();
        window.SetComponents(Full with { Music = false, Volume = false, VolumeWheel = true }); await Settle();
        Check(window.MusicIsland is null && window.Appearance.Layout.DetachedMusic && window.Components.VolumeWheel,
            "Wheel control remains enabled without creating an empty detached music island");
        window.SetComponents(Full with { Music = false, Volume = true }); await Settle();
        var volumeOnly = RequireMusicIsland(window);
        Check(volumeOnly.IsVisible && !volumeOnly.Components.Music && volumeOnly.Components.Volume,
            "A volume-only configuration still has an independently accessible island");
        Check(Named<FrameworkElement>(volumeOnly, "CompactVolume").IsVisible &&
              !Named<Grid>(volumeOnly, "CompactMusicRow").IsVisible,
            "A volume-only island displays its control immediately");

        window.SetComponents(new(Music: true, Volume: true, Time: false, Date: false, Battery: false)); await Settle();
        Check(window.IsHudPresentationVisible && RequireMusicIsland(window).IsVisible,
            "A configuration containing only music still reports a live HUD presentation");
        Check(Field<Task?>(fixture.Media, "_initialization") is null && !Field<bool>(fixture.Media, "_disposed"),
            "All presentation, style and media tests leave the shared media service uninitialized and alive");
        var lastChild = RequireMusicIsland(window); bool lastClosed = false; lastChild.Closed += (_, _) => lastClosed = true;
        window.Close(); await Settle(); fixture.WindowClosed = true;
        Check(lastClosed && window.MusicIsland is null && Subscribers(fixture.Media) == 0,
            "Closing the root closes its child and releases every view's media listener");
        Check(!Field<bool>(fixture.Media, "_disposed"), "Closing view windows does not dispose the application's shared media service");
    }

    private static async Task RunProfileChecks()
    {
        using var fixture = new Fixture("profiles", AppearanceSettings.Default, Full, profiles: true);
        var window = fixture.Window;
        string first = Guid.NewGuid().ToString("D"), second = Guid.NewGuid().ToString("D"), fresh = Guid.NewGuid().ToString("D");
        var firstPosition = new HudIslandPosition(null, .73, .67);
        var secondPosition = new HudIslandPosition(null, .12, .82);
        var primaryFirst = PlacementSettings.Default with { Placement = IslandPlacement.TopLeft };
        var firstHud = new HudProfileSettings(AppearanceSettings.Default with { Layout = new(DetachedMusic: true, MusicPosition: firstPosition) }, Full, primaryFirst);
        var secondHud = new HudProfileSettings(AppearanceSettings.Default with { Layout = new(DetachedMusic: false, MusicPosition: secondPosition) }, Full,
            PlacementSettings.Default with { Placement = IslandPlacement.BottomLeft });
        Check(fixture.Controller!.ReplaceSettings(new(true, new[]
        {
            new GameProfile(first, "Тестовая игра A", @"D:\Games\FixtureA\game-a.exe", firstHud),
            new GameProfile(second, "Тестовая игра B", @"D:\Games\FixtureB\game-b.exe", secondHud),
            new GameProfile(fresh, "Новая игра", @"D:\Games\FixtureC\game-c.exe", firstHud with
                { Appearance = AppearanceSettings.Default with { Layout = new(DetachedMusic: true) },
                  Placement = PlacementSettings.Default with { Placement = IslandPlacement.BottomRight } })
        })), "Isolated game profiles retain independent split choices, explicit anchors and an unset initial anchor");
        window.Show(); await Settle();
        string[] baseHashes = fixture.BaseHashes();
        Check(window.ActivateGameProfile(first), "The first game's split profile can be activated"); await Settle();
        var child = RequireMusicIsland(window);
        Check(window.Appearance.Layout.DetachedMusic && child.Placement.Settings.XFraction == firstPosition.XFraction &&
              child.Placement.Settings.YFraction == firstPosition.YFraction && window.Placement.Settings == primaryFirst,
            "Activating a game restores the separate music and metrics positions");
        Check(fixture.BaseHashes().SequenceEqual(baseHashes), "Selecting a split game profile does not rewrite ordinary preferences");

        int dragCompletions = 0; window.ProfileInteractionEnded += () => dragCompletions++;
        child.Placement.BeginDrag();
        Check(window.IsHudDragging && !window.ActivateGameProfile(second) && window.ActiveGameProfileId == first,
            "A profile switch is deferred while the detached island is being dragged");
        var workArea = child.Placement.CurrentDisplay.WorkArea;
        DisplayService.MoveWindow(child, workArea.Left + workArea.Width * .41, workArea.Top + workArea.Height * .36);
        child.Placement.EndDrag(); await Settle();
        var changedPosition = window.Appearance.Layout.MusicPosition;
        Check(!window.IsHudDragging && dragCompletions == 1,
            "Ending a music drag clears the shared interaction guard and emits one completion");
        Check(changedPosition is not null && changedPosition != firstPosition &&
              fixture.Profiles.Load().Profiles.Single(p => p.Id == first).Settings.Appearance.Layout.MusicPosition == changedPosition,
            "A music drag in a game saves only that game's latest independent anchor");
        Check(fixture.BaseHashes().SequenceEqual(baseHashes), "Saving a detached game position leaves ordinary preference bytes unchanged");
        Check(fixture.Profiles.Load().Profiles.Single(p => p.Id == second).Settings.Appearance.Layout.MusicPosition == secondPosition,
            "Moving one game's island does not alter another game's saved anchor");

        Check(window.ActivateGameProfile(second), "Another game's merged profile can be activated after dragging ends"); await Settle();
        Check(window.MusicIsland is null && !window.Appearance.Layout.DetachedMusic &&
              Named<Grid>(window, "CompactMusicRow").Visibility == Visibility.Visible,
            "A profile with split disabled returns the player to the main HUD");
        Check(window.ActivateGameProfile(first), "The split profile can be activated again"); await Settle();
        child = RequireMusicIsland(window);
        Check(window.Appearance.Layout.MusicPosition == changedPosition &&
              child.Placement.Settings.XFraction == changedPosition!.XFraction && child.Placement.Settings.YFraction == changedPosition.YFraction,
            "Returning to a game restores its latest dragged position rather than the original profile snapshot");

        Check(window.ActivateGameProfile(fresh), "A newly configured split profile with no music coordinates can be activated"); await Settle();
        var freshChild = RequireMusicIsland(window);
        Check(window.Appearance.Layout.MusicPosition is not null && window.Appearance.Layout.MusicPosition != changedPosition &&
              CloseTo(DisplayService.GetWindowBounds(window), DisplayService.GetWindowBounds(freshChild)),
            "A new split profile derives its own initial placement beside its current HUD instead of reusing another game's cached anchor");
        var freshMaterializedPosition = window.Appearance.Layout.MusicPosition;
        Rect freshChildBounds = DisplayService.GetWindowBounds(freshChild);
        Rect freshArea = window.Placement.CurrentDisplay.WorkArea;
        window.Placement.BeginDrag();
        DisplayService.MoveWindow(window, freshArea.Left + freshArea.Width * .26, freshArea.Top + freshArea.Height * .27);
        window.Placement.EndDrag();
        Check(window.Appearance.Layout.MusicPosition == freshMaterializedPosition &&
              Near(freshChildBounds, DisplayService.GetWindowBounds(freshChild)),
            "Dragging only a freshly activated profile's metrics island leaves its first materialized music position fixed");
        var freshPersisted = fixture.Profiles.Load().Profiles.Single(p => p.Id == fresh).Settings;
        Check(freshPersisted.Appearance.Layout.MusicPosition == freshMaterializedPosition &&
              freshPersisted.Placement == window.Placement.Settings,
            "A primary-only drag immediately persists both positions of a profile whose stored music anchor was initially unset");
        Check(fixture.BaseHashes().SequenceEqual(baseHashes),
            "Materializing and saving a new game's music anchor never rewrites ordinary preferences");
        Check(window.ActivateGameProfile(first), "Returning from a new split profile can restore the edited profile again"); await Settle();
        Check(window.Appearance.Layout.MusicPosition == changedPosition,
            "Materializing a different profile's default anchor does not overwrite an edited game's position");

        Check(window.ApplySettingsDraft(window.Appearance, window.Components,
            new(window.Placement.Settings with { Locked = true }, fixture.Controller.Settings, false, false), first),
            "The shared placement lock can be accepted through unified settings while a split profile is active");
        await Settle();
        Check(window.Placement.Settings.Locked && RequireMusicIsland(window).Placement.Settings.Locked,
            "The unified placement lock applies to both independent islands");
        Check(fixture.BaseHashes().SequenceEqual(baseHashes), "A split profile's unified settings still preserve ordinary files");

        Check(window.SetGameProfiles(window.GameProfiles with { Enabled = false }),
            "Automatic profiles can be disabled while using separate islands"); await Settle();
        Check(window.ActiveGameProfileId is null && fixture.Appearance.Load().Layout.DetachedMusic &&
              fixture.Appearance.Load().Layout.MusicPosition == changedPosition,
            "Disabling automatic profiles promotes the current split mode and both positions to ordinary settings");
        Check(window.MusicIsland?.IsVisible == true, "Promoting the current split view keeps the player accessible");
    }

    private static async Task RunPositionLifecycleChecks()
    {
        using var fixture = new Fixture("position-lifecycle", AppearanceSettings.Default, Full);
        var window = fixture.Window;
        window.Show(); await Settle();
        window.Placement.SetPreset(IslandPlacement.TopLeft);
        window.SetHudLayout(window.Appearance.Layout with { DetachedMusic = true }); await Settle();
        var initialChild = RequireMusicIsland(window);
        var initialPosition = window.Appearance.Layout.MusicPosition;
        Check(initialPosition is not null && initialPosition == fixture.Appearance.Load().Layout.MusicPosition,
            "First enabling split mode materializes and saves an independent music anchor");
        Check(CloseTo(DisplayService.GetWindowBounds(window), DisplayService.GetWindowBounds(initialChild)),
            "The initial music island is placed beside the current primary island within its monitor");
        Rect childBounds = DisplayService.GetWindowBounds(initialChild);
        Rect area = window.Placement.CurrentDisplay.WorkArea;
        window.Placement.BeginDrag();
        DisplayService.MoveWindow(window, area.Left + area.Width * .61, area.Top + area.Height * .58);
        window.Placement.EndDrag(); await Settle();
        Check(window.Appearance.Layout.MusicPosition == initialPosition && Near(childBounds, DisplayService.GetWindowBounds(initialChild)),
            "Dragging only the metrics island leaves the first materialized music anchor independent");
        Check(fixture.Appearance.Load().Layout.MusicPosition == initialPosition &&
              fixture.Placement.Load() == window.Placement.Settings,
            "Completing a primary-only drag persists both independent positions");
        window.Close(); fixture.WindowClosed = true; await Settle();
        var restarted = new MainWindow(fixture.Media, fixture.System, fixture.Placement, fixture.Appearance, fixture.Components,
            new AutostartService(readOnly: true)) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        try
        {
            restarted.Show(); await Settle();
            var restored = RequireMusicIsland(restarted);
            Check(restarted.Appearance.Layout.MusicPosition == initialPosition && Near(childBounds, DisplayService.GetWindowBounds(restored)),
                "Reopening after a metrics-only drag restores the music island at its original independent position");
            restarted.Placement.SetPreset(IslandPlacement.BottomRight); await Settle();
            restarted.SetHudLayout(restarted.Appearance.Layout with { MusicPosition = null }); await Settle();
            Check(restarted.Appearance.Layout.MusicPosition is not null && restarted.Appearance.Layout.MusicPosition != initialPosition &&
                  CloseTo(DisplayService.GetWindowBounds(restarted), DisplayService.GetWindowBounds(RequireMusicIsland(restarted))),
                "Resetting the music position after moving the primary derives a fresh anchor beside the current HUD");
            Check(fixture.Appearance.Load().Layout.MusicPosition == restarted.Appearance.Layout.MusicPosition,
                "The newly reset music anchor is saved for the next launch");
        }
        finally { restarted.Close(); }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root;
        public readonly AppearanceStore Appearance;
        public readonly ComponentStore Components;
        public readonly PlacementStore Placement;
        public readonly GameProfileStore Profiles;
        public readonly MediaService Media = new();
        public readonly SystemService System = new();
        public readonly GameProfileController? Controller;
        public readonly MainWindow Window;
        public bool WindowClosed;
        public Fixture(string name, AppearanceSettings appearance, ComponentSettings components, bool profiles = false)
        {
            Root = Path.Combine(Output, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            Appearance = new(Path.Combine(Root, "appearance.json")); Components = new(Path.Combine(Root, "components.json"));
            Placement = new(Path.Combine(Root, "placement.json")); Profiles = new(Path.Combine(Root, "profiles.json"));
            if (!Appearance.Save(appearance) || !Components.Save(components) || !Placement.Save(PlacementSettings.Default))
                throw new IOException("Unable to initialize isolated fixture settings.");
            System.Dispose();
            if (profiles) Controller = new(Appearance, Components, Placement, Profiles);
            Window = new MainWindow(Media, System, Controller?.PlacementStore ?? Placement, Controller?.AppearanceStore ?? Appearance,
                Controller?.ComponentStore ?? Components, new AutostartService(readOnly: true), Controller)
                { Opacity = 0, Topmost = false, ShowActivated = false, ShowInTaskbar = false };
        }
        public string[] BaseHashes() => new[] { "appearance.json", "components.json", "placement.json" }.Select(name => Hash(Path.Combine(Root, name))).ToArray();
        public void Dispose()
        {
            if (!WindowClosed) Window.Close();
            Media.Dispose(); System.Dispose();
        }
    }

    private static HudEditorWindow NewEditor(AppearanceSettings appearance) =>
        new(appearance, Full, "Тестовый HUD", MainWindow.RenderHudPreview)
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -30000, Top = -30000 };
    private static MainWindow RequireMusicIsland(MainWindow owner) => owner.MusicIsland ?? throw new InvalidOperationException("The music presentation was not created.");
    private static T Named<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T ?? throw new InvalidOperationException("Missing control: " + name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static int Subscribers(MediaService media) => typeof(MediaService).GetField("Changed", Private)!.GetValue(media) is Delegate handlers ? handlers.GetInvocationList().Length : 0;
    private static void Publish(MediaService media, MediaSnapshot value)
    {
        typeof(MediaService).GetProperty(nameof(MediaService.Current))!.SetValue(media, value);
        (typeof(MediaService).GetField("Changed", Private)!.GetValue(media) as Action<MediaSnapshot>)?.Invoke(value);
    }
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent)) yield return parent;
    }
    private static long Style(Window window) => GetWindowLongPtr(new WindowInteropHelper(window).Handle, -20).ToInt64();
    private static bool Near(Rect first, Rect second) => Math.Abs(first.Left - second.Left) < 1.1 && Math.Abs(first.Top - second.Top) < 1.1 &&
        Math.Abs(first.Width - second.Width) < 1.1 && Math.Abs(first.Height - second.Height) < 1.1;
    private static bool CloseTo(Rect primary, Rect music) => !primary.IntersectsWith(music) &&
        Math.Abs(primary.Left + primary.Width / 2 - music.Left - music.Width / 2) <= Math.Max(primary.Width, music.Width) &&
        Math.Min(Math.Abs(primary.Bottom - music.Top), Math.Abs(primary.Top - music.Bottom)) <= 32;
    private static async Task Settle() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(20); }
    private static void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); Checks.Add(description); }
    private static string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    private static Dictionary<string, string> RealPreferences() => new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }.ToDictionary(name => name,
        name => Hash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland", name)));
    private static BitmapSource Artwork(Color color)
    {
        byte[] pixels = new byte[64 * 64 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4); bitmap.Freeze(); return bitmap;
    }
    private static bool HasAlpha(BitmapSource source, int firstRow, int lastRow)
    {
        int stride = source.PixelWidth * 4; byte[] pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        for (int y = firstRow; y < lastRow; y++)
            for (int x = 0; x < source.PixelWidth; x++) if (pixels[y * stride + x * 4 + 3] != 0) return true;
        return false;
    }
    private static void CaptureComposition(MainWindow primary, MainWindow music, string name)
    {
        static BitmapSource Render(MainWindow window)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width), (int)Math.Ceiling(window.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render((Visual)window.Content); bitmap.Freeze(); return bitmap;
        }
        BitmapSource left = Render(primary), right = Render(music);
        int width = Math.Max(960, left.PixelWidth + right.PixelWidth + 100), height = Math.Max(320, Math.Max(left.PixelHeight, right.PixelHeight) + 100);
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 23, 28)), null, new Rect(0, 0, width, height));
            var caption = new FormattedText("Luma Island · отдельные островки", CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 18, Brushes.White, 1);
            context.DrawText(caption, new Point(30, 20));
            context.DrawText(new FormattedText("Демонстрационные данные · реальные элементы приложения", CultureInfo.GetCultureInfo("ru-RU"),
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.LightGray, 1), new Point(30, height - 30));
            context.DrawImage(left, new Rect(30, 64, left.PixelWidth, left.PixelHeight));
            context.DrawImage(right, new Rect(left.PixelWidth + 70, 64, right.PixelWidth, right.PixelHeight));
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); result.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
        using var stream = File.Create(Path.Combine(Output, name)); encoder.Save(stream);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
