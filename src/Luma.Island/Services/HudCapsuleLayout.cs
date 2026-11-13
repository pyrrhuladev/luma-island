using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;

namespace Luma.Island.Services;

public sealed record HudCapsule(string Id, string Items, HudIslandPosition? Position = null)
{
    internal const int MaxLegacyTextLength = 256;
    private const int MaxLayoutTextLength = HudCapsuleLayout.MaxInstances * 48;
    private const StringSplitOptions SplitOptions = StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries;

    public string? Rows { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HudLayoutNode? Root { get; init; }

    internal bool HasExplicitRows => Rows is { Length: <= MaxLayoutTextLength };

    public string[] GetItems() => Root is { } root ? root.LeafIds().ToArray() : SplitItems(HasExplicitRows ? Rows!.Replace(';', ',') : Items);

    public string[][] GetRows() => Root is { } root ? root.GetChildren().Select(child => child.LeafIds().ToArray()).Where(row => row.Length > 0).ToArray() : HasExplicitRows
        ? Rows!.Split(';', SplitOptions).Select(SplitItems).Where(row => row.Length > 0).ToArray()
        : GetItems().GroupBy(HudCapsuleLayout.Group).Select(group => group.ToArray()).ToArray();

    internal static string[] SplitItems(string? items) => items is { Length: <= MaxLayoutTextLength }
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
public sealed partial record HudCapsuleLayout(ImmutableArray<HudCapsule> Islands)
{
    public const string MainId = "main";
    public const int MaxInstances = 64;
    private static readonly string[] HeaderItems = ["fps", "low", "time", "battery"];
    private static readonly string[] DefaultItems = [.. HeaderItems, "cpu", "gpu", "memory", "music"];
    private static readonly (string Metric, string Chart)[] Charts =
        [("cpu", "cpu-chart"), ("gpu", "gpu-chart"), ("memory", "memory-chart"), ("fps", "fps-chart")];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasInstances { get; init; }

    [JsonIgnore]
    public bool CanAddInstance => HasInstances && !Islands.IsDefaultOrEmpty &&
        Islands.Sum(island => island.GetItems().Length) < MaxInstances;

    public static string KindOf(string? id)
    {
        if (id is null) return string.Empty;
        int separator = id.IndexOf('@');
        return separator < 0 ? id : id[..separator];
    }

    public static bool IsMetric(string item) => KindGroup(KindOf(item)) == "metrics";
    public static bool IsHeader(string item) => KindGroup(KindOf(item)) == "header";
    public static bool IsChart(string item) => KindGroup(KindOf(item)) == "charts";
    public static bool IsMovable(string item) => IsKind(KindOf(item)) && IsInstanceId(item);

    private static bool IsKind(string kind) => KindGroup(kind) is not null;

    private static bool IsInstanceId(string item)
    {
        int separator = item.IndexOf('@');
        return separator < 0 || item.Length - separator - 1 == 32 &&
            Guid.TryParseExact(item.AsSpan(separator + 1), "N", out Guid id) && id != Guid.Empty;
    }

    public static string Group(string item)
    {
        string kind = KindOf(item);
        return KindGroup(kind) ?? kind;
    }

    private static string? KindGroup(string kind) => kind switch
    {
        "fps" or "low" or "time" or "battery" => "header",
        "cpu" or "gpu" or "memory" or "cpu-temp" or "gpu-temp" or "memory-temp" => "metrics",
        "cpu-chart" or "gpu-chart" or "memory-chart" or "fps-chart" => "charts",
        "music" or "frame-time" or "volume" or "date" => kind,
        _ => null
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
        if (layout.HasTree) return NormalizeTree(layout);
        var islands = layout.Islands.IsDefault ? ImmutableArray<HudCapsule>.Empty : layout.Islands;
        var main = islands.FirstOrDefault(island => CanonicalId(island?.Id) == MainId);
        var usedItems = new HashSet<string>(StringComparer.Ordinal);
        var (mainItems, mainRows) = ReadCapsule(main, expandLegacyHeader: !layout.HasInstances, layout.HasInstances, usedItems);
        var accepted = new List<(string Id, List<string> Items, List<string[]>? Rows, HudIslandPosition? Position)>
        {
            (MainId, mainItems, mainRows, null)
        };
        var usedIds = new HashSet<string>(StringComparer.Ordinal) { MainId };
        foreach (HudCapsule island in islands)
        {
            string? id = CanonicalId(island?.Id);
            if (id is null || usedIds.Contains(id)) continue;
            var (items, rows) = ReadCapsule(island, expandLegacyHeader: false, layout.HasInstances, usedItems);
            if (items.Count != 0)
            {
                usedIds.Add(id);
                accepted.Add((id, items, rows, HudIslandPosition.Normalize(island!.Position)));
            }
        }

        if (!layout.HasInstances) mainItems.AddRange(DefaultItems.Where(usedItems.Add));
        var normalized = ImmutableArray.CreateBuilder<HudCapsule>(accepted.Count);
        foreach (var (id, items, rows, position) in accepted)
        {
            if (!layout.HasInstances)
            {
                // Older layouts implicitly drew mini charts and volume with their owners.
                foreach (var (metric, chart) in Charts)
                    if (items.Contains(metric) && usedItems.Add(chart)) items.Add(chart);
                if (id == MainId && usedItems.Add("frame-time")) items.Add("frame-time");
                if (items.Contains("music") && usedItems.Add("volume")) items.Add("volume");
                if (id == MainId && usedItems.Add("date")) items.Add("date");
            }
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
        var result = layout with { Islands = normalized.MoveToImmutable() };
        return layout.Equals(result) ? layout : result;
    }

    public HudCapsuleLayout ToInstances(Func<string, bool> isVisible)
    {
        if (HasInstances) return this;
        IEnumerable<string> VisibleWidgets(string item)
        {
            if (isVisible(item)) yield return item;
            if (item is "cpu" or "gpu" or "memory" && isVisible(item + "-temp")) yield return item + "-temp";
        }

        var islands = Normalize(this).Islands.Select(capsule => capsule.HasExplicitRows
            ? WithRows(capsule, capsule.GetRows().Select(row => row.SelectMany(VisibleWidgets).ToArray()).Where(row => row.Length > 0))
            : capsule with { Items = string.Join(',', capsule.GetItems().SelectMany(VisibleWidgets)) });
        return Normalize(new(islands.ToImmutableArray()) { HasInstances = true });
    }

    public HudCapsuleLayout AddInstance(string kind, string? destinationId = null)
    {
        if (HasTree) return AddNode(kind, destinationId ?? MainId, null, int.MaxValue);
        if (!CanAddInstance || !IsKind(kind)) return this;
        int index = FindIsland(destinationId ?? MainId);
        if (index < 0) return this;

        HudCapsule target = Islands[index];
        string item = $"{kind}@{Guid.NewGuid():N}";
        string group = Group(kind);
        var rows = target.GetRows().ToList();
        int row = rows.FindLastIndex(candidate => candidate.All(id => Group(id) == group));
        if (row >= 0 && rows[row].Length < WidgetsPerRow(group)) rows[row] = [.. rows[row], item];
        else rows.Insert(row >= 0 ? row + 1 : rows.Count, [item]);
        return this with { Islands = Islands.SetItem(index, WithRows(target, rows)) };
    }

    public HudCapsuleLayout RemoveInstance(string item)
    {
        if (HasTree) return RemoveNode(item);
        if (!HasInstances || Islands.IsDefaultOrEmpty) return this;
        int index = FindSource(item);
        if (index < 0) return this;
        HudCapsule source = Islands[index];
        var items = source.GetItems().Where(id => id != item).ToList();
        return this with
        {
            Islands = items.Count == 0 && source.Id != MainId
                ? Islands.RemoveAt(index)
                : Islands.SetItem(index, WithoutItem(source, item, items))
        };
    }

    public HudCapsuleLayout ResetInstances()
    {
        if (HasTree) return GatherTree();
        if (!HasInstances || Islands.IsDefaultOrEmpty) return this;
        var rows = Islands.SelectMany(island => island.GetItems()).GroupBy(Group)
            .SelectMany(group => group.Chunk(WidgetsPerRow(group.Key)));
        var reset = this with { Islands = [WithRows(new(MainId, string.Empty), rows)] };
        return Equals(reset) ? this : reset;
    }

    private static int WidgetsPerRow(string group) => group switch
    {
        "header" => 4,
        "metrics" => 3,
        "charts" => 2,
        _ => 1
    };

    public HudCapsuleLayout Move(string item, string? destinationId, int insertIndex, HudIslandPosition? position = null)
    {
        if (HasTree) return MoveNode(item, destinationId, null, insertIndex, position);
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
            return this with { Islands = moved };
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
            return this with { Islands = Islands.SetItem(sourceIndex, source with { Items = string.Join(',', sourceItems) }) };
        }

        var targetItems = target.GetItems().ToList();
        targetItems.Insert(index, item);
        var reassigned = Islands.SetItem(targetIndex, target with { Items = string.Join(',', targetItems) });
        return this with
        {
            Islands = sourceItems.Count == 0 && source.Id != MainId
                ? reassigned.RemoveAt(sourceIndex)
                : reassigned.SetItem(sourceIndex, WithoutItem(source, item, sourceItems))
        };
    }

    /// <summary>Move into a row or insert a new row before the supplied destination index.</summary>
    public HudCapsuleLayout MoveToRow(string item, string destinationId, int rowIndex, int columnIndex, bool newRow)
    {
        if (HasTree)
        {
            var root = Islands.FirstOrDefault(capsule => capsule.Id == destinationId)?.Root;
            if (root is null) return this;
            var rows = root.GetChildren();
            if (newRow || rows.Length == 0)
            {
                var row = HudLayoutNode.Create("row");
                return InsertNode(row, destinationId, root.Id, rowIndex).MoveNode(item, destinationId, row.Id, 0);
            }
            var targetNode = rows[Math.Clamp(rowIndex, 0, rows.Length - 1)];
            return MoveNode(item, destinationId, targetNode.IsContainer ? targetNode.Id : root.Id, columnIndex);
        }
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
            return this with { Islands = Islands.SetItem(targetIndex, updatedTarget) };
        }

        var reassigned = Islands.SetItem(targetIndex, updatedTarget);
        if (sourceRows.Count == 0 && source.Id != MainId) return this with { Islands = reassigned.RemoveAt(sourceIndex) };
        HudCapsule updatedSource = source.HasExplicitRows
            ? WithRows(source, sourceRows)
            : source with { Items = string.Join(',', sourceRows.SelectMany(row => row)) };
        return this with { Islands = reassigned.SetItem(sourceIndex, updatedSource) };
    }

    public HudCapsuleLayout WithPosition(string id, HudIslandPosition position)
    {
        if (id == MainId || Islands.IsDefaultOrEmpty) return this;
        int index = FindIsland(id);
        if (index < 0) return this;
        var normalized = HudIslandPosition.Normalize(position);
        HudCapsule island = Islands[index];
        return island.Position == normalized ? this : this with { Islands = Islands.SetItem(index, island with { Position = normalized }) };
    }

    public bool Equals(HudCapsuleLayout? other) =>
        other is not null && HasInstances == other.HasInstances && Islands.AsSpan().SequenceEqual(other.Islands.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(HasInstances);
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

    private static (List<string> Items, List<string[]>? Rows) ReadCapsule(HudCapsule? island, bool expandLegacyHeader,
        bool hasInstances, HashSet<string> used)
    {
        if (island is null) return ([], null);
        if (island.HasExplicitRows && (hasInstances || island.Rows!.Length <= HudCapsule.MaxLegacyTextLength))
        {
            var rows = new List<string[]>();
            var items = new List<string>();
            foreach (string[] row in island.GetRows())
            {
                var accepted = ReadItems(row, expandLegacyHeader, hasInstances, used);
                if (accepted.Count == 0) continue;
                rows.Add(accepted.ToArray());
                items.AddRange(accepted);
            }
            return (items, rows);
        }

        string[] input = hasInstances || island.Items is { Length: <= HudCapsule.MaxLegacyTextLength }
            ? HudCapsule.SplitItems(island.Items)
            : [];
        return (ReadItems(input, expandLegacyHeader, hasInstances, used), null);
    }

    private static List<string> ReadItems(IEnumerable<string> input, bool expandLegacyHeader, bool hasInstances, HashSet<string> used)
    {
        var items = new List<string>();
        foreach (string raw in input)
        {
            string item = raw.ToLowerInvariant();
            if (hasInstances && used.Count >= MaxInstances) break;
            if (expandLegacyHeader && item == "header") items.AddRange(HeaderItems.Where(used.Add));
            else if ((hasInstances ? IsMovable(item) : IsKind(item) && item is not ("cpu-temp" or "gpu-temp" or "memory-temp")) &&
                     used.Add(item)) items.Add(item);
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
