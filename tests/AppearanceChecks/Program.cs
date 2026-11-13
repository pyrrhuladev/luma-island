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
Check(AppearanceSettings.Default == new AppearanceSettings(.40), "new installs default to the quiet HUD with 60% background transparency");
Check(AppearanceStore.MinOpacity == .05 && AppearanceStore.MaxOpacity == 1, "supported background transparency range is zero through 95 percent");
File.WriteAllText(path, "{\"mode\":\"Glass\",\"glassOpacity\":0.35}");
Check(new AppearanceStore(path).Load() == new AppearanceSettings(.35), "legacy glass preferences retain their opacity");
const string legacyMatte = """
    {"mode":"Matte","compact":true,"glassOpacity":0.76,"clickThrough":true,"scale":1.25,
     "layout":{"metricColumns":2,"detachedMusic":true},"alerts":{"enabled":true,"cpuThreshold":94}}
    """;
File.WriteAllText(path, legacyMatte);
var restored = new AppearanceSettings(.76, ClickThrough: true, Scale: 1.25)
{
    Layout = HudLayoutSettings.Default with { MetricColumns = 2, DetachedMusic = true },
    Alerts = AlertSettings.Default with { Enabled = true, CpuThreshold = 94 }
};
Check(store.Load() == restored, "legacy matte and density fields are skipped while all supported preferences load");
Check(File.ReadAllText(path) == legacyMatte, "loading legacy preferences leaves the original file intact");
File.WriteAllText(path, "{\"mode\":999,\"compact\":\"bad\",\"glassOpacity\":0.25}");
Check(store.Load() == new AppearanceSettings(.25), "unsupported values in obsolete fields cannot discard saved opacity");

var glass = restored with { GlassOpacity = .82 };
Check(store.Save(glass), "valid settings save");
Check(new AppearanceStore(path).Load() == glass, "fresh store restores all supported preferences");
using (var json = JsonDocument.Parse(File.ReadAllText(path)))
{
    Check(!json.RootElement.TryGetProperty("mode", out _) && !json.RootElement.TryGetProperty("compact", out _),
        "retired appearance fields are omitted on the next normal save");
    Check(json.RootElement.GetProperty("glassOpacity").GetDouble() == .82, "camelCase properties");
}
foreach (double opacity in new[] { 1.0, .50, .40, .35, .25, .15, .05 })
{
    var airy = glass with { GlassOpacity = opacity };
    Check(store.Save(airy), "high-transparency preset saves: " + opacity);
    Check(new AppearanceStore(path).Load() == airy, "high-transparency preset survives fresh store: " + opacity);
}
Check(AppearanceStore.Normalize(glass with { GlassOpacity = -1 }) == glass with { GlassOpacity = AppearanceStore.MinOpacity },
    "lower opacity clamp preserves the other appearance preferences");
Check(AppearanceStore.Normalize(glass with { GlassOpacity = 9 }) == glass with { GlassOpacity = AppearanceStore.MaxOpacity },
    "upper opacity clamp preserves the other appearance preferences");

foreach (var content in new[]
{
    "{}", "null", "{oops", "[]",
    "{\"glassOpacity\":\"NaN\"}", "{\"glassOpacity\":NaN}", "{\"glassOpacity\":\"Infinity\"}"
})
{
    File.WriteAllText(path, content);
    Check(store.Load() == AppearanceSettings.Default, "invalid/default json: " + content);
}

File.WriteAllText(path, "{\"mode\":\"Glass\",\"glassOpacity\":0.01}");
Check(store.Load() == new AppearanceSettings(AppearanceStore.MinOpacity), "load lower bound clamped");
File.WriteAllText(path, "{\"mode\":\"Glass\",\"glassOpacity\":2}");
Check(store.Load() == new AppearanceSettings(AppearanceStore.MaxOpacity), "load upper bound clamped");
File.WriteAllText(path, "{\"glassOpacity\":1e999}");
Check(store.Load().GlassOpacity == AppearanceSettings.Default.GlassOpacity, "non-finite JSON number defaults");

foreach (double opacity in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
{
    Check(AppearanceStore.Normalize(glass with { GlassOpacity = opacity }) == glass with { GlassOpacity = .40 },
        "non-finite opacity normalization preserves the other appearance preferences");
    Check(store.Save(glass with { GlassOpacity = opacity }), "non-finite value can be saved safely");
    Check(new AppearanceStore(path).Load() == (glass with { GlassOpacity = .40 }), "non-finite opacity normalized before serialization");
}
Check(store.Save(glass with { GlassOpacity = -1 }), "save lower bound");
Check(store.Load().GlassOpacity == AppearanceStore.MinOpacity, "saved lower bound clamped");
Check(store.Save(glass with { GlassOpacity = 9 }), "save upper bound");
Check(store.Load().GlassOpacity == AppearanceStore.MaxOpacity, "saved upper bound clamped");

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

var treePreferences = HudCapsuleLayout.FromLegacy(HudLayoutSettings.Default).ToTree(_ => false);
string treeParent = treePreferences.Islands[0].Root!.Id;
for (int depth = 1; depth < HudCapsuleLayout.MaxTreeDepth - 1; depth++)
{
    var container = HudLayoutNode.Create("column");
    treePreferences = treePreferences.InsertNode(container, "main", treeParent, 0);
    treeParent = container.Id;
}
treePreferences = treePreferences.AddNode("time", "main", treeParent, 0);
var nestedAppearance = AppearanceSettings.Default with { Layout = HudLayoutSettings.Default with { Capsules = treePreferences } };
var nestedStore = new AppearanceStore(Path.Combine(directory, "nested-tree.json"));
Check(nestedStore.Save(nestedAppearance) && nestedStore.Load() == nestedAppearance,
    "Every supported container nesting level saves and loads through the real appearance store");

Console.WriteLine($"PASS: {checks} appearance checks. Evidence: {directory}");

namespace Luma.Island
{
    internal static class App { internal static void Log(Exception exception) { } }
}
