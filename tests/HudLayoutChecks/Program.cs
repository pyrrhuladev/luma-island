using Luma.Island.Services;

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
Console.WriteLine($"PASS: {count} HUD layout settings checks.");
