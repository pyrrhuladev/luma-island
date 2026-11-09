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

    // Each island has one hardware grid. Keep its items together and snap music
    // to the closer edge of that grid when the pointer is over its middle.
    public int GetInsertionIndex(string item, int insertIndex)
    {
        string[] items = GetItems();
        int index = Math.Clamp(insertIndex, 0, items.Length);
        int firstMetric = Array.FindIndex(items, key => key != item && HudCapsuleLayout.IsMetric(key));
        if (firstMetric < 0) return index;
        int afterMetrics = Array.FindLastIndex(items, key => key != item && HudCapsuleLayout.IsMetric(key)) + 1;
        if (HudCapsuleLayout.IsMetric(item)) return Math.Clamp(index, firstMetric, afterMetrics);
        if (index <= firstMetric || index >= afterMetrics) return index;
        return index - firstMetric <= afterMetrics - index ? firstMetric : afterMetrics;
    }
}

/// <summary>One ordered home for each movable block, plus the fixed main header.</summary>
public sealed record HudCapsuleLayout(ImmutableArray<HudCapsule> Islands)
{
    public const string MainId = "main";
    private const string Header = "header";
    private const string Music = "music";
    private static readonly string[] Metrics = ["cpu", "gpu", "memory"];
    private static readonly string[] MovableItems = [.. Metrics, Music];

    public static bool IsMetric(string item) => item is "cpu" or "gpu" or "memory";
    public static bool IsMovable(string item) => IsMetric(item) || item == Music;

    public static HudCapsuleLayout FromLegacy(HudLayoutSettings layout)
    {
        var settings = HudLayoutSettings.Normalize(layout with { Capsules = null });
        var main = new List<string>();
        foreach (string block in settings.BlockOrder.Split(','))
        {
            if (block == "metrics") main.AddRange(settings.MetricOrder.Split(','));
            else if (block != Music || !settings.DetachedMusic) main.Add(block);
        }

        var root = new HudCapsule(MainId, string.Join(',', main));
        return new(settings.DetachedMusic
            ? [root, new HudCapsule(Music, Music, settings.MusicPosition)]
            : [root]);
    }

    public static HudCapsuleLayout Normalize(HudCapsuleLayout layout)
    {
        var islands = layout.Islands.IsDefault ? ImmutableArray<HudCapsule>.Empty : layout.Islands;
        var main = islands.FirstOrDefault(island => CanonicalId(island?.Id) == MainId);
        var usedItems = new HashSet<string>(StringComparer.Ordinal);
        List<string> mainItems = ReadItems(main, allowHeader: true, usedItems);
        if (!mainItems.Contains(Header)) mainItems.Insert(0, Header);

        var normalized = ImmutableArray.CreateBuilder<HudCapsule>();
        normalized.Add(new(MainId, Header));
        var usedIds = new HashSet<string>(StringComparer.Ordinal) { MainId };
        foreach (HudCapsule island in islands)
        {
            string? id = CanonicalId(island?.Id);
            if (id is null || usedIds.Contains(id)) continue;
            var items = ReadItems(island, allowHeader: false, usedItems);
            if (items.Count != 0)
            {
                usedIds.Add(id);
                normalized.Add(new(id, GroupMetrics(items), HudIslandPosition.Normalize(island!.Position)));
            }
        }

        mainItems.AddRange(MovableItems.Where(usedItems.Add));
        normalized[0] = new(MainId, GroupMetrics(mainItems));
        var result = new HudCapsuleLayout(normalized.ToImmutable());
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
        return new(sourceItems.Count == 0
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

    private static List<string> ReadItems(HudCapsule? island, bool allowHeader, HashSet<string> used)
    {
        var items = new List<string>();
        foreach (string raw in island?.GetItems() ?? [])
        {
            string item = raw.ToLowerInvariant();
            if ((IsMovable(item) || allowHeader && item == Header) && used.Add(item)) items.Add(item);
        }
        return items;
    }

    private static string GroupMetrics(List<string> items)
    {
        int first = items.FindIndex(IsMetric);
        if (first < 0) return string.Join(',', items);
        string[] metrics = items.Where(IsMetric).ToArray();
        items.RemoveAll(IsMetric);
        items.InsertRange(first, metrics);
        return string.Join(',', items);
    }

    private static string? CanonicalId(string? value)
    {
        string? id = value?.Trim();
        if (id is not { Length: > 0 and <= 64 } ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')) return null;
        return id.ToLowerInvariant();
    }
}
