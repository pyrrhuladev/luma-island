using System.Text.Json;
using Luma.Island.Services;
using System.IO;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }

var directory = Path.Combine(Path.GetFullPath(args.Length > 0 ? args[0] : "work"),
    "appearance-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "appearance.json");
var store = new AppearanceStore(path);

Check(store.Load() == AppearanceSettings.Default, "missing file returns defaults");
Check(!File.Exists(path), "reading missing settings does not create a file");
Check(AppearanceStore.Normalize(null) == AppearanceSettings.Default, "null settings normalize");
Check(AppearanceSettings.Default == new AppearanceSettings(IslandAppearance.Glass, .40), "new installs default to the quiet HUD with 60% background transparency");
Check(AppearanceStore.MinOpacity == .05 && AppearanceStore.MaxOpacity == 1, "supported background transparency range is zero through 95 percent");
Check(!store.Load().Compact && !AppearanceSettings.Default.Compact, "missing/default settings leave compact mode disabled");
Check(!new AppearanceSettings(IslandAppearance.Glass, .35).Compact, "legacy two-argument constructor leaves compact mode disabled");
File.WriteAllText(path, "{\"mode\":\"Glass\",\"glassOpacity\":0.35}");
Check(new AppearanceStore(path).Load() == new AppearanceSettings(IslandAppearance.Glass, .35, false),
    "legacy JSON preserves mode/opacity and defaults compact to false");
File.WriteAllText(path, "{\"mode\":\"Matte\",\"glassOpacity\":0.76,\"compact\":true}");
Check(new AppearanceStore(path).Load() == new AppearanceSettings(IslandAppearance.Matte, .76, true),
    "existing matte mode and saved compact density survive the new defaults");
File.WriteAllText(path, "{\"mode\":\"Glass\",\"glassOpacity\":0.25,\"compact\":true}");
Check(new AppearanceStore(path).Load() == new AppearanceSettings(IslandAppearance.Glass, .25, true),
    "existing highest-transparency choice remains exactly as saved");

var glass = new AppearanceSettings(IslandAppearance.Glass, .82);
Check(store.Save(glass), "valid settings save");
Check(new AppearanceStore(path).Load() == glass, "fresh store restores mode and opacity");
using (var json = JsonDocument.Parse(File.ReadAllText(path)))
{
    Check(json.RootElement.GetProperty("mode").GetString() == "Glass", "enum stored as string");
    Check(json.RootElement.GetProperty("glassOpacity").GetDouble() == .82, "camelCase properties");
}
foreach (double opacity in new[] { 1.0, .50, .40, .35, .25, .15, .05 })
{
    var airy = glass with { GlassOpacity = opacity };
    Check(store.Save(airy), "high-transparency preset saves: " + opacity);
    Check(new AppearanceStore(path).Load() == airy, "high-transparency preset survives fresh store: " + opacity);
}
foreach (var mode in new[] { IslandAppearance.Matte, IslandAppearance.Glass })
foreach (bool compact in new[] { true, false })
{
    var expected = new AppearanceSettings(mode, .35, compact);
    Check(store.Save(expected), "compact/full mode saves independently of appearance: " + mode + "/" + compact);
    Check(new AppearanceStore(path).Load() == expected, "compact/full mode roundtrips independently of appearance: " + mode + "/" + compact);
    using var json = JsonDocument.Parse(File.ReadAllText(path));
    Check(json.RootElement.GetProperty("compact").GetBoolean() == compact, "compact is stored as a boolean");
}
Check(AppearanceStore.Normalize(new AppearanceSettings(IslandAppearance.Glass, -1, true)) ==
    new AppearanceSettings(IslandAppearance.Glass, AppearanceStore.MinOpacity, true), "lower opacity clamp preserves compact mode");
Check(AppearanceStore.Normalize(new AppearanceSettings(IslandAppearance.Glass, 9, true)) ==
    new AppearanceSettings(IslandAppearance.Glass, AppearanceStore.MaxOpacity, true), "upper opacity clamp preserves compact mode");
Check(AppearanceStore.Normalize(new AppearanceSettings((IslandAppearance)999, .35, true)) ==
    new AppearanceSettings(IslandAppearance.Matte, .35, true), "invalid appearance mode fallback preserves compact mode");
foreach (double opacity in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
{
    var normalized = new AppearanceSettings(IslandAppearance.Glass, .40, true);
    Check(AppearanceStore.Normalize(new AppearanceSettings(IslandAppearance.Glass, opacity, true)) == normalized,
        "non-finite opacity normalization preserves compact mode");
    Check(store.Save(new AppearanceSettings(IslandAppearance.Glass, opacity, true)) && new AppearanceStore(path).Load() == normalized,
        "normalized compact settings survive persistence");
}

foreach (var content in new[]
{
    "{}", "null", "{oops", "[]",
    "{\"mode\":\"Unknown\"}", "{\"mode\":999}",
    "{\"glassOpacity\":\"NaN\"}", "{\"glassOpacity\":NaN}",
    "{\"glassOpacity\":\"Infinity\"}", "{\"compact\":\"true\"}", "{\"compact\":1}", "{\"compact\":null}"
})
{
    File.WriteAllText(path, content);
    Check(store.Load() == AppearanceSettings.Default, "invalid/default json: " + content);
}

File.WriteAllText(path, "{\"mode\":\"Glass\",\"glassOpacity\":0.01}");
Check(store.Load() == new AppearanceSettings(IslandAppearance.Glass, AppearanceStore.MinOpacity), "load lower bound clamped");
File.WriteAllText(path, "{\"mode\":\"Glass\",\"glassOpacity\":2}");
Check(store.Load() == new AppearanceSettings(IslandAppearance.Glass, AppearanceStore.MaxOpacity), "load upper bound clamped");
File.WriteAllText(path, "{\"glassOpacity\":1e999}");
Check(store.Load().GlassOpacity == AppearanceSettings.Default.GlassOpacity, "non-finite JSON number defaults");

Check(AppearanceStore.Normalize(new AppearanceSettings((IslandAppearance)999, .82)) ==
    new AppearanceSettings(IslandAppearance.Matte, .82), "invalid enum normalizes without discarding opacity");
foreach (double opacity in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
{
    Check(store.Save(glass with { GlassOpacity = opacity }), "non-finite value can be saved safely");
    Check(new AppearanceStore(path).Load() == (glass with { GlassOpacity = .40 }), "non-finite opacity normalized before serialization");
}
Check(store.Save(glass with { GlassOpacity = -1 }), "save lower bound");
Check(store.Load().GlassOpacity == AppearanceStore.MinOpacity, "saved lower bound clamped");
Check(store.Save(glass with { GlassOpacity = 9 }), "save upper bound");
Check(store.Load().GlassOpacity == AppearanceStore.MaxOpacity, "saved upper bound clamped");
Check(store.Save(glass with { Mode = (IslandAppearance)(-1) }), "save invalid enum");
Check(store.Load() == (glass with { Mode = IslandAppearance.Matte }), "saved invalid enum becomes matte");

Check(store.Save(glass), "prepare original file for locked write");
string before = File.ReadAllText(path);
using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    Check(!store.Save(new AppearanceSettings()), "locked destination reports save failure");
    Check(store.Load() == AppearanceSettings.Default, "locked file load falls back safely");
}
Check(File.ReadAllText(path) == before, "failed replacement preserves old file bytes");
Check(new AppearanceStore(path).Load() == glass, "old settings survive a failed save");
Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "temporary files cleaned after failure");

Console.WriteLine($"PASS: {checks} appearance checks. Evidence: {directory}");

namespace Luma.Island
{
    internal static class App { internal static void Log(Exception exception) { } }
}
