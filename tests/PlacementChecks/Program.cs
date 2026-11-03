using System.Windows;
using System.IO;
using Luma.Island.Services;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
bool Near(double a, double b) => Math.Abs(a - b) < .001;
var directory = Path.Combine(Path.GetFullPath(args.Length > 0 ? args[0] : "work"), "placement-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "settings.json");
var store = new PlacementStore(path);
Check(store.Load() == PlacementSettings.Default, "missing settings");
var custom = new PlacementSettings("secondary", IslandPlacement.Custom, .21, .87, true);
Check(store.Save(custom) && new PlacementStore(path).Load() == custom, "custom state survives fresh store");
Check(store.Save(custom with { XFraction = -1, YFraction = 5 }), "save out-of-range");
Check(store.Load().XFraction == 0 && store.Load().YFraction == 1, "fractions clamped");
Check(store.Save(custom with { XFraction = double.NaN, YFraction = double.PositiveInfinity }), "save non-finite");
Check(store.Load().XFraction == .5 && store.Load().YFraction == 0, "non-finite normalized");
foreach (var content in new[] { "{}", "null", "{oops", "{\"placement\":\"Unknown\"}", "{\"placement\":999}" })
{
    File.WriteAllText(path, content);
    Check(store.Load() == PlacementSettings.Default, "invalid/default json: " + content);
}
store.Save(custom);
string before = File.ReadAllText(path);
using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Check(!store.Save(custom with { Locked = false }), "write failure reported");
Check(File.ReadAllText(path) == before, "failed save leaves previous settings intact");
Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "no partial temp settings");

var monitors = new[]
{
    new DisplayInfo("primary", "Primary", new Rect(0, 0, 1920, 1040), true),
    new DisplayInfo("secondary", "Secondary", new Rect(-2560, 80, 2560, 1400), false)
};
Check(DisplayService.Resolve("missing", monitors).Id == "primary", "removed monitor fallback");
Check(DisplayService.Resolve("secondary", monitors).Id == "secondary", "selected monitor");
foreach (double scale in new[] { 1d, 1.5, 2d })
foreach (var display in monitors)
foreach (var preset in Enum.GetValues<IslandPlacement>().Where(p => p != IslandPlacement.Custom))
{
    var fractions = PlacementMath.Fractions(new PlacementSettings(Placement: preset));
    var collapsed = PlacementMath.Bounds(display.WorkArea, new Size(408 * scale, 80 * scale), fractions, 8 * scale);
    var expanded = PlacementMath.Bounds(display.WorkArea, new Size(408 * scale, 268 * scale), fractions, 8 * scale);
    Check(display.WorkArea.Contains(collapsed) && display.WorkArea.Contains(expanded), "contained preset: " + preset);
    var saved = PlacementMath.Capture(display.WorkArea, collapsed, 8 * scale);
    Check(Near(saved.X, fractions.X) && Near(saved.Y, fractions.Y), "custom coordinate roundtrip");
    var after = PlacementMath.Bounds(display.WorkArea, collapsed.Size, fractions, 8 * scale);
    Check(after == collapsed, "expand/collapse does not change canonical point");
    if (preset.ToString().StartsWith("Bottom")) Check(Near(collapsed.Bottom, expanded.Bottom), "bottom anchoring");
}
Check(PlacementMath.NearestDisplay(new Rect(-2400, 120, 408, 80), monitors).Id == "secondary", "drag to negative-coordinate monitor");
Check(PlacementMath.NearestDisplay(new Rect(9000, 9000, 408, 80), monitors).Id == "primary", "off-screen nearest fallback");
var tiny = PlacementMath.Bounds(new Rect(100, 200, 200, 40), new Size(408, 268), new Point(1, 1), 8);
Check(tiny.Location == new Point(100, 200), "tiny display keeps top-left accessible");
Console.WriteLine($"PASS: {checks} placement checks. Evidence: {directory}");

namespace Luma.Island
{
    internal static class App { internal static void Log(Exception exception) { } }
}
