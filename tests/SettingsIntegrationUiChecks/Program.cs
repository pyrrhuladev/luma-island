using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Luma.Island;
using Luma.Island.Services;
using Luma.Island.Settings;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static string Output = null!;
    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : "work/checks/SettingsIntegrationUiChecks");
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
                    assemblySha256 = Hash(typeof(MainWindow).Assembly.Location),
                    isolation = "Fixture stores and private HKCU test keys only. No App startup, collectors, SDK, media initialization, global input, sensor setup or Windows Settings launch. Native HUD remains transparent and nonactivating."
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} unified settings integration UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        var realBefore = RealPreferences();
        string fixture = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        string registryRoot = @"Software\LumaIsland.Tests\SettingsIntegration-" + Guid.NewGuid().ToString("N");
        string runKey = registryRoot + @"\Run", approvalKey = registryRoot + @"\Approval";
        byte[] disabledApproval = { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        string fakeExe = Path.Combine(fixture, "Luma.Island.exe"); File.WriteAllText(fakeExe, "Fixture only; never execute.");
        string originalCommand = '"' + fakeExe + "\" --startup";
        using (var run = Registry.CurrentUser.CreateSubKey(runKey)) run.SetValue(AutostartService.ValueName, originalCommand);
        using (var approval = Registry.CurrentUser.CreateSubKey(approvalKey)) approval.SetValue(AutostartService.ValueName, disabledApproval, RegistryValueKind.Binary);
        var autostart = new AutostartService(fakeExe, runKey, approvalKey);
        var appearance = new AppearanceStore(Path.Combine(fixture, "appearance.json"));
        var components = new ComponentStore(Path.Combine(fixture, "components.json"));
        var placement = new PlacementStore(Path.Combine(fixture, "placement.json"));
        var profiles = new GameProfileStore(Path.Combine(fixture, "profiles.json"));
        var empty = new ComponentSettings(false, false, false, false, false);
        var basePlacement = PlacementSettings.Default with { XFraction = .123456789123, YFraction = .876543219876 };
        Check(appearance.Save(AppearanceSettings.Default with { ClickThrough = true }) && components.Save(empty) && placement.Save(basePlacement), "Only fixture preference stores are initialized");
        var controller = new GameProfileController(appearance, components, placement, profiles);
        string firstId = Guid.NewGuid().ToString("D"), secondId = Guid.NewGuid().ToString("D");
        Check(controller.ReplaceSettings(new(true, new[]
        {
            new GameProfile(firstId, "Первая игра", @"D:\Games\First\first.exe", HudProfileSettings.Default),
            new GameProfile(secondId, "Вторая игра", @"D:\Games\Second\second.exe", HudProfileSettings.Default)
        })), "Two fixture game profiles are configured");
        using var media = new MediaService(); using var system = new SystemService(); system.Dispose();
        var window = new MainWindow(media, system, controller.PlacementStore, controller.AppearanceStore,
            controller.ComponentStore, autostart, controller) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        try
        {
            new WindowInteropHelper(window).EnsureHandle(); window.Show(); await Settle();
            Check(window.ActivateGameProfile(firstId), "First game becomes the editor target");
            string[] baseHashes = BaseHashes(fixture);
            var draftAppearance = AppearanceSettings.Default with { GlassOpacity = .17,
                Layout = new("music,header,metrics", "memory,cpu,gpu", 2, 1.1, 9), Alerts = new(Enabled: true, Stutters: false) };
            var draftComponents = empty with { Cpu = true, Gpu = true, Fps = true };
            var draftPlacement = window.Placement.Settings with { Placement = IslandPlacement.Custom, XFraction = .22, YFraction = .11, Locked = true };
            var draftProfiles = controller.Settings with { Profiles = controller.Settings.Profiles.Select(p => p.Id == secondId ? p with { Name = "Вторая игра переименована" } : p).ToArray() };
            int sourceChanges = 0; window.ComponentsChanged += _ => sourceChanges++;
            Check(window.ApplySettingsDraft(draftAppearance, draftComponents, new(draftPlacement, draftProfiles, true, false), firstId), "Unified Save applies HUD and profile changes to the same active target");
            Check(sourceChanges == 1 && window.ActiveGameProfileId == firstId, "Unified Save reconfigures existing collectors once and retains target");
            Check(baseHashes.SequenceEqual(BaseHashes(fixture)), "Saving active game leaves all ordinary preference files byte-identical");
            var savedProfiles = profiles.Load(); var savedFirst = savedProfiles.Profiles.Single(p => p.Id == firstId);
            Check(savedFirst.Settings.Appearance.Layout == draftAppearance.Layout && savedFirst.Settings.Components == draftComponents, "Current HUD draft is merged rather than overwritten by older profile settings");
            Check(savedFirst.Settings.Placement == draftPlacement, "Profile position and lock are saved with the HUD");
            Check(savedProfiles.Profiles.Single(p => p.Id == secondId).Name == "Вторая игра переименована", "Other profile edits survive the current HUD merge");
            Check(window.Appearance.ClickThrough && !savedFirst.Settings.Appearance.ClickThrough, "Global input preference survives while profile snapshot excludes it");
            using (var run = Registry.CurrentUser.OpenSubKey(runKey)) Check(Equals(run?.GetValue(AutostartService.ValueName), originalCommand), "Unchanged autostart registration is not rewritten despite Windows disabling it");
            Check(ApprovalUnchanged(approvalKey, disabledApproval), "Unified Save preserves Windows StartupApproved bytes");

            var promoted = draftAppearance with { GlassOpacity = .23 };
            var disabledProfiles = controller.Settings with { Enabled = false };
            Check(window.ApplySettingsDraft(promoted, draftComponents, new(draftPlacement, disabledProfiles, true, false), firstId), "Disabling automatic profiles saves the edited current HUD as global");
            Check(window.ActiveGameProfileId is null && !window.GameProfiles.Enabled, "Automatic profiles are disabled and route returns to ordinary settings");
            Check(appearance.Load().GlassOpacity == .23 && appearance.Load().Layout == promoted.Layout && components.Load() == draftComponents, "Edited appearance and components become ordinary persisted preferences");
            Check(placement.Load() == draftPlacement, "Edited position becomes ordinary persisted placement");
            Check(profiles.Load().Profiles.Single(p => p.Id == firstId).Settings.Appearance.GlassOpacity == .23, "Disabling automation also retains the latest game snapshot");

            Check(window.SetGameProfiles(controller.Settings with { Enabled = true }) && window.ActivateGameProfile(firstId), "Game profile can be enabled and activated again");
            string[] beforeDelete = BaseHashes(fixture);
            var removal = controller.Settings with { Profiles = controller.Settings.Profiles.Where(p => p.Id != firstId).ToArray() };
            var discardedGame = promoted with { GlassOpacity = .99, Layout = new(MetricColumns: 1) };
            Check(window.ApplySettingsDraft(discardedGame, empty, new(draftPlacement with { Placement = IslandPlacement.BottomLeft }, removal, true, false), firstId), "Deleting active profile applies an explicit base-HUD return plan");
            Check(window.ActiveGameProfileId is null && beforeDelete.SequenceEqual(BaseHashes(fixture)), "Deleted profile draft cannot overwrite ordinary appearance, components or position");
            Check(window.Appearance.GlassOpacity == .23 && window.Appearance.Layout == promoted.Layout && window.Components == draftComponents,
                "Visible HUD returns to the saved base rather than the deleted game's draft");
            Check(profiles.Load().Profiles.Length == 1 && profiles.Load().Profiles[0].Id == secondId, "Other game profile persists after deletion");

            Check(window.ApplySettingsDraft(window.Appearance, window.Components, new(window.Placement.Settings, controller.Settings, false, true), null), "Explicit autostart change is committed only on unified Save");
            using (var run = Registry.CurrentUser.OpenSubKey(runKey)) Check(run?.GetValue(AutostartService.ValueName) is null, "Explicitly disabling autostart removes only its test registration");
            Check(ApprovalUnchanged(approvalKey, disabledApproval), "Explicit registration change still preserves the Windows decision");
            Check(window.Appearance.ClickThrough, "All save paths keep the global input preference");

            var menu = ((Border)window.FindName("IslandSurface")).ContextMenu;
            Check(menu.Items.OfType<MenuItem>().Count() == 5 && menu.Items.Count == 6 && menu.Items[4] is Separator,
                "Actual HUD quick menu contains five actions and one separator");
            Check(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Настройки…")), "Unified settings are discoverable from the actual quick menu");
        }
        finally
        {
            window.Close();
            Registry.CurrentUser.DeleteSubKeyTree(registryRoot, throwOnMissingSubKey: false);
        }
        Check(RealPreferences().All(pair => realBefore.TryGetValue(pair.Key, out string? value) && value == pair.Value), "Actual user preference files remain byte-identical");
    }
    private static bool ApprovalUnchanged(string path, byte[] expected) { using var key = Registry.CurrentUser.OpenSubKey(path); return key?.GetValue(AutostartService.ValueName) is byte[] bytes && bytes.SequenceEqual(expected); }
    private static string[] BaseHashes(string root) => new[] { "appearance.json", "components.json", "placement.json" }.Select(name => Hash(Path.Combine(root, name))).ToArray();
    private static string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    private static Dictionary<string, string> RealPreferences() => new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }.ToDictionary(name => name,
        name => Hash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland", name)));
    private static async Task Settle() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(40); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Checks.Add(message); }
}
