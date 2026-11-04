using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Luma.Island.Services;
using Luma.Island.Settings;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static string Output = null!;
    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : "work/checks/SettingsApplicationUiChecks");
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
                    version = typeof(SettingsApplicationPages).Assembly.GetName().Version?.ToString(),
                    assemblySha256 = Hash(typeof(SettingsApplicationPages).Assembly.Location),
                    isolation = "Unshown draft controls, fake displays/profile paths and explicit fake callbacks. No SDK, collectors, media session, actual preference/registry writes, system settings launch, file picker or global input."
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} settings application page UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        var before = Preferences();
        string firstId = Guid.NewGuid().ToString("D"), secondId = Guid.NewGuid().ToString("D");
        var initialPlacement = new PlacementSettings(null, IslandPlacement.Custom, .12345678901234, .88765432101234);
        var initialProfiles = new GameProfileSettings(true, new[]
        {
            new GameProfile(firstId, "Первая игра", @"D:\Games\A\A.exe", HudProfileSettings.Default),
            new GameProfile(secondId, "Вторая игра", @"D:\Games\B\B.exe", HudProfileSettings.Default)
        });
        var currentHud = new HudProfileSettings(AppearanceSettings.Default with
        {
            GlassOpacity = .13, ClickThrough = true, Layout = new(MetricOrder: "memory,gpu,cpu", MetricColumns: 2),
            Alerts = new(Enabled: true, CpuTemperature: false, Stutters: false)
        }, ComponentSettings.Default with { Cpu = true, Fps = true }, initialPlacement);
        var displays = new[] { new DisplayInfo("display-a", "Первый экран", new Rect(0, 0, 1920, 1040), true),
            new DisplayInfo("display-b", "Второй экран", new Rect(-1920, 0, 1920, 1040), false) };
        int changed = 0, refreshes = 0, startupActions = 0, sensorActions = 0;
        bool startupThrows = true;
        var owner = new Window { ShowActivated = false, ShowInTaskbar = false };
        var context = new SettingsEditorContext(initialPlacement, HudProfileSettings.Default, initialProfiles, firstId,
            displays, new AutostartState(true, true),
            _ => { sensorActions++; throw new InvalidOperationException("Датчики: тестовая ошибка"); },
            () => { startupActions++; if (startupThrows) throw new InvalidOperationException("Windows: тестовая ошибка"); },
            () => { refreshes++; return new[] { displays[0] }; });
        var pages = new SettingsApplicationPages(context, () => currentHud, () => changed++, () => owner);
        try
        {
            Check(pages.FindControl("PlacementMonitorCombo") is null && pages.FindControl("SettingsProfileList") is null,
                "Unvisited pages do not create controls");
            Check(pages.Capture().Placement == initialPlacement && !pages.Capture().AutostartChanged, "Unvisited pages preserve the complete initial draft");
            Check(pages.Validate() is null && startupActions == 0 && sensorActions == 0 && refreshes == 0, "Validation and capture never run external actions");
            var positionPage = pages.CreatePositionPage();
            var profilesPage = pages.CreateProfilesPage();
            var applicationPage = pages.CreateApplicationPage();
            await Settle();
            Check(ReferenceEquals(positionPage, pages.CreatePositionPage()) && ReferenceEquals(profilesPage, pages.CreateProfilesPage())
                && ReferenceEquals(applicationPage, pages.CreateApplicationPage()), "Page instances are cached and preserve controls");
            Check(changed == 0, "Initial page construction does not mark preferences changed");
            Check(pages.Capture().Placement == initialPlacement, "Opening position page preserves null monitor and exact fractional position");
            var monitors = Named<ComboBox>(pages, "PlacementMonitorCombo");
            Check(monitors.Items.Count == 3 && monitors.SelectedIndex == 0, "Automatic primary display is a separate null choice");
            Check(Named<Slider>(pages, "PlacementXSlider").IsEnabled && Named<Slider>(pages, "PlacementYSlider").IsEnabled,
                "Custom position enables precise X and Y controls");
            monitors.SelectedIndex = 2;
            Check(pages.Capture().Placement.MonitorId == "display-b" && context.Placement.MonitorId is null, "Choosing another display changes only the draft");
            int beforeRefresh = changed;
            Click(Named<Button>(pages, "RefreshDisplaysButton"));
            Check(refreshes == 1 && pages.Capture().Placement.MonitorId == "display-b", "Explicit monitor refresh preserves an unavailable selected display");
            Check(changed == beforeRefresh && monitors.Items.Count == 3, "Monitor refresh does not silently replace the saved selection");
            Check(monitors.SelectedItem?.ToString()?.Contains("Недоступный") == true, "Unavailable monitor gets a readable fallback label");
            monitors.SelectedIndex = 0;
            Check(pages.Capture().Placement.MonitorId is null, "User may explicitly return to automatic primary selection");
            var x = Named<Slider>(pages, "PlacementXSlider"); var y = Named<Slider>(pages, "PlacementYSlider");
            Check(x.Value / 100 == initialPlacement.XFraction && y.Value / 100 == initialPlacement.YFraction, "Position sliders do not round initial fractions");
            x.Value = 50;
            Check(pages.Capture().Placement.XFraction == .5 && pages.Capture().Placement.YFraction == initialPlacement.YFraction,
                "Changing X leaves the untouched Y fraction exact");
            Click(Named<ToggleButton>(pages, "PlacementPreset_TopRight"));
            Check(pages.Capture().Placement.Placement == IslandPlacement.TopRight && !x.IsEnabled && !y.IsEnabled,
                "Anchor preset is drafted without movement and disables custom coordinates");
            Click(Named<Button>(pages, "CustomPlacementButton"));
            Check(pages.Capture().Placement.Placement == IslandPlacement.Custom && x.IsEnabled && pages.Capture().Placement.XFraction == .5,
                "Returning to free placement retains its coordinates");
            Named<CheckBox>(pages, "PlacementLockCheckBox").IsChecked = true;
            Check(pages.Capture().Placement.Locked && !context.Placement.Locked, "Position lock is isolated from the current HUD");

            var list = Named<ListBox>(pages, "SettingsProfileList");
            Check(list.SelectedIndex == 0 && Named<TextBox>(pages, "SettingsProfileNameBox").Text == "Первая игра", "Active profile is selected initially");
            Check(Named<TextBlock>(pages, "SettingsActiveProfileLabel").Text.Contains("Сейчас"), "Active profile is labelled");
            var profileName = Named<TextBox>(pages, "SettingsProfileNameBox");
            profileName.Text = "Новое имя"; await Settle();
            Check(pages.Capture().Profiles.Profiles[0].Name == "Новое имя", "Pending profile rename is captured without losing focus");
            Check(initialProfiles.Profiles[0].Name == "Первая игра", "Renaming never mutates context records or arrays");
            Check(!pages.TryAddGame(@"d:\GAMES\a\A.EXE", out var duplicate) && duplicate.Contains("уже") && pages.Capture().Profiles.Profiles.Length == 2,
                "Executable duplicates are detected case-insensitively");
            Check(!pages.TryAddGame("not-an-executable.txt", out _) && pages.Capture().Profiles.Profiles.Length == 2,
                "Invalid executable paths never enter the draft");
            Check(pages.TryAddGame(@"D:\Games\C\C.exe", out var addError) && addError.Length == 0, "A valid game path adds a local profile without executing a file");
            var added = pages.Capture().Profiles.Profiles.Single(profile => profile.ExecutablePath.EndsWith(@"C\C.exe"));
            Check(added.Name == "C" && Guid.TryParse(added.Id, out _), "New profile has a bounded filename label and stable identity");
            Check(added.Settings.Appearance.Layout == currentHud.Appearance.Layout && added.Settings.Components == currentHud.Components
                && !added.Settings.Appearance.ClickThrough, "Adding a profile copies the current draft HUD while excluding global click-through");
            Check(!pages.TryChangeGameExecutable(added.Id, @"D:\Games\B\B.exe", out _), "Changing executable rejects another profile's path");
            Check(pages.TryChangeGameExecutable(added.Id, @"D:\Games\C\Other.exe", out _), "Changing executable edits the selected local profile");
            Check(pages.Capture().Profiles.Profiles.Single(profile => profile.Id == added.Id).Name == "C", "Executable change preserves the chosen name");
            Check(pages.CopyCurrentHud(secondId), "Copy current HUD operates on an existing draft profile");
            Check(pages.Capture().Profiles.Profiles.Single(profile => profile.Id == secondId).Settings.Appearance.Alerts == currentHud.Appearance.Alerts,
                "Copied HUD includes independent warning configuration");
            Check(initialProfiles.Profiles[1].Settings == HudProfileSettings.Default, "Copying does not mutate the initial profile settings");
            Check(!pages.RemoveGame("missing-profile") && !pages.CopyCurrentHud("missing-profile"), "Unknown profile commands leave the draft intact");
            Check(pages.RemoveGame(firstId) && initialProfiles.Profiles.Length == 2, "Deleting an active profile only changes the local list");
            Check(Named<TextBlock>(pages, "ProfilesDraftNotice").Text.Contains("вернутся общие"), "Deleting active profile explains restoration to common settings");
            Named<CheckBox>(pages, "ProfilesAutoCheckBox").IsChecked = false;
            Check(!pages.Capture().Profiles.Enabled && initialProfiles.Enabled, "Automatic switching is a local draft switch");
            Check(pages.Validate() is null, "Valid profile changes pass outer-window validation");
            profileName = Named<TextBox>(pages, "SettingsProfileNameBox"); profileName.Text = "Bad\tName"; await Settle();
            Check(pages.Validate() is not null, "Profile control characters are rejected before saving");
            profileName.Text = "Нормальное имя"; await Settle(); Check(pages.Validate() is null, "Fixing a profile name clears validation");

            var startup = Named<CheckBox>(pages, "AppAutostartCheckBox");
            Check(startup.IsChecked == true && !pages.Capture().AutostartChanged, "Windows-disabled registration remains checked and untouched");
            Check(Named<TextBlock>(pages, "AutostartStatusText").Text.Contains("отключён"), "Windows startup decision is described separately");
            startup.IsChecked = false;
            Check(!pages.Capture().AutostartRegistered && pages.Capture().AutostartChanged, "Autostart opt-out is recorded as an explicit draft change");
            startup.IsChecked = true;
            Check(!pages.Capture().AutostartChanged, "Reverting autostart avoids unnecessary registry writes");
            Check(startupActions == 0 && sensorActions == 0, "Visiting application page does not launch Windows Settings or sensor setup");
            Click(Named<Button>(pages, "OpenWindowsStartupButton"));
            Check(startupActions == 1 && Named<TextBlock>(pages, "StartupActionStatus").Text.Contains("тестовая ошибка"), "External action errors stay inline");
            startupThrows = false; Click(Named<Button>(pages, "OpenWindowsStartupButton"));
            Check(startupActions == 2 && Named<TextBlock>(pages, "StartupActionStatus").Text.Length == 0, "External action can be retried successfully");
            Click(Named<Button>(pages, "OpenSensorsButton"));
            Check(sensorActions == 1 && Named<TextBlock>(pages, "SensorActionStatus").Text.Contains("тестовая ошибка"), "Sensor setup is called only by its explicit button and handles failure");
            Check(Named<TextBlock>(pages, "SettingsVersionText").Text.StartsWith("Luma Island "), "Application section exposes its actual version");

            var missingContext = context with { Placement = initialPlacement with { MonitorId = "disconnected-screen" },
                Autostart = new AutostartState(true, false, "Ошибка чтения"), OpenSensors = null, OpenWindowsStartup = null, RefreshDisplays = null };
            var unavailable = new SettingsApplicationPages(missingContext, () => currentHud, () => { }, () => owner);
            unavailable.CreatePositionPage(); unavailable.CreateApplicationPage();
            Check(unavailable.Capture().Placement.MonitorId == "disconnected-screen", "Disconnected monitor ID is retained when opening and saving without changes");
            Check(!Named<Button>(unavailable, "RefreshDisplaysButton").IsEnabled && !Named<Button>(unavailable, "OpenSensorsButton").IsEnabled
                && !Named<Button>(unavailable, "OpenWindowsStartupButton").IsEnabled, "Unavailable external capabilities have disabled actions");
            Check(!Named<CheckBox>(unavailable, "AppAutostartCheckBox").IsEnabled && !unavailable.Capture().AutostartChanged,
                "Uncertain startup state is read-only and cannot be silently rewritten");
            var fullProfiles = Enumerable.Range(0, GameProfileStore.MaxProfiles).Select(index => new GameProfile(
                Guid.NewGuid().ToString("D"), "Игра " + index, @"D:\Games\G" + index + @"\game.exe", HudProfileSettings.Default)).ToArray();
            var full = new SettingsApplicationPages(context with { Profiles = new(true, fullProfiles) }, () => currentHud, () => { }, () => owner);
            Check(!full.TryAddGame(@"D:\Games\Extra\extra.exe", out var limitError) && limitError.Contains("64")
                && full.Capture().Profiles.Profiles.Length == GameProfileStore.MaxProfiles, "Profile limit rejects extra games without truncating existing entries");
            Check(full.RemoveGame(fullProfiles[0].Id) && full.TryAddGame(@"D:\Games\Extra\extra.exe", out _),
                "Removing a draft profile frees capacity for a replacement");
            var emptyPages = new SettingsApplicationPages(context with { ActiveProfileId = null, Profiles = GameProfileSettings.Default },
                () => currentHud, () => { }, () => owner);
            emptyPages.CreateProfilesPage();
            Check(Named<TextBlock>(emptyPages, "SettingsActiveProfileLabel").Text.Length == 0 && !Named<Button>(emptyPages, "RemoveProfileButton").IsEnabled,
                "Empty profile list has no misleading active badge or enabled deletion");
            Check(changed > 0 && CaptureEqual(context, initialProfiles, initialPlacement), "User changes notify the shell while the original context remains intact");
        }
        finally { owner.Close(); }
        Check(before.All(pair => Preferences()[pair.Key] == pair.Value), "All actual user preference files remain byte-identical");
    }
    private static bool CaptureEqual(SettingsEditorContext context, GameProfileSettings profiles, PlacementSettings placement)
        => ReferenceEquals(context.Profiles, profiles) && context.Placement == placement && profiles.Profiles[0].Name == "Первая игра" && profiles.Profiles[1].Settings == HudProfileSettings.Default;
    private static T Named<T>(SettingsApplicationPages pages, string name) where T : FrameworkElement
        => pages.FindControl(name) as T ?? throw new InvalidOperationException("Missing control: " + name);
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    private static string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    private static Dictionary<string, string> Preferences() => new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }
        .ToDictionary(name => name, name => Hash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland", name)));
    private static async Task Settle() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(20); }
    private static void Check(bool passed, string description) { if (!passed) throw new InvalidOperationException(description); Checks.Add(description); }
}
