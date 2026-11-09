using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static readonly ComponentSettings Empty = new(false, false, false, false, false);
    private static string Output = null!;
    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : "work/checks/HudEditorIntegrationUiChecks");
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
                    isolation = "Isolated fixture stores; no App startup, collectors, SDK, SMTC initialization, global input, driver or service changes. Production preview is never shown and owns no HWND."
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} HUD editor integration UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        var userBefore = RealPreferences();
        var components = ComponentSettings.Default with { Cpu = true, Gpu = true, Memory = true, Fps = true,
            OnePercentLow = true, FrameTime = true, CpuTemperature = true, GpuTemperature = true, MemoryTemperature = true };
        int windowsBefore = Application.Current.Windows.Count;
        var normal = MainWindow.RenderHudPreview(AppearanceSettings.Default, components);
        Check(normal.IsFrozen && normal.PixelWidth >= 300 && normal.PixelHeight > 100, "Actual production preview has frozen HUD-sized pixels");
        Check(VisiblePixels(normal) > 1000, "Never-shown production visual preview contains visible text and surface pixels");
        Check(Application.Current.Windows.Count == windowsBefore, "Preview releases its never-shown Window after rendering");
        var customAppearance = AppearanceSettings.Default with { Scale = 1.5,
            Layout = new("music,metrics,header", "memory,gpu,cpu", 1, 1.3, 12),
            Alerts = AlertSettings.Default with { Enabled = true } };
        var custom = MainWindow.RenderHudPreview(customAppearance, components);
        Check(custom.PixelHeight > normal.PixelHeight && VisiblePixels(custom) > 1000, "Custom rows, font, spacing and scale produce a real enlarged preview");
        Save(custom, "editor-preview-custom.png"); Save(normal, "editor-preview-default.png");
        for (int index = 0; index < 8; index++) MainWindow.RenderHudPreview(index % 2 == 0 ? customAppearance : AppearanceSettings.Default, components);
        Check(Application.Current.Windows.Count == windowsBefore, "Repeated live previews leave no hidden Window instances");
        var empty = MainWindow.RenderHudPreview(AppearanceSettings.Default, Empty);
        Check(empty.PixelWidth > 0 && empty.PixelHeight > 0 && VisiblePixels(empty) > 0, "Empty component selection still renders a valid capsule preview");
        var malformed = MainWindow.RenderHudPreview(AppearanceSettings.Default with { Layout = null!, Alerts = null! }, Empty);
        Check(malformed.IsFrozen, "Preview normalizes missing nested settings");

        var off = MonitorRequirements.Resolve(Empty, AlertSettings.Default, true);
        Check(off == new MonitorRequirements(false, false, false, false, false, false, false, false, false), "Default disabled alerts start no collectors");
        var cpuAlert = MonitorRequirements.Resolve(Empty, new AlertSettings(Enabled: true, GpuTemperature: false, Stutters: false), true);
        Check(cpuAlert.CpuTemperature && !cpuAlert.Cpu && !cpuAlert.GpuTemperature && !cpuAlert.FrameTime, "CPU warning reads only temperature even when its numeric component is hidden");
        var ramAlert = MonitorRequirements.Resolve(Empty, new AlertSettings(Enabled: true, CpuTemperature: false, GpuTemperature: false, MemoryTemperature: true, Stutters: false), true);
        Check(ramAlert.MemoryTemperature && !ramAlert.Memory && !ramAlert.CpuTemperature, "RAM warning independently requests its hidden sensor");
        var gpuAlert = MonitorRequirements.Resolve(Empty, new AlertSettings(Enabled: true, CpuTemperature: false, Stutters: false), true);
        Check(gpuAlert.GpuTemperature && !gpuAlert.Gpu && !gpuAlert.CpuTemperature && !gpuAlert.FrameTime, "GPU warning shares its existing sensor without a utilization sampler");
        var frameAlert = MonitorRequirements.Resolve(Empty, new AlertSettings(Enabled: true, CpuTemperature: false, GpuTemperature: false), true);
        Check(frameAlert.FrameTime && !frameAlert.Fps && !frameAlert.OnePercentLow && !frameAlert.GpuTemperature, "Stutter warning requests real frames without enabling visible FPS or Low");
        var hidden = MonitorRequirements.Resolve(components, new AlertSettings(Enabled: true, MemoryTemperature: true), false);
        Check(hidden == off, "Hiding in the tray stops alert and visual collector requirements together");
        var disabled = MonitorRequirements.Resolve(Empty, new AlertSettings(Enabled: false, MemoryTemperature: true), true);
        Check(disabled == off, "Master warning switch disables every warning-specific source");
        Check(MonitorRequirements.Resolve(components, AlertSettings.Default, true).FrameTime, "Turning off warnings preserves separately enabled frame graph");

        string fixture = Path.Combine(Output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var appearanceStore = new AppearanceStore(Path.Combine(fixture, "appearance.json"));
        var componentStore = new ComponentStore(Path.Combine(fixture, "components.json"));
        var placementStore = new PlacementStore(Path.Combine(fixture, "placement.json"));
        var profileStore = new GameProfileStore(Path.Combine(fixture, "profiles.json"));
        Check(appearanceStore.Save(AppearanceSettings.Default with { ClickThrough = true }) && componentStore.Save(Empty) && placementStore.Save(PlacementSettings.Default), "Only fixture stores are initialized");
        var profiles = new GameProfileController(appearanceStore, componentStore, placementStore, profileStore);
        string gameId = Guid.NewGuid().ToString("D");
        Check(profiles.ReplaceSettings(new(true, new[] { new GameProfile(gameId, "Тестовая игра", @"D:\Games\Example\Example.exe", HudProfileSettings.Default) })), "Fixture game profile is configured without launching a game");
        using var media = new MediaService(); using var system = new SystemService(); system.Dispose();
        var window = new MainWindow(media, system, profiles.PlacementStore, profiles.AppearanceStore,
            profiles.ComponentStore, new AutostartService(readOnly: true), profiles)
            { Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Topmost = false };
        try
        {
            new WindowInteropHelper(window).EnsureHandle(); window.Show(); await Settle();
            Check(window.ActivateGameProfile(gameId), "Fixture game profile is activated on the real HUD");
            string baseAppearanceHash = Hash(Path.Combine(fixture, "appearance.json"));
            string baseComponentHash = Hash(Path.Combine(fixture, "components.json"));
            int configurationEvents = 0;
            window.ComponentsChanged += _ => configurationEvents++;
            Check(window.ApplyHudEditorSettings(customAppearance with { ClickThrough = false }, components), "Editor saves layout, alerts and component selection through existing routing");
            Check(window.Appearance.ClickThrough, "Editor preserves global click-through preference");
            Check(configurationEvents == 1, "Editor commit reconfigures existing collectors once");
            Check(Hash(Path.Combine(fixture, "appearance.json")) == baseAppearanceHash && Hash(Path.Combine(fixture, "components.json")) == baseComponentHash,
                "Active game editor changes leave ordinary appearance and component files byte-identical");
            var savedGame = profileStore.Load().Profiles.Single();
            Check(savedGame.Settings.Appearance.Layout == customAppearance.Layout && savedGame.Settings.Appearance.Alerts == customAppearance.Alerts,
                "Game profile persists custom layout and independent warning thresholds");
            Check(savedGame.Settings.Components == components, "Game profile persists editor component switches");
            Check(!savedGame.Settings.Appearance.ClickThrough, "Profile snapshot excludes the global input preference");
            window.ApplyAlertPerformance(new PerformanceSnapshot(null, null, null, 0, 0, CpuTemperatureCelsius: 98), 0);
            window.ApplyAlertPerformance(new PerformanceSnapshot(null, null, null, 0, 0, CpuTemperatureCelsius: 98), 4);
            Check(window.AlertView.Active, "Active profile warning appears from its configured sensor");
            Check(window.ActivateGameProfile(null), "Game exit restores the ordinary HUD");
            Check(window.Appearance.Layout == HudLayoutSettings.Default && !window.Appearance.Alerts.Enabled && !window.AlertView.Active,
                "Returning to base clears game-specific layout and warning state");
            Check(window.Appearance.ClickThrough, "Profile switch preserves the global input preference");
            var baseEdit = AppearanceSettings.Default with { Layout = new(MetricColumns: 2), Alerts = new(Enabled: true, CpuTemperature: false, Stutters: false) };
            Check(window.ApplyHudEditorSettings(baseEdit, Empty), "Global editor settings save through ordinary stores");
            Check(appearanceStore.Load().Layout.MetricColumns == 2 && appearanceStore.Load().Alerts.NeedsGpuTemperature && componentStore.Load() == Empty,
                "Global HUD retains layout and alert configuration after store reload");
            Check(profileStore.Load().Profiles.Single().Settings.Appearance.Layout == customAppearance.Layout,
                "Editing the ordinary HUD leaves the saved game layout intact");
        }
        finally { window.Close(); }
        Check(SamePreferences(userBefore, RealPreferences()), "All actual user preference files remain byte-identical");
    }

    private static int VisiblePixels(BitmapSource image)
    {
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        int count = 0; for (int offset = 3; offset < pixels.Length; offset += 4) if (pixels[offset] != 0) count++;
        return count;
    }
    private static void Save(BitmapSource bitmap, string name) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(Output, name)); encoder.Save(stream); }
    private static string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    private static Dictionary<string, string> RealPreferences() => new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }
        .ToDictionary(name => name, name => Hash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland", name)));
    private static bool SamePreferences(Dictionary<string, string> a, Dictionary<string, string> b) => a.All(pair => b.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);
    private static async Task Settle() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(40); }
    private static void Check(bool passed, string description) { if (!passed) throw new InvalidOperationException(description); Checks.Add(description); }
}
