using Luma.Island.Services;
using System.Text.Json;

int count = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); count++; }
Check(HudLayoutSettings.Normalize(null) == HudLayoutSettings.Default, "Missing settings migrate to the original layout");
Check(HudLayoutSettings.Normalize(new(null!, null!, 0, double.NaN, double.PositiveInfinity)) == HudLayoutSettings.Default with { MetricColumns = 1 },
    "Invalid input becomes a finite, complete layout");
Check(HudLayoutSettings.Normalize(new(" MUSIC,header,music,alien,metrics ", " GPU,CPU,gpu ")).BlockOrder == "music,header,metrics", "Block keys are canonical and unique");
Check(HudLayoutSettings.Normalize(new(" MUSIC,header,music,alien,metrics ", " GPU,CPU,gpu ")).MetricOrder == "gpu,cpu,memory", "Missing metrics are appended without disturbing requested order");
Check(HudLayoutSettings.Normalize(new(new string('x', 1000), "")).BlockOrder == HudLayoutSettings.Default.BlockOrder, "Oversized order strings are discarded");
Check(HudLayoutSettings.Normalize(new(TextScale: double.NegativeInfinity)).TextScale == 1, "Nonfinite font input has a stable fallback");
Check(HudLayoutSettings.Normalize(new(Spacing: double.NaN)).Spacing == 6, "Nonfinite spacing input has a stable fallback");
Check(HudLayoutSettings.Normalize(new(MetricColumns: 9, TextScale: 5, Spacing: 20)) == new HudLayoutSettings(MetricColumns: 3, TextScale: 1.3, Spacing: 12), "Upper bounds prevent unbounded geometry");
Check(HudLayoutSettings.Normalize(new(MetricColumns: -1, TextScale: -.2, Spacing: -50)) == new HudLayoutSettings(MetricColumns: 1, TextScale: .8, Spacing: 2), "Lower bounds keep readable positive geometry");
string[] blocks = ["header,metrics,music", "header,music,metrics", "metrics,header,music", "metrics,music,header", "music,header,metrics", "music,metrics,header"];
string[] metrics = ["cpu,gpu,memory", "cpu,memory,gpu", "gpu,cpu,memory", "gpu,memory,cpu", "memory,cpu,gpu", "memory,gpu,cpu"];
foreach (string order in blocks)
    foreach (string metric in metrics)
    {
        var settings = new HudLayoutSettings(order, metric, 2, 1.17, 9);
        Check(HudLayoutSettings.Normalize(settings) == settings, "Every valid permutation round-trips without drift");
        var normalized = HudLayoutSettings.Normalize(settings with { BlockOrder = order.ToUpperInvariant(), MetricOrder = " " + metric.Replace(",", " , ") + " " });
        Check(normalized == settings && HudLayoutSettings.Normalize(normalized) == normalized, "Normalization is canonical, idempotent and value-comparable");
    }
Check(HudLayoutSettings.Default == new HudLayoutSettings(), "Normalization never mutates the shared default");
Check(!HudLayoutSettings.Default.DetachedMusic && HudLayoutSettings.Default.MusicPosition is null,
    "Existing installations keep a single island and no stored secondary position");
Check(HudIslandPosition.Normalize(null) is null, "Missing music position keeps initial automatic placement");
var musicPosition = new HudIslandPosition("  DISPLAY2  ", .123456789, .87654321);
var canonicalPosition = new HudIslandPosition("DISPLAY2", .123456789, .87654321);
Check(HudIslandPosition.Normalize(musicPosition) == canonicalPosition, "A secondary position keeps exact fractions and trims the monitor ID");
Check(musicPosition.MonitorId == "  DISPLAY2  ", "Position normalization leaves the input record untouched");
Check(HudIslandPosition.Normalize(new(" ", double.NaN, double.NegativeInfinity)) == new HudIslandPosition(),
    "Nonfinite secondary coordinates have stable finite defaults");
Check(HudIslandPosition.Normalize(new(null, -5, 20)) == new HudIslandPosition(null, 0, 1),
    "Secondary coordinates stay within the monitor travel range");
Check(HudIslandPosition.Normalize(canonicalPosition) == canonicalPosition, "Valid positions normalize without drift");
Check(HudIslandPosition.Normalize(HudIslandPosition.Normalize(musicPosition)) == canonicalPosition,
    "Secondary position normalization is idempotent");
var detached = new HudLayoutSettings(DetachedMusic: true, MusicPosition: musicPosition);
var canonicalDetached = detached with { MusicPosition = canonicalPosition };
Check(HudLayoutSettings.Normalize(detached) == canonicalDetached, "Layout normalization preserves split mode and normalizes its position");
Check(HudLayoutSettings.Normalize(canonicalDetached with { DetachedMusic = false }).MusicPosition == canonicalPosition,
    "Returning to one island retains the position for the next split");
Check(HudLayoutSettings.Normalize(canonicalDetached) == canonicalDetached, "A split layout is structurally comparable and stable");
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
var migrated = JsonSerializer.Deserialize<HudLayoutSettings>("{\"blockOrder\":\"header,metrics,music\",\"metricOrder\":\"cpu,gpu,memory\",\"metricColumns\":3,\"textScale\":1,\"spacing\":6}", jsonOptions);
Check(HudLayoutSettings.Normalize(migrated) == HudLayoutSettings.Default, "Pre-split JSON migrates to the original single island");
Check(HudLayoutSettings.Normalize(JsonSerializer.Deserialize<HudLayoutSettings>(JsonSerializer.Serialize(canonicalDetached, jsonOptions), jsonOptions)) == canonicalDetached,
    "A detached monitor position round-trips through the settings JSON");
Check(JsonSerializer.Deserialize<HudLayoutSettings>("{\"detachedMusic\":true,\"musicPosition\":null}", jsonOptions) is { DetachedMusic: true, MusicPosition: null },
    "Split mode may intentionally request initial automatic placement");

Check(canonicalDetached.Capsules is null && migrated?.Capsules is null, "Legacy layouts keep their existing runtime until a capsule draft is started");
var mainLayout = HudCapsuleLayout.FromLegacy(new(BlockOrder: "music,metrics,header", MetricOrder: "memory,cpu,gpu"));
Check(mainLayout.Islands.SequenceEqual(new[] { new HudCapsule("main", "music,memory,cpu,gpu,header") }),
    "Opening a capsule draft retains the legacy block and hardware order");
var split = HudCapsuleLayout.FromLegacy(canonicalDetached with { BlockOrder = "metrics,music,header", MetricOrder = "gpu,cpu,memory" });
Check(split.Islands.SequenceEqual(new[] { new HudCapsule("main", "gpu,cpu,memory,header"), new HudCapsule("music", "music", canonicalPosition) }),
    "Opening a capsule draft retains the detached player's monitor, position and remaining block order");
Check(HudCapsuleLayout.FromLegacy(detached with { MusicPosition = null }).Islands[1].Position is null,
    "Automatic legacy music placement remains automatic in the capsule model");

var splitCpu = mainLayout.Move("cpu", null, 0, musicPosition);
var cpu = splitCpu.Islands[1];
Check(splitCpu.Islands[0].Items == "music,memory,gpu,header" && cpu.Items == "cpu" && cpu.Position == canonicalPosition,
    "Dropping CPU outside gives it one independent capsule at the normalized drop position");
var movedCpu = splitCpu.Move("cpu", null, 0, new("DISPLAY1", .1, .2));
Check(movedCpu.Islands.Length == 2 && movedCpu.Islands[1] == cpu with { Position = new("DISPLAY1", .1, .2) },
    "Dragging a one-block capsule's content outside preserves its identity");
Check(ReferenceEquals(splitCpu.WithPosition("main", canonicalPosition), splitCpu) &&
      ReferenceEquals(splitCpu.WithPosition(cpu.Id, canonicalPosition), splitCpu),
    "The primary placement belongs to the existing placement store, and an unchanged detached anchor does not edit layout");

var grouped = splitCpu.Move("gpu", cpu.Id, 0);
Check(grouped.Islands[1].Items == "gpu,cpu" && grouped.Islands[1].Position == canonicalPosition &&
      grouped.Islands[0].Items == "music,memory,header",
    "Moving GPU before CPU groups both indicators in the existing destination without shifting its anchor");
var musicBelow = grouped.Move("music", cpu.Id, 2);
Check(musicBelow.Islands[1].Items == "gpu,cpu,music" && musicBelow.Islands[0].Items == "memory,header",
    "Music can join a hardware capsule below the metric grid");
var musicAbove = musicBelow.Move("music", cpu.Id, 0);
Check(musicAbove.Islands[1].Items == "music,gpu,cpu",
    "A drop above the hardware grid changes the music order inside the same capsule");
var reordered = musicAbove.Move("cpu", cpu.Id, 1);
Check(reordered.Islands[1].Items == "music,cpu,gpu",
    "Reordering hardware respects pre-removal insertion indexes");
var emptied = splitCpu.Move("cpu", "main", 2);
Check(emptied == mainLayout, "Returning the only detached widget removes its empty capsule and preserves value equality");
Check(ReferenceEquals(mainLayout.Move("header", null, 0, canonicalPosition), mainLayout) &&
      ReferenceEquals(mainLayout.Move("cpu", "missing", 0), mainLayout) &&
      ReferenceEquals(mainLayout.Move("cpu", null, 0), mainLayout),
    "Invalid destinations and attempts to move the fixed header leave the draft intact");

var malformed = new HudCapsuleLayout([
    new(" FIRST ", "gpu, music,cpu,header,unknown,gpu", new(" DISPLAY1 ", -1, 2)),
    new("MAIN", "memory,header,memory", canonicalPosition),
    new("first", "cpu"),
    new("bad/path", "header"),
    new("empty", "alien")
]);
var repaired = HudCapsuleLayout.Normalize(malformed);
Check(repaired.Islands.SequenceEqual(new[] {
    new HudCapsule("main", "memory,header"), new HudCapsule("first", "gpu,cpu,music", new("DISPLAY1", 0, 1))
}), "Normalization repairs IDs, monitor anchors, duplicate widgets, invalid islands and hardware grouping");
Check(ReferenceEquals(HudCapsuleLayout.Normalize(repaired), repaired) &&
      HudCapsuleLayout.Normalize(new(default)) == HudCapsuleLayout.FromLegacy(HudLayoutSettings.Default),
    "Normalization returns a stable value and restores missing widgets to the main capsule");
Check(musicBelow.Islands[1].GetInsertionIndex("music", 1) == 0 &&
      musicBelow.Islands[1].GetInsertionIndex("memory", 3) == 2,
    "Drop previews can use the same metric-grid insertion boundaries as the model");
var capsuleSettings = canonicalDetached with { Capsules = reordered };
string capsuleJson = JsonSerializer.Serialize(capsuleSettings, jsonOptions);
Check(HudLayoutSettings.Normalize(JsonSerializer.Deserialize<HudLayoutSettings>(capsuleJson, jsonOptions)) == capsuleSettings,
    "Ordered capsules and their independent positions round-trip with value equality through layout JSON");
Console.WriteLine($"PASS: {count} HUD layout settings checks.");
