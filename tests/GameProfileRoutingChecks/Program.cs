using System.Security.Cryptography;
using System.Text.Json;
using Luma.Island.Services;

int checks = 0;
var completed = new List<string>();
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    checks++; completed.Add(label);
}
string output = Path.GetFullPath(args.Length == 0 ? "work/game-profile-routing-checks" : args[0]);
Directory.CreateDirectory(output);
string directory = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string appearancePath = Path.Combine(directory, "appearance.json"), componentPath = Path.Combine(directory, "components.json"),
    placementPath = Path.Combine(directory, "placement.json"), profilePath = Path.Combine(directory, "profiles.json");
string[] basePaths = { appearancePath, componentPath, placementPath };
Dictionary<string, string?> RealPreferences()
{
    string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
    return new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }.ToDictionary(name => name,
        name => File.Exists(Path.Combine(real, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(real, name)))) : null);
}
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
string[] BaseHashes() => basePaths.Select(Hash).ToArray();
var realPreferences = RealPreferences();
var appearance = new CountedAppearance(appearancePath);
var components = new CountedComponents(componentPath);
var placement = new CountedPlacement(placementPath);
var profiles = new GameProfileStore(profilePath);
var originalBase = new HudProfileSettings(new AppearanceSettings(IslandAppearance.Matte, .76, false, true, 1.1),
    ComponentSettings.Default with { Battery = false, Cpu = true },
    new PlacementSettings(null, IslandPlacement.TopCenter));
Check(appearance.Save(originalBase.Appearance) && components.Save(originalBase.Components) && placement.Save(originalBase.Placement), "Fixture base files saved");
var a = new GameProfile(Guid.NewGuid().ToString("D"), "Game A", @"C:\Games\Alpha\alpha.exe",
    new HudProfileSettings(new AppearanceSettings(IslandAppearance.Glass, .25, true, true, .8),
        ComponentSettings.Default with { Fps = true, Gpu = true, Date = false },
        new PlacementSettings(null, IslandPlacement.BottomLeft)));
var b = new GameProfile(Guid.NewGuid().ToString("D"), "Game B", @"C:\Games\Beta\beta.exe",
    new HudProfileSettings(new AppearanceSettings(IslandAppearance.Glass, .54, false, false, 1.4),
        ComponentSettings.Default with { Music = false, Memory = true, Charts = true },
        new PlacementSettings(null, IslandPlacement.BottomRight)));
Check(profiles.Save(new GameProfileSettings(true, new[] { a, b })), "Two isolated game bindings saved");
appearance.Writes = components.Writes = placement.Writes = 0;
var controller = new GameProfileController(appearance, components, placement, profiles);
Check(controller.ActiveProfileId is null && controller.CurrentSettings == originalBase, "Controller starts in the existing base context without selecting a game");
Check(appearance.Writes + components.Writes + placement.Writes == 0, "Constructing routed stores does not rewrite ordinary preference files");
Check(controller.Settings.Profiles.All(profile => !profile.Settings.Appearance.ClickThrough), "Game snapshots never own the global input recovery mode");
var baseHashes = BaseHashes();
var selectedA = controller.Activate(a.Id);
Check(selectedA.Appearance.Scale == .8 && selectedA.Appearance.Compact && selectedA.Appearance.ClickThrough,
    "Game A loads its own scale/density while keeping the global input mode");
Check(controller.AppearanceStore.Load() == selectedA.Appearance && controller.ComponentStore.Load() == selectedA.Components &&
    controller.PlacementStore.Load() == selectedA.Placement, "All three routed loads agree on the active game snapshot");
var bBefore = controller.Settings.Profiles.Single(profile => profile.Id == b.Id);
var aAppearance = selectedA.Appearance with { GlassOpacity = .05, Scale = 1.5 };
var aComponents = selectedA.Components with { CpuTemperature = true, Memory = true };
var aPlacement = new PlacementSettings(null, IslandPlacement.Custom, .31, .68, true);
Check(controller.AppearanceStore.Save(aAppearance), "An active game saves its appearance through the existing appearance store API");
Check(controller.ComponentStore.Save(aComponents), "An active game saves its component selection through the existing store API");
Check(controller.PlacementStore.Save(aPlacement), "An active game saves its changed position through the existing store API");
Check(BaseHashes().SequenceEqual(baseHashes), "Active-game appearance, components and position edits leave all three base files byte-for-byte intact");
var savedA = profiles.Load().Profiles.Single(profile => profile.Id == a.Id);
Check(savedA.Settings == new HudProfileSettings(aAppearance with { ClickThrough = false }, aComponents, aPlacement), "The complete edited game snapshot is persisted independently");
Check(controller.Settings.Profiles.Single(profile => profile.Id == b.Id) == bBefore, "Editing Game A does not affect Game B");
Check(controller.BaseSettings == originalBase, "Active-game edits do not mutate the cached base snapshot");

DateTime marker = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
File.SetLastWriteTimeUtc(profilePath, marker);
Check(controller.AppearanceStore.Save(aAppearance) && controller.ComponentStore.Save(aComponents) && controller.PlacementStore.Save(aPlacement), "Unchanged routed edits remain successful");
Check(File.GetLastWriteTimeUtc(profilePath) == marker, "An unchanged game snapshot causes no extra disk write");
var suppressedSnapshot = controller.CurrentSettings;
string[] suppressedBase = BaseHashes();
string suppressedProfiles = Hash(profilePath);
IDisposable outer = controller.SuppressWrites();
using (controller.SuppressWrites())
{
    Check(controller.AppearanceStore.Save(aAppearance with { ClickThrough = false, Scale = .75 }) &&
        controller.ComponentStore.Save(ComponentSettings.Default) && controller.PlacementStore.Save(PlacementSettings.Default),
        "Nested suppression accepts automatic application without routing it into any store");
}
Check(controller.CaptureCurrent(HudProfileSettings.Default), "Outer suppression continues to cover snapshot capture after inner disposal");
Check(controller.CurrentSettings == suppressedSnapshot && BaseHashes().SequenceEqual(suppressedBase) && Hash(profilePath) == suppressedProfiles,
    "Suppressed switching changes neither cached snapshots nor base/profile files");
outer.Dispose(); outer.Dispose();
Check(controller.PlacementStore.Save(aPlacement with { XFraction = .42 }), "Idempotent suppression disposal restores normal saving exactly once");
Check(profiles.Load().Profiles.Single(profile => profile.Id == a.Id).Settings.Placement.XFraction == .42, "The first edit after suppression persists in its original game");
var selectedB = controller.Activate(b.Id);
Check(selectedB.Appearance.Scale == 1.4 && !selectedB.Appearance.Compact && selectedB.Appearance.ClickThrough,
    "Switching to Game B selects its independent layout without restoring a game-owned input mode");
Check(controller.Activate(null) == originalBase && BaseHashes().SequenceEqual(baseHashes), "Returning from games restores the original base snapshot without rewriting it");
controller.Activate(a.Id);
string inputComponentsBefore = Hash(componentPath), inputPlacementBefore = Hash(placementPath);
Check(controller.AppearanceStore.Save(controller.CurrentSettings.Appearance with { ClickThrough = false }), "An explicit input-mode change inside a game can save the global choice");
Check(appearance.Load() == (originalBase.Appearance with { ClickThrough = false }),
    "Saving global input changes only that field of the base appearance, not the game appearance");
Check(Hash(componentPath) == inputComponentsBefore && Hash(placementPath) == inputPlacementBefore,
    "Global input-mode edits never rewrite the base components or placement");
Check(!controller.Activate(b.Id).Appearance.ClickThrough && !controller.Activate(null).Appearance.ClickThrough,
    "The new input choice stays global across every game and base context");
Check(profiles.Load().Profiles.All(profile => !profile.Settings.Appearance.ClickThrough), "No game file stores a hidden input-mode override");

controller.Activate(a.Id);
var baseBeforeInputFailure = controller.BaseSettings;
string componentsBeforeInputRetry = Hash(componentPath), placementBeforeInputRetry = Hash(placementPath);
using (var locked = new FileStream(appearancePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Check(!controller.AppearanceStore.Save(controller.CurrentSettings.Appearance with { ClickThrough = true }),
        "An active game's global input-mode write reports a locked base appearance file");
Check(appearance.Load() == baseBeforeInputFailure.Appearance && controller.BaseSettings.Appearance.ClickThrough,
    "A failed global input write keeps the previous base bytes while retaining the user's pending choice");
Check(controller.CaptureCurrent(controller.CurrentSettings),
    "Capturing an unchanged active game retries the pending global input-mode save");
Check(appearance.Load() == (baseBeforeInputFailure.Appearance with { ClickThrough = true }) &&
    Hash(componentPath) == componentsBeforeInputRetry && Hash(placementPath) == placementBeforeInputRetry,
    "The input retry preserves base visuals, scale, components and position instead of copying game settings");
Check(controller.AppearanceStore.Save(controller.CurrentSettings.Appearance with { ClickThrough = false }),
    "The restored global input preference can still be changed normally inside a game");

controller.Activate(null);
var editedBase = new HudProfileSettings(new AppearanceSettings(IslandAppearance.Glass, .65, true, false, 1.2),
    originalBase.Components with { Gpu = true, Cpu = false },
    originalBase.Placement with { Placement = IslandPlacement.BottomLeft, Locked = true });
string profilesBeforeBase = Hash(profilePath);
appearance.Writes = components.Writes = placement.Writes = 0;
Check(controller.CaptureCurrent(editedBase), "Normal base-context edits use the existing persistence behavior");
Check(appearance.Load() == editedBase.Appearance && components.Load() == editedBase.Components && placement.Load() == editedBase.Placement,
    "Base-context edits persist in all three ordinary files");
Check(appearance.Writes == 1 && components.Writes == 1 && placement.Writes == 1 && Hash(profilePath) == profilesBeforeBase,
    "Base edits write each changed base file once and leave all game settings alone");
controller.Activate(b.Id);
var currentB = controller.CurrentSettings;
var wantedProfiles = controller.Settings with { Enabled = false };
Check(controller.ReplaceSettings(wantedProfiles) && !profiles.Load().Enabled, "Automatic selection can be disabled independently of game bindings");
Check(controller.KeepCurrentAsGlobal(currentB), "Disabling automatic profiles can keep the current HUD as the new global layout");
Check(controller.ActiveProfileId is null && controller.CurrentSettings == currentB &&
    appearance.Load() == currentB.Appearance && components.Load() == currentB.Components && placement.Load() == currentB.Placement,
    "Keeping the current layout changes the persistence context without changing any current setting");
Check(controller.Settings.Profiles.Single(profile => profile.Id == b.Id) == bBefore,
    "Keeping a game layout globally does not overwrite the saved game binding");
var recreated = new GameProfileController(appearance, components, placement, profiles);
Check(!recreated.Settings.Enabled && recreated.ActiveProfileId is null && recreated.CurrentSettings == currentB,
    "Restart with automatic profiles disabled restores exactly the layout kept by the user");

controller.Activate(a.Id);
byte[] profileBytesBeforeFailure = File.ReadAllBytes(profilePath);
string[] baseBeforeFailure = BaseHashes();
var pendingPosition = controller.CurrentSettings.Placement with { YFraction = .23 };
using (var locked = new FileStream(profilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Check(!controller.PlacementStore.Save(pendingPosition), "A locked profile file reports a failed active-game save");
Check(File.ReadAllBytes(profilePath).SequenceEqual(profileBytesBeforeFailure) && BaseHashes().SequenceEqual(baseBeforeFailure),
    "A failed profile replacement preserves the old profile bytes and all base files");
Check(controller.CurrentSettings.Placement == pendingPosition, "Failed persistence retains the user's current in-memory position for retry");
Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Failed replacement leaves no temporary preference files");
Check(controller.PlacementStore.Save(pendingPosition), "Repeating the same edit retries a previously failed profile save");
Check(profiles.Load().Profiles.Single(profile => profile.Id == a.Id).Settings.Placement == pendingPosition,
    "Dirty saved-state tracking does not incorrectly skip the retry");

controller.Activate(null);
var pendingBase = controller.CurrentSettings with
{
    Appearance = controller.CurrentSettings.Appearance with { Scale = .95 },
    Components = controller.CurrentSettings.Components with { Date = !controller.CurrentSettings.Components.Date },
    Placement = controller.CurrentSettings.Placement with { Locked = !controller.CurrentSettings.Placement.Locked }
};
byte[] appearanceBytesBeforeFailure = File.ReadAllBytes(appearancePath);
appearance.Writes = components.Writes = placement.Writes = 0;
using (var locked = new FileStream(appearancePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Check(!controller.CaptureCurrent(pendingBase), "A failed base appearance write is reported while capturing current settings");
Check(appearance.Writes == 1 && components.Writes == 1 && placement.Writes == 1,
    "A base-file failure still attempts the other independent base stores");
Check(File.ReadAllBytes(appearancePath).SequenceEqual(appearanceBytesBeforeFailure) &&
    components.Load() == pendingBase.Components && placement.Load() == pendingBase.Placement,
    "A locked destination retains original appearance bytes while successful independent settings are saved");
appearance.Writes = components.Writes = placement.Writes = 0;
Check(controller.CaptureCurrent(pendingBase), "Retrying a partially failed base capture succeeds");
Check(appearance.Load() == pendingBase.Appearance && appearance.Writes == 1 && components.Writes == 0 && placement.Writes == 0,
    "Retry writes only the previously failed file, retaining the successfully saved snapshots");

var baseBeforeGame = controller.CurrentSettings;
var pendingBaseVisual = baseBeforeGame.Appearance with { GlassOpacity = .47, Scale = 1.15 };
using (var locked = new FileStream(appearancePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Check(!controller.AppearanceStore.Save(pendingBaseVisual), "A base appearance edit can fail before a game becomes active");
Check(appearance.Load() == baseBeforeGame.Appearance, "Failed pre-game appearance editing preserves the previous base file");
var gameWhileBaseDirty = controller.Activate(b.Id);
appearance.Writes = components.Writes = placement.Writes = 0;
Check(controller.CaptureCurrent(gameWhileBaseDirty), "An active game's capture flushes a dirty base appearance left from before activation");
Check(appearance.Load() == pendingBaseVisual && appearance.Writes == 1 && components.Writes == 0 && placement.Writes == 0,
    "Pre-game retry saves only the pending base values, not the active game's different appearance or scale");
Check(controller.CurrentSettings == gameWhileBaseDirty &&
    profiles.Load().Profiles.Single(profile => profile.Id == b.Id).Settings == GameProfileStore.NormalizeSnapshot(gameWhileBaseDirty),
    "Retrying a pending base edit neither changes the active view nor overwrites the game's own snapshot");
controller.Activate(null);

var cloned = controller.Settings;
var originalFirst = controller.Settings.Profiles[0];
cloned.Profiles[0] = cloned.Profiles[0] with { Name = "External array mutation" };
Check(controller.Settings.Profiles[0] == originalFirst, "Mutating the public settings array cannot mutate controller state or its saved-state cache");
var replaceInput = controller.Settings;
replaceInput.Profiles[0] = replaceInput.Profiles[0] with { Name = "Renamed game" };
Check(controller.ReplaceSettings(replaceInput), "A deliberately replaced settings snapshot is saved");
replaceInput.Profiles[0] = replaceInput.Profiles[0] with { Name = "Mutation after save" };
Check(controller.Settings.Profiles[0].Name == "Renamed game" && profiles.Load().Profiles[0].Name == "Renamed game",
    "Changing the caller's array after a save does not alias either current or saved controller state");
controller.Activate(a.Id);
try { controller.Activate(Guid.NewGuid().ToString("D")); Check(false, "Unknown profile activation must fail"); }
catch (ArgumentException) { Check(controller.ActiveProfileId == a.Id, "Unknown activation leaves the valid active context unchanged"); }
Check(controller.ReplaceSettings(controller.Settings with { Profiles = controller.Settings.Profiles.Where(profile => profile.Id != a.Id).ToArray() }),
    "An active game binding can be removed");
Check(controller.ActiveProfileId is null && controller.CurrentSettings == controller.BaseSettings,
    "Removing the active profile clears the routing context before later edits");
Check(controller.AppearanceStore.Save(controller.CurrentSettings.Appearance with { GlassOpacity = .33 }) && appearance.Load().GlassOpacity == .33,
    "An edit immediately after removing the active profile saves normally instead of targeting a stale game ID");
Check(realPreferences.OrderBy(pair => pair.Key).SequenceEqual(RealPreferences().OrderBy(pair => pair.Key)), "Real user preference files were not changed");
File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
{
    passed = true, checks, completedChecks = completed,
    isolation = "Linked source and isolated JSON files only. No WPF startup, process monitoring, actual game paths, hardware, media or real preference writes. File locks exercise real Windows replacement failures.",
    directory
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks} game profile routing checks.");

sealed class CountedAppearance(string path) : AppearanceStore(path)
{
    public int Writes;
    public override bool Save(AppearanceSettings settings) { Writes++; return base.Save(settings); }
}
sealed class CountedComponents(string path) : ComponentStore(path)
{
    public int Writes;
    public override bool Save(ComponentSettings settings) { Writes++; return base.Save(settings); }
}
sealed class CountedPlacement(string path) : PlacementStore(path)
{
    public int Writes;
    public override bool Save(PlacementSettings settings) { Writes++; return base.Save(settings); }
}
namespace Luma.Island
{
    internal static class App { internal static void Log(Exception exception) { } }
}
