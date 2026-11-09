using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Luma.Island.Services;

public sealed record HudCapsule(string Id, string Items, HudIslandPosition? Position = null)
{
    public string[] GetItems() => Items is { Length: <= 256 }
        ? Items.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        : [];

    // Each visual group occupies one row or grid. Snap unrelated tiles to its edge.
    public int GetInsertionIndex(string item, int insertIndex)
    {
        string[] items = GetItems();
        int index = Math.Clamp(insertIndex, 0, items.Length);
        string group = HudCapsuleLayout.Group(item);
        int first = Array.FindIndex(items, key => key != item && HudCapsuleLayout.Group(key) == group);
        if (first >= 0)
        {
            int after = Array.FindLastIndex(items, key => key != item && HudCapsuleLayout.Group(key) == group) + 1;
            return Math.Clamp(index, first, after);
        }

        if (index == 0 || index == items.Length) return index;
        string adjacentGroup = HudCapsuleLayout.Group(items[index]);
        if (adjacentGroup != HudCapsuleLayout.Group(items[index - 1])) return index;
        first = Array.FindIndex(items, key => HudCapsuleLayout.Group(key) == adjacentGroup);
        int end = Array.FindLastIndex(items, key => HudCapsuleLayout.Group(key) == adjacentGroup) + 1;
        return index - first <= end - index ? first : end;
    }
}

/// <summary>One ordered home for each HUD tile, with the primary window retained when empty.</summary>
public sealed record HudCapsuleLayout(ImmutableArray<HudCapsule> Islands)
{
    public const string MainId = "main";
    private static readonly string[] HeaderItems = ["fps", "low", "time", "battery"];
    private static readonly string[] DefaultItems = [.. HeaderItems, "cpu", "gpu", "memory", "music"];
    private static readonly (string Metric, string Chart)[] Charts =
        [("cpu", "cpu-chart"), ("gpu", "gpu-chart"), ("memory", "memory-chart"), ("fps", "fps-chart")];

    public static bool IsMetric(string item) => item is "cpu" or "gpu" or "memory";
    public static bool IsHeader(string item) => item is "fps" or "low" or "time" or "battery";
    public static bool IsChart(string item) => item is "cpu-chart" or "gpu-chart" or "memory-chart" or "fps-chart";
    public static bool IsMovable(string item) => IsMetric(item) || IsHeader(item) || IsChart(item) ||
        item is "music" or "frame-time" or "volume" or "date";

    public static string Group(string item) => item switch
    {
        "fps" or "low" or "time" or "battery" => "header",
        "cpu" or "gpu" or "memory" => "metrics",
        "cpu-chart" or "gpu-chart" or "memory-chart" or "fps-chart" => "charts",
        _ => item
    };

    public static HudCapsuleLayout FromLegacy(HudLayoutSettings layout)
    {
        var settings = HudLayoutSettings.Normalize(layout with { Capsules = null });
        var main = new List<string>();
        foreach (string block in settings.BlockOrder.Split(','))
        {
            if (block == "metrics") main.AddRange(settings.MetricOrder.Split(','));
            else if (block == "header") main.AddRange(HeaderItems);
            else if (!settings.DetachedMusic) main.Add("music");
        }

        var root = new HudCapsule(MainId, string.Join(',', main));
        return Normalize(new(settings.DetachedMusic
            ? [root, new HudCapsule("music", "music", settings.MusicPosition)]
            : [root]));
    }

    public static HudCapsuleLayout Normalize(HudCapsuleLayout layout)
    {
        var islands = layout.Islands.IsDefault ? ImmutableArray<HudCapsule>.Empty : layout.Islands;
        var main = islands.FirstOrDefault(island => CanonicalId(island?.Id) == MainId);
        var usedItems = new HashSet<string>(StringComparer.Ordinal);
        List<string> mainItems = ReadItems(main, expandLegacyHeader: true, usedItems);
        var accepted = new List<(string Id, List<string> Items, HudIslandPosition? Position)>
        {
            (MainId, mainItems, null)
        };
        var usedIds = new HashSet<string>(StringComparer.Ordinal) { MainId };
        foreach (HudCapsule island in islands)
        {
            string? id = CanonicalId(island?.Id);
            if (id is null || usedIds.Contains(id)) continue;
            var items = ReadItems(island, expandLegacyHeader: false, usedItems);
            if (items.Count != 0)
            {
                usedIds.Add(id);
                accepted.Add((id, items, HudIslandPosition.Normalize(island!.Position)));
            }
        }

        mainItems.AddRange(DefaultItems.Where(usedItems.Add));
        var normalized = ImmutableArray.CreateBuilder<HudCapsule>(accepted.Count);
        foreach (var (id, items, position) in accepted)
        {
            // Older layouts implicitly drew mini charts and volume with their owners.
            foreach (var (metric, chart) in Charts)
                if (items.Contains(metric) && usedItems.Add(chart)) items.Add(chart);
            if (id == MainId && usedItems.Add("frame-time")) items.Add("frame-time");
            if (items.Contains("music") && usedItems.Add("volume")) items.Add("volume");
            if (id == MainId && usedItems.Add("date")) items.Add("date");
            normalized.Add(new(id, string.Join(',', items.GroupBy(Group).SelectMany(group => group)), position));
        }
        var result = new HudCapsuleLayout(normalized.MoveToImmutable());
        return layout.Equals(result) ? layout : result;
    }

    public HudCapsuleLayout Move(string item, string? destinationId, int insertIndex, HudIslandPosition? position = null)
    {
        if (!IsMovable(item) || Islands.IsDefaultOrEmpty) return this;
        int sourceIndex = FindSource(item);
        if (sourceIndex < 0) return this;

        HudCapsule source = Islands[sourceIndex];
        var sourceItems = source.GetItems().ToList();
        int itemIndex = sourceItems.IndexOf(item);
        sourceItems.RemoveAt(itemIndex);
        if (destinationId is null)
        {
            if (position is null) return this;
            if (source.Id != MainId && sourceItems.Count == 0) return WithPosition(source.Id, position);
            var moved = Islands.SetItem(sourceIndex, source with { Items = string.Join(',', sourceItems) })
                .Add(new(Guid.NewGuid().ToString("N"), item, HudIslandPosition.Normalize(position)));
            return new(moved);
        }

        int targetIndex = FindIsland(destinationId);
        if (targetIndex < 0) return this;
        HudCapsule target = Islands[targetIndex];
        int index = target.GetInsertionIndex(item, insertIndex);
        if (sourceIndex == targetIndex)
        {
            if (itemIndex < index) index--;
            if (itemIndex == index) return this;
            sourceItems.Insert(index, item);
            return new(Islands.SetItem(sourceIndex, source with { Items = string.Join(',', sourceItems) }));
        }

        var targetItems = target.GetItems().ToList();
        targetItems.Insert(index, item);
        var reassigned = Islands.SetItem(targetIndex, target with { Items = string.Join(',', targetItems) });
        return new(sourceItems.Count == 0 && source.Id != MainId
            ? reassigned.RemoveAt(sourceIndex)
            : reassigned.SetItem(sourceIndex, source with { Items = string.Join(',', sourceItems) }));
    }

    public HudCapsuleLayout WithPosition(string id, HudIslandPosition position)
    {
        if (id == MainId || Islands.IsDefaultOrEmpty) return this;
        int index = FindIsland(id);
        if (index < 0) return this;
        var normalized = HudIslandPosition.Normalize(position);
        HudCapsule island = Islands[index];
        return island.Position == normalized ? this : new(Islands.SetItem(index, island with { Position = normalized }));
    }

    public bool Equals(HudCapsuleLayout? other) =>
        other is not null && Islands.AsSpan().SequenceEqual(other.Islands.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (HudCapsule island in Islands.AsSpan()) hash.Add(island);
        return hash.ToHashCode();
    }

    private int FindSource(string item)
    {
        for (int index = 0; index < Islands.Length; index++)
            if (Array.IndexOf(Islands[index].GetItems(), item) >= 0) return index;
        return -1;
    }

    private int FindIsland(string id)
    {
        for (int index = 0; index < Islands.Length; index++)
            if (Islands[index].Id == id) return index;
        return -1;
    }

    private static List<string> ReadItems(HudCapsule? island, bool expandLegacyHeader, HashSet<string> used)
    {
        var items = new List<string>();
        foreach (string raw in island?.GetItems() ?? [])
        {
            string item = raw.ToLowerInvariant();
            if (expandLegacyHeader && item == "header") items.AddRange(HeaderItems.Where(used.Add));
            else if (IsMovable(item) && used.Add(item)) items.Add(item);
        }
        return items;
    }

    private static string? CanonicalId(string? value)
    {
        string? id = value?.Trim();
        if (id is not { Length: > 0 and <= 64 } ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')) return null;
        return id.ToLowerInvariant();
    }
}
