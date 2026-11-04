using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Luma.Island;
using Luma.Island.Services;

internal static partial class Program
{
    private static async Task RunRoutingChecks()
    {
        string directory = Path.Combine(Output, "routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string appearancePath = Path.Combine(directory, "appearance.json");
        string componentsPath = Path.Combine(directory, "components.json");
        string placementPath = Path.Combine(directory, "placement.json");
        string profilesPath = Path.Combine(directory, "profiles.json");
        string[] ordinaryPaths = { appearancePath, componentsPath, placementPath };
        var appearance = new AppearanceStore(appearancePath);
        var components = new ComponentStore(componentsPath);
        var placement = new PlacementStore(placementPath);
        var profiles = new GameProfileStore(profilesPath);
        var ordinary = new HudProfileSettings(new AppearanceSettings(IslandAppearance.Glass, .65, false, false, .9),
            new ComponentSettings(Music: false, Volume: false, Time: true, Date: false, Battery: false),
            new PlacementSettings(Placement: IslandPlacement.BottomRight));
        var gameA = new HudProfileSettings(new AppearanceSettings(IslandAppearance.Glass, .35, true, false, 1.25),
            ordinary.Components with { Cpu = true, Gpu = true, Fps = true },
            ordinary.Placement with { Placement = IslandPlacement.TopLeft });
        var gameB = new HudProfileSettings(new AppearanceSettings(IslandAppearance.Matte, .76, false, false, .8),
            ordinary.Components with { Time = false, Memory = true },
            ordinary.Placement with { Placement = IslandPlacement.TopRight, Locked = true });
        var configuration = new GameProfileSettings(true, new[]
        {
            Fixture.Profiles[0] with { Settings = gameA }, Fixture.Profiles[1] with { Settings = gameB }
        });
        Check(appearance.Save(ordinary.Appearance) && components.Save(ordinary.Components) && placement.Save(ordinary.Placement) && profiles.Save(configuration),
            "Routing fixture stores are saved independently of real user preferences");
        Dictionary<string, string> baseHashes = HashFiles(ordinaryPaths);
        using var media = new MediaService();
        using var system = new SystemService();
        system.Dispose();
        var controller = new GameProfileController(appearance, components, placement, profiles);
        MainWindow? window = CreateRoutingWindow(controller, media, system);
        try
        {
            window.Show(); await Settle();
            Check(window.ActiveGameProfileId is null && window.Appearance == ordinary.Appearance && window.Components == ordinary.Components,
                "Before an automatic selection the ordinary HUD is restored from the existing stores");
            Check(window.ActivateGameProfile(FirstId), "The configured first game profile activates through the production API");
            await Settle();
            Check(window.ActiveGameProfileId == FirstId && window.Appearance.Scale == 1.25 && window.Appearance.Compact &&
                window.Appearance.GlassOpacity == .35 && window.Components == gameA.Components,
                "Game A applies its scale, compactness, transparency and independent component selection");
            Check(window.Placement.Settings == gameA.Placement, "Game A applies its own monitor-aware placement");
            CheckNativeProfileBounds(window, "Game A native geometry follows its saved anchor and scaled dimensions");
            double firstWidth = window.Width;

            window.SetHudScale(1.35);
            MenuItem appearanceMenu = window.CreateAppearanceMenu();
            Slider scaleSlider = appearanceMenu.Items.OfType<MenuItem>().Select(item => item.Header).OfType<StackPanel>()
                .SelectMany(panel => panel.Children.OfType<Slider>()).Single(slider => slider.Name == "HudScaleSlider");
            string profileBeforePreview = File.ReadAllText(profilesPath);
            scaleSlider.Value = 120;
            await Settle();
            Check(Math.Abs(window.HudScale - 1.2) < .0001 && File.ReadAllText(profilesPath) == profileBeforePreview && SameHashes(baseHashes, ordinaryPaths),
                "The native scale slider previews the active game without writing profile or ordinary settings during movement");
            scaleSlider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Check(profiles.Load().Profiles.Single(profile => profile.Id == FirstId).Settings.Appearance.Scale == 1.2,
                "Completing the scale slider saves the selected game's scale");
            window.SetAppearance(IslandAppearance.Glass, .30);
            window.SetComponents(gameA.Components with { Memory = true });
            window.Placement.SetLocked(false);
            window.Placement.BeginDrag();
            double dpi = DisplayService.GetWindowScale(window);
            Rect draggedBounds = PlacementMath.Bounds(window.Placement.CurrentDisplay.WorkArea,
                new Size(window.Width * dpi, window.Height * dpi), new Point(.31, .64), 8 * dpi);
            DisplayService.MoveWindow(window, draggedBounds.X, draggedBounds.Y);
            window.Placement.EndDrag();
            await Settle();
            Check(window.Placement.Settings.Placement == IslandPlacement.Custom && Math.Abs(window.Placement.Settings.XFraction - .31) < .005 &&
                Math.Abs(window.Placement.Settings.YFraction - .64) < .005,
                "Actual HWND movement followed by the production drag completion saves normalized custom coordinates");
            Check(window.SaveCurrentProfileEdits(), "The current game HUD snapshot is captured successfully");
            HudProfileSettings edited = window.CaptureHudSettings();
            GameProfileSettings persisted = profiles.Load();
            Check(persisted.Profiles.Single(profile => profile.Id == FirstId).Settings == GameProfileStore.NormalizeSnapshot(edited),
                "Active-game edits persist position, scale, appearance and all components together");
            Check(persisted.Profiles.Single(profile => profile.Id == SecondId).Settings == gameB && SameHashes(baseHashes, ordinaryPaths),
                "Editing game A leaves game B and all three ordinary preference files unchanged");

            int interactionsEnded = 0;
            window.ProfileInteractionEnded += () => interactionsEnded++;
            HudProfileSettings beforeDeferredSwitch = window.CaptureHudSettings();
            string profilesBeforeDeferredSwitch = File.ReadAllText(profilesPath);
            window.Placement.BeginDrag();
            Check(window.IsHudDragging, "The production placement controller exposes an active HUD drag to profile selection");
            Check(!window.ActivateGameProfile(SecondId) && window.ActiveGameProfileId == FirstId &&
                window.CaptureHudSettings() == beforeDeferredSwitch && File.ReadAllText(profilesPath) == profilesBeforeDeferredSwitch,
                "A requested game switch during a drag leaves the active profile, HUD and saved game settings unchanged");
            window.Placement.EndDrag();
            Check(!window.IsHudDragging && interactionsEnded == 1,
                "Completing a drag emits the interaction-ended notification used to replay a deferred profile selection");
            Check(window.ActivateGameProfile(SecondId), "The second configured game can replace the active profile");
            await Settle();
            Check(window.Appearance.Mode == IslandAppearance.Matte && window.Appearance.Scale == .8 && !window.Appearance.Compact &&
                window.Components == gameB.Components && window.Width < firstWidth && window.Placement.Settings == gameB.Placement,
                "Game B restores its independent matte style, scale, density, indicators and placement");
            CheckNativeProfileBounds(window, "Game B geometry matches the new scaled anchor without a stale size");
            Check(window.ActivateGameProfile(FirstId), "Returning to game A activates its saved edited profile");
            await Settle();
            Check(window.CaptureHudSettings() == edited, "Returning to game A restores the user's last adjustments rather than its initial snapshot");

            window.InitializeInputRecovery(true, false);
            Check(window.SetClickThrough(true) && window.IsClickThrough, "Input recovery can be enabled globally while a game profile is active");
            var globalInput = appearance.Load();
            Check(globalInput == ordinary.Appearance with { ClickThrough = true }, "Only the global input flag changes in the ordinary appearance store");
            Dictionary<string, string> inputHashes = HashFiles(ordinaryPaths);
            Check(window.ActivateGameProfile(SecondId) && window.IsClickThrough && (InputStyle(window) & 0x08000020u) == 0x08000020u,
                "Switching profiles does not change native global click-through and nonactivation bits");
            Check(window.ActivateGameProfile(null), "A completed game returns the HUD to the ordinary context");
            await Settle();
            Check(window.ActiveGameProfileId is null && window.Appearance == ordinary.Appearance with { ClickThrough = true } &&
                window.Components == ordinary.Components && window.Placement.Settings == ordinary.Placement,
                "Game exit restores the ordinary HUD while retaining the current global input preference");
            Check(SameHashes(inputHashes, ordinaryPaths), "Automatic game switches and game exit never rewrite ordinary preference files");
            CheckNativeProfileBounds(window, "Ordinary geometry is restored after the game context ends");

            Check(window.ActivateGameProfile(FirstId), "The first game is selected again before disabling automatic profiles");
            await Settle();
            HudProfileSettings beforeDisable = window.CaptureHudSettings();
            Rect nativeBeforeDisable = DisplayService.GetWindowBounds(window);
            Check(window.SetGameProfiles(window.GameProfiles with { Enabled = false }), "Disabling automatic profiles saves the user's explicit choice");
            await Settle();
            Check(!window.GameProfiles.Enabled && window.ActiveGameProfileId is null && window.CaptureHudSettings() == beforeDisable,
                "Disabling automatic profiles keeps the current HUD appearance, scale, components and placement unchanged");
            Check(Near(nativeBeforeDisable, DisplayService.GetWindowBounds(window)), "Disabling automatic profiles does not make the HUD jump on screen");
            Check(appearance.Load() == beforeDisable.Appearance && components.Load() == beforeDisable.Components && placement.Load() == beforeDisable.Placement,
                "The kept HUD is committed through the ordinary stores after automatic profiles are disabled");
            Check(!window.ActivateGameProfile(SecondId), "A disabled automatic-profile feature cannot apply a queued game selection");
            window.Close();
            var recreatedController = new GameProfileController(appearance, components, placement, profiles);
            window = CreateRoutingWindow(recreatedController, media, system);
            window.Show(); await Settle();
            Check(!window.GameProfiles.Enabled && window.ActiveGameProfileId is null && window.CaptureHudSettings() == beforeDisable,
                "Restart restores the kept ordinary HUD when automatic profiles are disabled");
            window.SetAppearance(IslandAppearance.Matte);
            Check(appearance.Load().Mode == IslandAppearance.Matte && !profiles.Load().Enabled,
                "Subsequent appearance edits use ordinary persistence while automatic profiles remain off");
        }
        finally { window?.Close(); }
    }

    private static MainWindow CreateRoutingWindow(GameProfileController controller, MediaService media, SystemService system) => new(
        media, system, controller.PlacementStore, controller.AppearanceStore, controller.ComponentStore,
        new AutostartService(readOnly: true), controller)
    { Opacity = 0, Topmost = false, ShowActivated = false, ShowInTaskbar = false };

    private static void CheckNativeProfileBounds(MainWindow window, string description) => Check(
        Near(DisplayService.GetWindowBounds(window), window.Placement.DesiredBounds(window.Height, window.Width)), description);
    private static bool Near(Rect first, Rect second) => Math.Abs(first.X - second.X) <= 1.1 && Math.Abs(first.Y - second.Y) <= 1.1 &&
        Math.Abs(first.Width - second.Width) <= 1.1 && Math.Abs(first.Height - second.Height) <= 1.1;
    private static Dictionary<string, string> HashFiles(IEnumerable<string> paths) => paths.ToDictionary(path => path,
        path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    private static bool SameHashes(Dictionary<string, string> expected, IEnumerable<string> paths) =>
        expected.OrderBy(item => item.Key).SequenceEqual(HashFiles(paths).OrderBy(item => item.Key));
    private static uint InputStyle(MainWindow window) => unchecked((uint)GetWindowLongPtr(new WindowInteropHelper(window).Handle, -20).ToInt64());
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
