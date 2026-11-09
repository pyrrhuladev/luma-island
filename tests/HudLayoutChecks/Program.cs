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
const string header = "fps,low,time,battery";
const string mainTail = "cpu-chart,gpu-chart,memory-chart,fps-chart,frame-time,volume,date";
var mainLayout = HudCapsuleLayout.FromLegacy(new(BlockOrder: "music,metrics,header", MetricOrder: "memory,cpu,gpu"));
Check(mainLayout.Islands.SequenceEqual(new[] { new HudCapsule("main", $"music,memory,cpu,gpu,{header},{mainTail}") }),
    "Opening a capsule draft retains the legacy block and hardware order");
var split = HudCapsuleLayout.FromLegacy(canonicalDetached with { BlockOrder = "metrics,music,header", MetricOrder = "gpu,cpu,memory" });
Check(split.Islands.SequenceEqual(new[] { new HudCapsule("main", $"gpu,cpu,memory,{header},cpu-chart,gpu-chart,memory-chart,fps-chart,frame-time,date"),
    new HudCapsule("music", "music,volume", canonicalPosition) }),
    "Opening a capsule draft retains the detached player's monitor, position and remaining block order");
Check(HudCapsuleLayout.FromLegacy(detached with { MusicPosition = null }).Islands[1].Position is null,
    "Automatic legacy music placement remains automatic in the capsule model");

const string version20 = """
    {"capsules":{"islands":[
        {"id":"main","items":"gpu,header,memory"},
        {"id":"side","items":"cpu,music","position":{"monitorId":"DISPLAY2","xFraction":0.25,"yFraction":0.75}}
    ]}}
    """;
var upgraded = HudLayoutSettings.Normalize(JsonSerializer.Deserialize<HudLayoutSettings>(version20, jsonOptions)).Capsules!;
Check(upgraded.Islands.SequenceEqual(new[] {
    new HudCapsule("main", $"gpu,memory,{header},gpu-chart,memory-chart,fps-chart,frame-time,date"),
    new HudCapsule("side", "cpu,music,cpu-chart,volume", new("DISPLAY2", .25, .75))
}), "Older interactive layouts expand the header, preserve the metric group, and keep graphs and volume near their former owners");
Check(ReferenceEquals(HudCapsuleLayout.Normalize(upgraded), upgraded), "The upgraded layout does not migrate twice");

var splitCpu = mainLayout.Move("cpu", null, 0, musicPosition);
var cpu = splitCpu.Islands[1];
Check(splitCpu.Islands[0].Items == $"music,memory,gpu,{header},{mainTail}" && cpu.Items == "cpu" && cpu.Position == canonicalPosition,
    "Dropping CPU outside gives it an independent capsule and leaves its chart in place");
var movedCpu = splitCpu.Move("cpu", null, 0, new("DISPLAY1", .1, .2));
Check(movedCpu.Islands.Length == 2 && movedCpu.Islands[1] == cpu with { Position = new("DISPLAY1", .1, .2) },
    "Dragging a one-block capsule's content outside preserves its identity");
Check(ReferenceEquals(splitCpu.WithPosition("main", canonicalPosition), splitCpu) &&
      ReferenceEquals(splitCpu.WithPosition(cpu.Id, canonicalPosition), splitCpu),
    "The primary placement belongs to the existing placement store, and an unchanged detached anchor does not edit layout");

var grouped = splitCpu.Move("gpu", cpu.Id, 0);
Check(grouped.Islands[1].Items == "gpu,cpu" && grouped.Islands[1].Position == canonicalPosition &&
      grouped.Islands[0].Items == $"music,memory,{header},{mainTail}",
    "Moving GPU before CPU groups both indicators in the existing destination without shifting its anchor");
var musicBelow = grouped.Move("music", cpu.Id, 2);
Check(musicBelow.Islands[1].Items == "gpu,cpu,music" && musicBelow.Islands[0].Items == $"memory,{header},{mainTail}",
    "Music can join a hardware capsule below the metric grid while volume stays separate");
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
    "Invalid destinations and attempts to move a legacy placeholder leave the draft intact");

var malformed = new HudCapsuleLayout([
    new(" FIRST ", "gpu, music,cpu,header,unknown,gpu", new(" DISPLAY1 ", -1, 2)),
    new("MAIN", "memory,header,memory", canonicalPosition),
    new("first", "cpu"),
    new("bad/path", "header"),
    new("empty", "alien")
]);
var repaired = HudCapsuleLayout.Normalize(malformed);
Check(repaired.Islands.SequenceEqual(new[] {
    new HudCapsule("main", $"memory,{header},memory-chart,fps-chart,frame-time,date"),
    new HudCapsule("first", "gpu,cpu,music,cpu-chart,gpu-chart,volume", new("DISPLAY1", 0, 1))
}), "Normalization repairs IDs, monitor anchors, duplicate widgets, invalid islands and hardware grouping");
Check(ReferenceEquals(HudCapsuleLayout.Normalize(repaired), repaired) &&
      HudCapsuleLayout.Normalize(new(default)) == HudCapsuleLayout.FromLegacy(HudLayoutSettings.Default),
    "Normalization returns a stable value and restores missing widgets to the main capsule");
Check(musicBelow.Islands[1].GetInsertionIndex("music", 1) == 0 &&
      musicBelow.Islands[1].GetInsertionIndex("memory", 3) == 2,
    "Drop previews can use the same metric-grid insertion boundaries as the model");

var initial = HudCapsuleLayout.FromLegacy(HudLayoutSettings.Default);
string[] items = initial.Islands[0].GetItems();
Check(items.Length == 15 && items.Distinct().Count() == 15 && items.All(HudCapsuleLayout.IsMovable),
    "Every rendered tile has one distinct movable ID");
foreach ((string item, int index) in items.Select((item, index) => (item, index)))
{
    var one = initial.Move(item, null, 0, canonicalPosition);
    Check(one.Islands[1].Items == item && !one.Islands[0].GetItems().Contains(item) &&
          ReferenceEquals(HudCapsuleLayout.Normalize(one), one), "A tile can be split independently: " + item);
    Check(one.Move(item, "main", index) == initial, "An independent tile can return to its previous place: " + item);
}
var detachedEverything = initial.Move(items[0], null, 0, canonicalPosition);
string detachedId = detachedEverything.Islands[1].Id;
foreach (string item in items.Skip(1)) detachedEverything = detachedEverything.Move(item, detachedId, int.MaxValue);
Check(detachedEverything.Islands[0] == new HudCapsule("main", "") && detachedEverything.Islands[1].GetItems().Length == items.Length &&
      ReferenceEquals(HudCapsuleLayout.Normalize(detachedEverything), detachedEverything),
    "The empty main capsule remains a valid drop destination after moving all widgets away");
Check(detachedEverything.Move("time", "main", 0).Islands[0].Items == "time", "A tile can be dropped into the empty main capsule");
var groups = new HudCapsule("test", "fps,low,time,battery,cpu-chart,memory-chart");
Check(groups.GetInsertionIndex("volume", 2) == 0 && groups.GetInsertionIndex("date", 5) == 4 &&
      groups.GetInsertionIndex("gpu-chart", 0) == 4 && groups.GetInsertionIndex("battery", 0) == 0,
    "Standalone rows snap to the edges of the header and chart groups while tiles can be reordered within a group");
var capsuleSettings = canonicalDetached with { Capsules = reordered };
string capsuleJson = JsonSerializer.Serialize(capsuleSettings, jsonOptions);
Check(HudLayoutSettings.Normalize(JsonSerializer.Deserialize<HudLayoutSettings>(capsuleJson, jsonOptions)) == capsuleSettings,
    "Ordered capsules and their independent positions round-trip with value equality through layout JSON");

Check(initial.Islands[0].Rows is null && initial.Islands[0].GetRows()[0].SequenceEqual(header.Split(',')) &&
      initial.Islands[0].GetRows()[1].SequenceEqual(new[] { "cpu", "gpu", "memory" }),
    "A layout saved without explicit rows retains the existing header and metric groups");
Check(ReferenceEquals(initial.MoveToRow("time", "main", 0, 2, newRow: false), initial) &&
      ReferenceEquals(initial.MoveToRow("time", "main", 0, 3, newRow: false), initial),
    "Dropping on either side of an unchanged tile does not convert its legacy capsule to explicit rows");

var dateBesideTime = initial.MoveToRow("date", "main", 0, 3, newRow: false);
var dateBesideTimeRoot = dateBesideTime.Islands[0];
Check(dateBesideTimeRoot.GetRows()[0].SequenceEqual(new[] { "fps", "low", "time", "date", "battery" }) &&
      dateBesideTimeRoot.GetRows().Length == initial.Islands[0].GetRows().Length - 1 &&
      dateBesideTimeRoot.Items == string.Join(',', dateBesideTimeRoot.GetRows().SelectMany(row => row)),
    "Dropping the date beside the clock combines different widget types in one row and removes the vacated row");
Check(ReferenceEquals(HudCapsuleLayout.Normalize(dateBesideTime), dateBesideTime),
    "An explicit mixed row is not regrouped by widget type during normalization");

var dateAboveMetrics = dateBesideTime.MoveToRow("date", "main", 1, 0, newRow: true);
Check(dateAboveMetrics.Islands[0].GetRows()[0].SequenceEqual(header.Split(',')) &&
      dateAboveMetrics.Islands[0].GetRows()[1].SequenceEqual(new[] { "date" }) &&
      dateAboveMetrics.Islands[0].GetRows()[2].SequenceEqual(new[] { "cpu", "gpu", "memory" }),
    "Dropping between rows creates a full-width row at the pre-removal destination index");
Check(ReferenceEquals(dateAboveMetrics.MoveToRow("date", "main", 2, 0, newRow: true), dateAboveMetrics) &&
      dateAboveMetrics.MoveToRow("date", "main", 0, 3, newRow: false) == dateBesideTime,
    "A no-op at a neighboring row boundary preserves the draft and a real move returns the prior value");
var dateBelowMusic = dateAboveMetrics.MoveToRow("date", "main", 4, 0, newRow: true);
Check(dateBelowMusic.Islands[0].GetRows()[2].SequenceEqual(new[] { "music" }) &&
      dateBelowMusic.Islands[0].GetRows()[3].SequenceEqual(new[] { "date" }),
    "Moving a singleton row down accounts for the source row being removed before the destination");

var explicitSplit = dateBesideTime.Move("date", null, 0, canonicalPosition);
var detachedDate = explicitSplit.Islands[1];
Check(detachedDate.Items == "date" && detachedDate.Position == canonicalPosition &&
      explicitSplit.Islands[0].GetRows()[0].SequenceEqual(header.Split(',')) &&
      explicitSplit.Islands[0].Rows is not null,
    "A free drop preserves existing explicit rows and creates an independent capsule at the requested position");
var repositionedDate = explicitSplit.WithPosition(detachedDate.Id, new("DISPLAY1", .1, .2));
Check(repositionedDate.Islands[0] == explicitSplit.Islands[0] && repositionedDate.Islands[1].Position == new HudIslandPosition("DISPLAY1", .1, .2),
    "Moving a detached capsule's window does not edit either capsule's rows");
Check(explicitSplit.MoveToRow("date", "main", 0, 3, newRow: false) == dateBesideTime &&
      explicitSplit.Move("date", "main", 3) == dateBesideTime,
    "Both precise and legacy flat reinsertions respect explicit rows and remove the empty detached capsule");

var dateInFloatingRow = splitCpu.MoveToRow("date", cpu.Id, 0, 1, newRow: false);
Check(dateInFloatingRow.Islands[0].Rows is null && dateInFloatingRow.Islands[1].Rows == "cpu,date" &&
      dateInFloatingRow.Islands[1].Position == canonicalPosition,
    "A cross-capsule drop converts only the destination to explicit rows and keeps its position");
var dateBackInLegacy = dateInFloatingRow.Move("date", "main", int.MaxValue);
Check(dateBackInLegacy.Islands[0].Rows is null && dateBackInLegacy.Islands[1].Rows == "cpu",
    "A legacy drop from an explicit source updates its rows without changing the destination's grouping");
var returnedClock = detachedEverything.MoveToRow("time", "main", 0, 0, newRow: false);
Check(returnedClock.Islands[0].Rows == "time" && returnedClock.Islands[0].GetRows().Length == 1,
    "A drop in the empty main destination creates its first row");

var malformedRows = new HudCapsuleLayout([
    new("main", "ignored") { Rows = " DATE , TIME ;; CPU , date ,alien; GPU " },
    new("floating", "memory", new(" DISPLAY2 ", -1, 2)) { Rows = new string('x', 257) }
]);
var repairedRows = HudCapsuleLayout.Normalize(malformedRows);
Check(repairedRows.Islands[0].GetRows().Take(3).Select(row => string.Join(',', row)).SequenceEqual(new[] { "date,time", "cpu", "gpu" }) &&
      repairedRows.Islands[0].GetItems().Contains("frame-time") && repairedRows.Islands[0].GetItems().Contains("cpu-chart") &&
      repairedRows.Islands.SelectMany(island => island.GetItems()).Order().SequenceEqual(items.Order()),
    "Normalization retains valid explicit rows, repairs malformed entries and migrates every missing tile exactly once");
Check(repairedRows.Islands[1].Rows is null && repairedRows.Islands[1].Position == new HudIslandPosition("DISPLAY2", 0, 1),
    "Oversized rows fall back to the legacy items and keep normalized floating coordinates");

var explicitSettings = canonicalDetached with { Capsules = dateAboveMetrics };
var explicitJson = JsonSerializer.Serialize(explicitSettings, jsonOptions);
var explicitRestored = HudLayoutSettings.Normalize(JsonSerializer.Deserialize<HudLayoutSettings>(explicitJson, jsonOptions));
Check(explicitRestored == explicitSettings && explicitRestored.GetHashCode() == explicitSettings.GetHashCode() &&
      explicitRestored.Capsules != dateBesideTime,
    "JSON round-trips explicit rows and compares both widget order and row boundaries by value");
Console.WriteLine($"PASS: {count} HUD layout settings checks.");
