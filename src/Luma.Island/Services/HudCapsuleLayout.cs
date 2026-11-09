using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Luma.Island.Services;

public sealed record HudCapsule(string Id, string Items, HudIslandPosition? Position = null)
{
    private const int MaxLayoutTextLength = 256;
    private const StringSplitOptions SplitOptions = StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries;

    public string? Rows { get; init; }

    internal bool HasExplicitRows => Rows is { Length: <= MaxLayoutTextLength };

    public string[] GetItems() => SplitItems(HasExplicitRows ? Rows!.Replace(';', ',') : Items);

    public string[][] GetRows() => HasExplicitRows
        ? Rows!.Split(';', SplitOptions).Select(SplitItems).Where(row => row.Length > 0).ToArray()
        : GetItems().GroupBy(HudCapsuleLayout.Group).Select(group => group.ToArray()).ToArray();

    private static string[] SplitItems(string? items) => items is { Length: <= MaxLayoutTextLength }
        ? items.Split(',', SplitOptions)
        : [];

    // Each visual group occupies one row or grid. Snap unrelated tiles to its edge.
    public int GetInsertionIndex(string item, int insertIndex)
    {
        string[] items = GetItems();
        int index = Math.Clamp(insertIndex, 0, items.Length);
        if (HasExplicitRows) return index;
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
        var (mainItems, mainRows) = ReadCapsule(main, expandLegacyHeader: true, usedItems);
        var accepted = new List<(string Id, List<string> Items, List<string[]>? Rows, HudIslandPosition? Position)>
        {
            (MainId, mainItems, mainRows, null)
        };
        var usedIds = new HashSet<string>(StringComparer.Ordinal) { MainId };
        foreach (HudCapsule island in islands)
        {
            string? id = CanonicalId(island?.Id);
            if (id is null || usedIds.Contains(id)) continue;
            var (items, rows) = ReadCapsule(island, expandLegacyHeader: false, usedItems);
            if (items.Count != 0)
            {
                usedIds.Add(id);
                accepted.Add((id, items, rows, HudIslandPosition.Normalize(island!.Position)));
            }
        }

        mainItems.AddRange(DefaultItems.Where(usedItems.Add));
        var normalized = ImmutableArray.CreateBuilder<HudCapsule>(accepted.Count);
        foreach (var (id, items, rows, position) in accepted)
        {
            // Older layouts implicitly drew mini charts and volume with their owners.
            foreach (var (metric, chart) in Charts)
                if (items.Contains(metric) && usedItems.Add(chart)) items.Add(chart);
            if (id == MainId && usedItems.Add("frame-time")) items.Add("frame-time");
            if (items.Contains("music") && usedItems.Add("volume")) items.Add("volume");
            if (id == MainId && usedItems.Add("date")) items.Add("date");
            if (rows is null)
            {
                normalized.Add(new(id, string.Join(',', items.GroupBy(Group).SelectMany(group => group)), position));
            }
            else
            {
                var added = items.Skip(rows.Sum(row => row.Length)).GroupBy(Group).Select(group => group.ToArray());
                normalized.Add(WithRows(new(id, string.Empty, position), rows.Concat(added)));
            }
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
            var moved = Islands.SetItem(sourceIndex, WithoutItem(source, item, sourceItems))
                .Add(new(Guid.NewGuid().ToString("N"), item, HudIslandPosition.Normalize(position)));
            return new(moved);
        }

        int targetIndex = FindIsland(destinationId);
        if (targetIndex < 0) return this;
        HudCapsule target = Islands[targetIndex];
        int index = target.GetInsertionIndex(item, insertIndex);
        if (target.HasExplicitRows)
        {
            string[][] rows = target.GetRows();
            int row = 0;
            while (row < rows.Length - 1 && index >= rows[row].Length)
                index -= rows[row++].Length;
            return MoveToRow(item, destinationId, row, index, newRow: false);
        }
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
            : reassigned.SetItem(sourceIndex, WithoutItem(source, item, sourceItems)));
    }

    /// <summary>Move into a row or insert a new row before the supplied destination index.</summary>
    public HudCapsuleLayout MoveToRow(string item, string destinationId, int rowIndex, int columnIndex, bool newRow)
    {
        if (!IsMovable(item) || Islands.IsDefaultOrEmpty) return this;
        int sourceIndex = FindSource(item);
        int targetIndex = FindIsland(destinationId);
        if (sourceIndex < 0 || targetIndex < 0) return this;

        HudCapsule source = Islands[sourceIndex];
        HudCapsule target = Islands[targetIndex];
        bool sameCapsule = sourceIndex == targetIndex;
        var sourceRows = source.GetRows().Select(row => row.ToList()).ToList();
        var targetRows = sameCapsule ? sourceRows : target.GetRows().Select(row => row.ToList()).ToList();
        var sourceRow = sourceRows.First(row => row.Contains(item));
        int sourceColumn = sourceRow.IndexOf(item);

        // Keep the source row in the list until after insertion so the destination
        // row index still describes the pre-removal layout seen by the pointer.
        if (newRow || targetRows.Count == 0)
        {
            sourceRow.RemoveAt(sourceColumn);
            targetRows.Insert(Math.Clamp(rowIndex, 0, targetRows.Count), [item]);
        }
        else
        {
            var targetRow = targetRows[Math.Clamp(rowIndex, 0, targetRows.Count - 1)];
            int index = Math.Clamp(columnIndex, 0, targetRow.Count);
            if (ReferenceEquals(sourceRow, targetRow) && sourceColumn < index) index--;
            sourceRow.RemoveAt(sourceColumn);
            targetRow.Insert(index, item);
        }
        sourceRows.RemoveAll(row => row.Count == 0);

        HudCapsule updatedTarget = WithRows(target, targetRows);
        if (sameCapsule)
        {
            if (updatedTarget.Rows == EncodeRows(target.GetRows())) return this;
            return new(Islands.SetItem(targetIndex, updatedTarget));
        }

        var reassigned = Islands.SetItem(targetIndex, updatedTarget);
        if (sourceRows.Count == 0 && source.Id != MainId) return new(reassigned.RemoveAt(sourceIndex));
        HudCapsule updatedSource = source.HasExplicitRows
            ? WithRows(source, sourceRows)
            : source with { Items = string.Join(',', sourceRows.SelectMany(row => row)) };
        return new(reassigned.SetItem(sourceIndex, updatedSource));
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

    private static HudCapsule WithoutItem(HudCapsule source, string item, List<string> remaining) => source.HasExplicitRows
        ? WithRows(source, source.GetRows().Select(row => row.Where(value => value != item).ToArray()).Where(row => row.Length > 0))
        : source with { Items = string.Join(',', remaining) };

    private static HudCapsule WithRows(HudCapsule capsule, IEnumerable<IEnumerable<string>> rows)
    {
        string encoded = EncodeRows(rows);
        return capsule with { Items = encoded.Replace(';', ','), Rows = encoded };
    }

    private static string EncodeRows(IEnumerable<IEnumerable<string>> rows) =>
        string.Join(';', rows.Select(row => string.Join(',', row)));

    private static (List<string> Items, List<string[]>? Rows) ReadCapsule(HudCapsule? island, bool expandLegacyHeader, HashSet<string> used)
    {
        if (island?.HasExplicitRows != true) return (ReadItems(island?.GetItems() ?? [], expandLegacyHeader, used), null);

        var rows = new List<string[]>();
        var items = new List<string>();
        foreach (string[] row in island.GetRows())
        {
            var accepted = ReadItems(row, expandLegacyHeader, used);
            if (accepted.Count == 0) continue;
            rows.Add(accepted.ToArray());
            items.AddRange(accepted);
        }
        return (items, rows);
    }

    private static List<string> ReadItems(IEnumerable<string> input, bool expandLegacyHeader, HashSet<string> used)
    {
        var items = new List<string>();
        foreach (string raw in input)
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
