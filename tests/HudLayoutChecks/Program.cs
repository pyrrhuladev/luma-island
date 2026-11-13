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

var legacyInstances = dateBesideTime.Move("cpu", null, 0, canonicalPosition);
var migratedInstances = legacyInstances.ToInstances(kind => kind is "time" or "date" or "cpu" or "cpu-temp" or "gpu-temp");
Check(migratedInstances.HasInstances && !legacyInstances.HasInstances && migratedInstances.Islands[0].GetRows().Length == 2 &&
      migratedInstances.Islands[0].GetRows()[0].SequenceEqual(new[] { "time", "date" }) &&
      migratedInstances.Islands[0].GetRows()[1].SequenceEqual(new[] { "gpu-temp" }) &&
      migratedInstances.Islands[1].GetItems().SequenceEqual(new[] { "cpu", "cpu-temp" }) &&
      migratedInstances.Islands[1].Position == canonicalPosition,
    "Instance migration retains visible rows, floating anchors and temperatures including a temperature with no numeric tile");
Check(ReferenceEquals(migratedInstances.ToInstances(_ => false), migratedInstances),
    "An existing instance layout is independent of legacy visibility preferences");

var clocks = initial.ToInstances(kind => kind == "time");
for (int i = 1; i < 20; i++) clocks = clocks.AddInstance("time");
string[] clockIds = clocks.Islands[0].GetItems();
Check(clockIds.Length == 20 && clockIds.Distinct().Count() == clockIds.Length &&
      clockIds.All(id => HudCapsuleLayout.IsMovable(id) && HudCapsuleLayout.KindOf(id) == "time") &&
      clocks.Islands[0].GetRows().All(row => row.Length == 4) && clocks.Islands[0].Items.Length > 256,
    "Twenty clocks have independent identities and fit ordinary rows beyond the legacy text limit");
var savedClocks = JsonSerializer.Serialize(HudLayoutSettings.Default with { Capsules = clocks }, jsonOptions);
var loadedClocks = HudLayoutSettings.Normalize(JsonSerializer.Deserialize<HudLayoutSettings>(savedClocks, jsonOptions)).Capsules!;
Check(loadedClocks == clocks && loadedClocks.GetHashCode() == clocks.GetHashCode() &&
      ReferenceEquals(HudCapsuleLayout.Normalize(loadedClocks), loadedClocks),
    "All instances and their rows survive JSON and canonicalize without changing their identities");

string movedClock = clockIds[7];
var floatingClock = clocks.Move(movedClock, null, 0, canonicalPosition);
string clockCapsule = floatingClock.Islands[1].Id;
Check(floatingClock.HasInstances && floatingClock.Islands[1].GetItems().SequenceEqual(new[] { movedClock }) &&
      !floatingClock.Islands[0].GetItems().Contains(movedClock) &&
      floatingClock.WithPosition(clockCapsule, new("DISPLAY2", .1, .2)).HasInstances,
    "Moving and positioning a duplicate retains its exact instance ID and schema");
var regroupedClocks = floatingClock.MoveToRow(movedClock, HudCapsuleLayout.MainId, 0, 1, newRow: false);
Check(regroupedClocks.HasInstances && regroupedClocks.Islands.Length == 1 &&
      regroupedClocks.Islands[0].GetRows()[0][1] == movedClock,
    "A row drop targets one duplicate and removes its empty capsule");
var removedClock = floatingClock.RemoveInstance(movedClock);
Check(removedClock.HasInstances && removedClock.Islands.Length == 1 &&
      removedClock.Islands[0].GetItems().SequenceEqual(clockIds.Where(id => id != movedClock)),
    "Deleting a duplicate removes just its instance and prunes an empty detached capsule");
var resetClocks = floatingClock.ResetInstances();
Check(resetClocks.HasInstances && resetClocks.Islands.Length == 1 &&
      resetClocks.Islands[0].GetItems().Order().SequenceEqual(clockIds.Order()),
    "Reset gathers all instances without discarding identities");

var noWidgets = initial.ToInstances(_ => false);
var blankWidgets = noWidgets.AddInstance("cpu-temp");
blankWidgets = blankWidgets.RemoveInstance(blankWidgets.Islands[0].GetItems().Single());
Check(noWidgets.HasInstances && noWidgets.Islands.Length == 1 && blankWidgets.HasInstances &&
      HudCapsuleLayout.Normalize(blankWidgets).Islands[0].GetItems().Length == 0 &&
      !blankWidgets.ResetInstances().Islands[0].GetItems().Any(),
    "An intentional empty layout and resetting it never recreate old defaults");
var invalidInstances = new HudCapsuleLayout([
    new("main", "") { Rows = $"TIME,{movedClock.ToUpperInvariant()},time@invalid;CPU-TEMP" },
    new("side", $"{movedClock},bogus@{Guid.NewGuid():N}")
]) { HasInstances = true };
var cleanInstances = HudCapsuleLayout.Normalize(invalidInstances);
Check(cleanInstances.Islands.Length == 1 && cleanInstances.Islands[0].GetItems().SequenceEqual(new[] { "time", movedClock, "cpu-temp" }) &&
      ReferenceEquals(cleanInstances.AddInstance(movedClock), cleanInstances) &&
      ReferenceEquals(cleanInstances.AddInstance("time", "missing"), cleanInstances),
    "Normalization and add reject malformed IDs, duplicate references and unknown destinations");

var capacity = noWidgets;
for (int i = 0; i < HudCapsuleLayout.MaxInstances; i++) capacity = capacity.AddInstance("memory-chart");
Check(capacity.Islands[0].GetItems().Length == HudCapsuleLayout.MaxInstances &&
      ReferenceEquals(HudCapsuleLayout.Normalize(capacity), capacity) && !capacity.CanAddInstance &&
      ReferenceEquals(capacity.AddInstance("time"), capacity),
    "The defensive limit keeps all accepted instances intact and rejects additions at capacity");
var tree = initial.ToTree(kind => kind is "time" or "cpu" or "gpu" or "memory" or "cpu-temp" or
    "cpu-chart" or "gpu-chart" or "memory-chart" or "fps-chart", metricColumns: 1, spacing: 8);
var treeRoot = tree.Islands[0].Root!;
Check(tree.HasTree && tree.HasInstances && treeRoot.Kind == "column" && treeRoot.Gap == 8 &&
      tree.GetNode("cpu-temp") is not null && treeRoot.GetChildren().Where(node => node.Kind == "row").All(node => node.GetChildren().Length == 2),
    "Tree migration materializes the saved one-column metrics and two-column charts with independent temperature");
Check(ReferenceEquals(tree.ToTree(_ => true), tree) && ReferenceEquals(HudCapsuleLayout.Normalize(tree), tree),
    "Tree migration and structural normalization retain identities and do not churn snapshots");
var treeJson = JsonSerializer.Serialize(tree, jsonOptions);
var treeReload = JsonSerializer.Deserialize<HudCapsuleLayout>(treeJson, jsonOptions)!;
Check(HudCapsuleLayout.Normalize(treeReload) == tree && treeReload.GetHashCode() == tree.GetHashCode(),
    "A nested layout roundtrips with structural equality and stable hashes");

var groupNode = HudLayoutNode.Create("row") with { Gap = 9, Padding = 4, Alignment = "center", Sizing = "stretch" };
var nested = tree.InsertNode(groupNode, "main", null, 1);
var innerGrid = HudLayoutNode.Create("grid") with { Columns = 3 };
nested = nested.InsertNode(innerGrid, "main", groupNode.Id, 0);
var extraClock = HudLayoutNode.Create("time");
nested = nested.InsertNode(extraClock, "main", innerGrid.Id, 0);
Check(nested.GetNode(innerGrid.Id)?.Columns == 3 && nested.FindNodeParent(extraClock.Id)?.Id == innerGrid.Id &&
      nested.FindNodeCapsule(extraClock.Id)?.Id == "main" && nested.Islands[0].GetItems().Contains(extraClock.Id),
    "Containers nest without losing leaf providers and every parent and capsule can be resolved");
Check(ReferenceEquals(nested.MoveNode(groupNode.Id, "main", innerGrid.Id, 0), nested) &&
      ReferenceEquals(nested.MoveNode(innerGrid.Id, "main", innerGrid.Id, 0), nested),
    "A container cannot move into itself or its descendants");
var movedToGrid = nested.MoveNode("time", "main", innerGrid.Id, 0);
Check(movedToGrid.GetNode(innerGrid.Id)!.GetChildren().Select(node => node.Id).SequenceEqual(new[] { "time", extraClock.Id }),
    "Moving an existing leaf inserts at the supplied post-removal position");
var reorderedGrid = movedToGrid.MoveNode("time", "main", innerGrid.Id, 1);
Check(reorderedGrid.GetNode(innerGrid.Id)!.GetChildren().Select(node => node.Id).SequenceEqual(new[] { extraClock.Id, "time" }) &&
      ReferenceEquals(reorderedGrid.MoveNode("time", "main", innerGrid.Id, 1), reorderedGrid),
    "Reordering in the same parent uses post-removal indexes and an unchanged slot is a no-op");
var copiedGroup = reorderedGrid.DuplicateNode(groupNode.Id);
var copies = copiedGroup.Islands[0].Root!.GetChildren().Where(node => node.Kind == "row" && node.Padding == 4).ToArray();
Check(copies.Length == 2 && copies[1].GetChildren().Single().Kind == "grid" &&
      copies[0].DescendantsAndSelf().All(node => copies[1].DescendantsAndSelf().All(copy => copy.Id != node.Id)),
    "Duplicating a group copies its settings and nested contents with independent IDs");
var changedGroup = copiedGroup.UpdateNode(innerGrid.Id, node => node with
    { Columns = 99, Gap = double.NaN, Padding = -10, Alignment = "unknown", Sizing = "unknown", Id = "bogus", Children = [] });
var changedGrid = changedGroup.GetNode(innerGrid.Id)!;
Check(changedGrid.Columns == 12 && changedGrid.Gap == 6 && changedGrid.Padding == 0 && changedGrid.Alignment == "start" &&
      changedGrid.Sizing == "content" && changedGrid.GetChildren().Length == 2,
    "Editing layout properties validates sizes while preserving node identity and children");
var floatedGroup = changedGroup.MoveNode(groupNode.Id, null, null, 0, new(null, .7, .4));
Check(floatedGroup.Islands.Length == 2 && floatedGroup.FindNodeCapsule(groupNode.Id)?.Id != "main" &&
      floatedGroup.FindNodeCapsule(groupNode.Id)?.Position == new HudIslandPosition(null, .7, .4) &&
      floatedGroup.GetNode(groupNode.Id) == changedGroup.GetNode(groupNode.Id),
    "Detaching a nested group keeps its complete subtree and assigns one positioned capsule");
var deletedGroup = floatedGroup.RemoveNode(groupNode.Id);
Check(deletedGroup.Islands.Length == 1 && deletedGroup.GetNode(groupNode.Id) is null &&
      deletedGroup.Islands[0].GetItems().Length == changedGroup.Islands[0].GetItems().Length - changedGroup.GetNode(groupNode.Id)!.LeafIds().Count(),
    "Deleting a detached group removes its subtree and prunes only its now-empty capsule");
var emptyGroup = nested.RemoveNode(extraClock.Id);
Check(emptyGroup.GetNode(innerGrid.Id) is { IsContainer: true } empty && empty.GetChildren().Length == 0 &&
      HudCapsuleLayout.Normalize(emptyGroup).GetNode(innerGrid.Id) is not null,
    "An explicitly created empty container remains available as a drop target");
var blankTree = noWidgets.ToTree(_ => false);
Check(blankTree.HasTree && blankTree.Islands[0].Root is { IsContainer: true } && blankTree.Islands[0].GetItems().Length == 0 &&
      ReferenceEquals(HudCapsuleLayout.Normalize(blankTree), blankTree),
    "An intentionally empty tree root is stable and does not recreate widgets");
var gatheredTree = floatedGroup.ResetInstances();
Check(gatheredTree.HasTree && gatheredTree.Islands.Length == 1 &&
      gatheredTree.Islands[0].GetItems().Order().SequenceEqual(floatedGroup.Islands.SelectMany(capsule => capsule.GetItems()).Order()) &&
      gatheredTree.GetNode(innerGrid.Id)?.Columns == 12,
    "Gathering a tree preserves all leaf identities and nested group settings");

var boundedTree = blankTree;
var boundedParent = boundedTree.Islands[0].Root!.Id;
for (int depth = 1; depth < HudCapsuleLayout.MaxTreeDepth; depth++)
{
    var level = HudLayoutNode.Create("column");
    boundedTree = boundedTree.InsertNode(level, "main", boundedParent, 0);
    boundedParent = level.Id;
}
Check(boundedTree.GetNode(boundedParent) is not null &&
      ReferenceEquals(boundedTree.AddNode("time", "main", boundedParent, 0), boundedTree),
    "The nesting limit rejects additions atomically without dropping existing groups");
var sixtyFourTree = blankTree;
for (int index = 0; index < HudCapsuleLayout.MaxInstances; index++) sixtyFourTree = sixtyFourTree.AddNode("time", "main", null, int.MaxValue);
Check(sixtyFourTree.Islands[0].GetItems().Length == HudCapsuleLayout.MaxInstances &&
      ReferenceEquals(sixtyFourTree.AddNode("time", "main", null, 0), sixtyFourTree),
    "The tree accepts all 64 component instances and rejects a further leaf without mutation");
Check(ReferenceEquals(nested.InsertNode(extraClock, "main", null, 0), nested) &&
      ReferenceEquals(nested.AddNode("alien", "main", null, 0), nested) &&
      ReferenceEquals(nested.AddNode("time", "main", extraClock.Id, 0), nested),
    "Duplicate references, unknown component kinds and insertion into a leaf are rejected");
Console.WriteLine($"PASS: {count} HUD layout settings checks.");
