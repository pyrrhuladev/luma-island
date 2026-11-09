using System.Security.Cryptography;
using System.Text.Json;
using Luma.Island.Services;
using Luma.Island.Settings;

int checks = 0;
void Check(bool passed, string description)
{
    if (!passed) throw new InvalidOperationException(description);
    checks++;
}
var originalBase = new HudProfileSettings(
    AppearanceSettings.Default with { ClickThrough = true },
    ComponentSettings.Default,
    PlacementSettings.Default with { MonitorId = null, XFraction = .5187165775401069 });
var edited = new HudProfileSettings(
    new AppearanceSettings(.173456, ClickThrough: true, Scale: 1.12345)
    {
        Layout = new("music,header,metrics", "memory,gpu,cpu", 2, 1.11234, 9),
        Alerts = new(Enabled: true, CpuThreshold: 94, MemoryTemperature: true)
    },
    ComponentSettings.Default with { Cpu = true, Gpu = true, Memory = true, Fps = true, FrameTime = true },
    new PlacementSettings("SECOND", IslandPlacement.Custom, .6123456789, .2345678912, true));
string activeId = "f3d949e7-2ae3-40a9-81c1-81d9224899f3";
string secondId = "1fe53864-a7fe-4fc1-9e95-f1905a3df16f";
var activeDraft = new GameProfile(activeId, "Renamed active", @"D:\Games\Alpha\Alpha.exe", HudProfileSettings.Default);
var secondDraft = new GameProfile(secondId, "Other edited profile", @"D:\Games\Beta\Beta.exe",
    new HudProfileSettings(AppearanceSettings.Default with { GlassOpacity = .61 },
        ComponentSettings.Default with { Memory = true }, PlacementSettings.Default with { Placement = IslandPlacement.BottomRight }));
var draft = new GameProfileSettings(true, new[] { activeDraft, secondDraft });
string inputBefore = JsonSerializer.Serialize(new { originalBase, edited, draft });

foreach (bool automatic in new[] { false, true })
{
    var global = SettingsCommitPlan.Create(originalBase, null, edited, draft with { Enabled = automatic });
    Check(global.ActiveProfileId is null && global.SaveHudAsGlobal, "Ordinary editing stays global regardless of automatic selection");
    Check(global.VisibleHud == edited, "Ordinary settings use the complete edited HUD");
    Check(global.Profiles.Enabled == automatic && global.Profiles.Profiles.Length == 2, "Profile configuration is retained during global editing");
    Check(global.Profiles.Profiles[0].Settings == GameProfileStore.NormalizeSnapshot(activeDraft.Settings), "Ordinary editing does not overwrite a configured game HUD");
}

var active = SettingsCommitPlan.Create(originalBase, activeId, edited, draft);
Check(active.ActiveProfileId == activeId && !active.SaveHudAsGlobal, "Automatic active-game edits target the selected game only");
Check(active.VisibleHud == edited, "Active game keeps the edited visible layout, components and placement");
Check(active.Profiles.Profiles[0].Settings == GameProfileStore.NormalizeSnapshot(edited), "Final HUD is merged into the active draft before persistence");
Check(active.Profiles.Profiles[0].Name == activeDraft.Name && active.Profiles.Profiles[0].ExecutablePath == activeDraft.ExecutablePath,
    "Active game rename and executable selection survive the HUD merge");
Check(active.Profiles.Profiles[1].Settings == GameProfileStore.NormalizeSnapshot(secondDraft.Settings), "Other profile edits are preserved without receiving the active HUD");
Check(active.VisibleHud.Appearance.ClickThrough && !active.Profiles.Profiles[0].Settings.Appearance.ClickThrough,
    "Global input preference is visible but never stored in the game snapshot");
Check(active.Profiles.Profiles[0].Settings.Placement == edited.Placement, "Edited game placement is merged with appearance and components");
Check(active.Profiles.Profiles[0].Settings.Appearance.Layout == edited.Appearance.Layout &&
      active.Profiles.Profiles[0].Settings.Appearance.Alerts == edited.Appearance.Alerts,
    "New layout and warning settings survive the merge");

var disabled = SettingsCommitPlan.Create(originalBase, activeId, edited, draft with { Enabled = false });
Check(disabled.ActiveProfileId is null && disabled.SaveHudAsGlobal, "Disabling automatic selection promotes the final HUD to ordinary settings");
Check(disabled.VisibleHud == edited, "Automatic-off keeps the edited view rather than the old base");
Check(disabled.Profiles.Profiles[0].Settings == GameProfileStore.NormalizeSnapshot(edited), "Automatic-off also retains the selected game's final snapshot");
Check(!disabled.Profiles.Enabled && disabled.Profiles.Profiles[1].Name == secondDraft.Name, "Automatic-off preserves all configured bindings and their edits");

foreach (bool automatic in new[] { false, true })
{
    var removed = SettingsCommitPlan.Create(originalBase, activeId, edited, new(automatic, new[] { secondDraft }));
    Check(removed.ActiveProfileId is null && !removed.SaveHudAsGlobal, "Deleting the active game never captures its draft into base stores");
    Check(removed.VisibleHud == originalBase, "Deleting the active game restores the entry-time base snapshot");
    Check(removed.Profiles.Profiles.Length == 1 && removed.Profiles.Profiles[0].Id == secondId, "Deleting one game retains other draft changes");
    Check(removed.VisibleHud.Placement.MonitorId is null && removed.VisibleHud.Placement.XFraction == originalBase.Placement.XFraction,
        "Restored base retains automatic monitor choice and exact custom fractions");
}
foreach (string unknown in new[] { "not-a-guid", "", Guid.Empty.ToString("D"), Guid.NewGuid().ToString("D") })
{
    var removed = SettingsCommitPlan.Create(originalBase, unknown, edited, draft);
    Check(removed.ActiveProfileId is null && !removed.SaveHudAsGlobal && removed.VisibleHud == originalBase,
        "Unknown entry-time active identity uses deletion semantics");
}
var canonical = SettingsCommitPlan.Create(originalBase, "{" + activeId.ToUpperInvariant() + "}", edited, draft);
Check(canonical.ActiveProfileId == activeId && canonical.Profiles.Profiles[0].Settings == GameProfileStore.NormalizeSnapshot(edited),
    "Equivalent GUID formats resolve the same active profile");

var malformedHud = new HudProfileSettings(new AppearanceSettings(double.NaN, Scale: double.PositiveInfinity)
    { Layout = null!, Alerts = new(CpuThreshold: double.NaN, FrameTimeThreshold: -1, StutterCount: -4) },
    null!, new PlacementSettings("  ", (IslandPlacement)99, double.NaN, 8));
var finite = SettingsCommitPlan.Create(originalBase, null, malformedHud, new(true, null!));
Check(finite.VisibleHud.Appearance.GlassOpacity == .4 && finite.VisibleHud.Appearance.Scale == 1,
    "Invalid appearance values are normalized before application");
Check(finite.VisibleHud.Appearance.Layout == HudLayoutSettings.Default && finite.VisibleHud.Appearance.Alerts.CpuThreshold == 90 &&
      finite.VisibleHud.Appearance.Alerts.FrameTimeThreshold == 10 && finite.VisibleHud.Appearance.Alerts.StutterCount == 2,
    "Nested layout and warning limits remain finite and bounded");
Check(finite.VisibleHud.Components == ComponentSettings.Default, "Missing components migrate to defaults");
Check(finite.VisibleHud.Placement == PlacementSettings.Default with { YFraction = 1 }, "Malformed placement is finite, clamped and valid");
Check(finite.Profiles.Profiles.Length == 0 && finite.Profiles.Enabled, "Missing profile array preserves automatic flag without invalid entries");
var invalidProfiles = new GameProfileSettings(true, new[]
{
    activeDraft with { ExecutablePath = @"D:\Games\Alpha\not-an-executable.txt" },
    secondDraft,
    secondDraft with { Name = "duplicate ID" },
    secondDraft with { Id = Guid.NewGuid().ToString("D"), Name = "duplicate executable" },
    secondDraft with { Id = "bad ID", ExecutablePath = @"D:\Games\Other.exe" }
});
var discarded = SettingsCommitPlan.Create(originalBase, activeId, edited, invalidProfiles);
Check(discarded.Profiles.Profiles.Length == 1 && discarded.Profiles.Profiles[0].Id == secondId, "Invalid and duplicate profile definitions are removed");
Check(!discarded.SaveHudAsGlobal && discarded.VisibleHud == originalBase, "An invalid active draft is handled as a removed binding");

Check(JsonSerializer.Serialize(new { originalBase, edited, draft }) == inputBefore, "Commit planning leaves every input value unchanged");
Check(!ReferenceEquals(active.VisibleHud, edited) && !ReferenceEquals(active.VisibleHud.Appearance, edited.Appearance) &&
      !ReferenceEquals(active.VisibleHud.Components, edited.Components) && !ReferenceEquals(active.VisibleHud.Placement, edited.Placement),
    "Visible snapshot is defensively cloned");
Check(!ReferenceEquals(active.Profiles.Profiles, draft.Profiles) && !ReferenceEquals(active.Profiles.Profiles[1], secondDraft) &&
      !ReferenceEquals(active.Profiles.Profiles[1].Settings, secondDraft.Settings), "Profile arrays and snapshot wrappers do not alias caller state");
var originalSecond = active.Profiles.Profiles[1];
draft.Profiles[1] = secondDraft with { Name = "caller replaced array entry" };
Check(active.Profiles.Profiles[1] == originalSecond, "Replacing an input array entry cannot change the saved plan");
active.Profiles.Profiles[1] = originalSecond with { Name = "plan replaced array entry" };
Check(draft.Profiles[1].Name == "caller replaced array entry", "Replacing a plan array entry cannot mutate its input");
var emptyProfiles = new GameProfileSettings(false, Array.Empty<GameProfile>());
var emptyPlan = SettingsCommitPlan.Create(originalBase, null, edited, emptyProfiles);
Check(!ReferenceEquals(emptyPlan.Profiles.Profiles, emptyProfiles.Profiles), "Even an empty caller array is not retained");
Check(AppearanceSettings.Default == new AppearanceSettings() && PlacementSettings.Default == new PlacementSettings(), "Shared defaults are never mutated");

// A plan must remain pure even when a caller's profile file cannot be opened for read/write.
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "work/checks/SettingsCommitChecks");
Directory.CreateDirectory(output);
string sentinel = Path.Combine(output, "locked-profile-sentinel.json");
File.WriteAllText(sentinel, "{\"sentinel\":\"do not alter\"}");
byte[] before = SHA256.HashData(File.ReadAllBytes(sentinel));
using (var locked = new FileStream(sentinel, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    var pure = SettingsCommitPlan.Create(originalBase, activeId, edited, new(true, new[] { activeDraft, secondDraft }));
    Check(pure.ActiveProfileId == activeId, "Planning needs no store access while profile files are locked");
}
Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(sentinel))), "Profile file bytes remain untouched by planning");
File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
{
    passed = true, checks, isolation = "Pure commit-plan functions and linked normalizers; no native UI, registry, collectors, SDK, user preferences or application startup. Only an isolated locked sentinel file is used."
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks} settings commit policy checks.");

namespace Luma.Island { internal static class App { internal static void Log(Exception error) { } } }
