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
Console.WriteLine($"PASS: {count} HUD layout settings checks.");
