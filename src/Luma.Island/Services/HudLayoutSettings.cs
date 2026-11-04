using System;
using System.Collections.Generic;

namespace Luma.Island.Services;

/// <summary>Value-only HUD arrangement; defaults reproduce the original layout.</summary>
public sealed record HudLayoutSettings(
    string BlockOrder = "header,metrics,music",
    string MetricOrder = "cpu,gpu,memory",
    int MetricColumns = 3,
    double TextScale = 1,
    double Spacing = 6)
{
    public static HudLayoutSettings Default { get; } = new();
    public const double MinTextScale = .8;
    public const double MaxTextScale = 1.3;
    public const double MinSpacing = 2;
    public const double MaxSpacing = 12;

    public static HudLayoutSettings Normalize(HudLayoutSettings? settings)
    {
        settings ??= Default;
        return settings with
        {
            BlockOrder = NormalizeOrder(settings.BlockOrder, "header", "metrics", "music"),
            MetricOrder = NormalizeOrder(settings.MetricOrder, "cpu", "gpu", "memory"),
            MetricColumns = Math.Clamp(settings.MetricColumns, 1, 3),
            TextScale = double.IsFinite(settings.TextScale) ? Math.Clamp(settings.TextScale, MinTextScale, MaxTextScale) : 1,
            Spacing = double.IsFinite(settings.Spacing) ? Math.Clamp(settings.Spacing, MinSpacing, MaxSpacing) : 6
        };
    }

    private static string NormalizeOrder(string? order, params string[] known)
    {
        var result = new List<string>(known.Length);
        if (order is { Length: <= 256 })
            foreach (string raw in order.Split(','))
            {
                string value = raw.Trim().ToLowerInvariant();
                if (Array.IndexOf(known, value) >= 0 && !result.Contains(value)) result.Add(value);
            }
        foreach (string value in known) if (!result.Contains(value)) result.Add(value);
        return string.Join(',', result);
    }
}
