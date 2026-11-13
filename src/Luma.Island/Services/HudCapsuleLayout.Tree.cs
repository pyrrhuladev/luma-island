using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;

namespace Luma.Island.Services;

public sealed partial record HudCapsuleLayout
{
    public const int MaxTreeDepth = 8;
    public const int MaxTreeNodes = 128;
    [JsonIgnore] public bool HasTree => !Islands.IsDefaultOrEmpty && Islands.Any(capsule => capsule?.Root is not null);

    public HudLayoutNode? GetNode(string id) => Islands.IsDefaultOrEmpty ? null : Islands
        .SelectMany(capsule => capsule.Root?.DescendantsAndSelf() ?? []).FirstOrDefault(node => node.Id == id);

    public HudCapsule? FindNodeCapsule(string id) => Islands.IsDefaultOrEmpty ? null : Islands
        .FirstOrDefault(capsule => capsule.Root?.DescendantsAndSelf().Any(node => node.Id == id) == true);

    public HudLayoutNode? FindNodeParent(string id) => Islands.IsDefaultOrEmpty ? null : Islands
        .SelectMany(capsule => capsule.Root?.DescendantsAndSelf() ?? [])
        .FirstOrDefault(node => node.GetChildren().Any(child => child.Id == id));

    public HudCapsuleLayout ToTree(Func<string, bool> isVisible, int metricColumns = 3, double spacing = 6)
    {
        if (HasTree) return this;
        var instances = ToInstances(isVisible);
        var converted = instances.Islands.Select(capsule =>
        {
            var children = capsule.GetRows().SelectMany(row =>
                row.All(IsChart) ? row.Chunk(2) : row.All(IsMetric) ? row.Chunk(Math.Clamp(metricColumns, 1, 3)) : [row])
                .Select(row => row.Length == 1 ? HudLayoutNode.Create(KindOf(row[0]), row[0]) :
                    HudLayoutNode.Create("row") with
                    {
                        Gap = spacing, Children = row.Select(id => HudLayoutNode.Create(KindOf(id), id)).ToImmutableArray()
                    }).ToImmutableArray();
            return capsule with
            {
                Root = HudLayoutNode.Create("column") with { Gap = spacing, Children = children }, Rows = null,
                Items = string.Join(',', children.SelectMany(node => node.LeafIds()))
            };
        }).ToImmutableArray();
        return Normalize(instances with { Islands = converted, HasInstances = true });
    }

    public HudCapsuleLayout AddNode(string kind, string targetCapsuleId, string? parentNodeId, int index, string? instanceId = null) =>
        InsertNode(HudLayoutNode.Create(kind, instanceId), targetCapsuleId, parentNodeId, index);

    public HudCapsuleLayout InsertNode(HudLayoutNode node, string targetCapsuleId, string? parentNodeId, int index)
    {
        if (!HasTree || node is null || GetNode(node.Id) is not null) return this;
        int target = FindIsland(targetCapsuleId);
        if (target < 0 || Islands[target].Root is not { } root) return this;
        string parentId = parentNodeId ?? root.Id;
        var parent = root.DescendantsAndSelf().FirstOrDefault(candidate => candidate.Id == parentId);
        if (parent?.IsContainer != true || !CanInsertTree(node, parent)) return this;
        var updated = RewriteTree(root, parentId, current => current with
        {
            Children = current.GetChildren().Insert(Math.Clamp(index, 0, current.GetChildren().Length), node)
        });
        var result = this with { Islands = Islands.SetItem(target, TreeCapsule(Islands[target], updated)) };
        var normalized = Normalize(result);
        return normalized.GetNode(node.Id) is null ? this : normalized;
    }

    public HudCapsuleLayout MoveNode(string id, string? targetCapsuleId, string? parentNodeId, int index, HudIslandPosition? position = null)
    {
        if (!HasTree || GetNode(id) is not { } node || FindNodeCapsule(id) is not { } source) return this;
        if (targetCapsuleId is null)
        {
            if (position is null) return this;
            if (source.Root?.Id == id) return WithPosition(source.Id, position);
            var removed = RemoveNode(id);
            var island = new HudCapsule(Guid.NewGuid().ToString("N"), string.Empty, HudIslandPosition.Normalize(position))
            {
                Root = HudLayoutNode.Create("column") with { Children = [node] }
            };
            var detached = Normalize(removed with { Islands = removed.Islands.Add(TreeCapsule(island, island.Root)) });
            return detached.GetNode(id) == node ? detached : this;
        }

        int target = FindIsland(targetCapsuleId);
        if (target < 0 || Islands[target].Root is not { } targetRoot) return this;
        string parentId = parentNodeId ?? targetRoot.Id;
        var parent = targetRoot.DescendantsAndSelf().FirstOrDefault(candidate => candidate.Id == parentId);
        if (parent?.IsContainer != true || node.DescendantsAndSelf().Any(candidate => candidate.Id == parentId)) return this;
        var oldParent = FindNodeParent(id);
        int insertion = Math.Clamp(index, 0, parent.GetChildren().Count(child => child.Id != id));
        if (oldParent?.Id == parentId)
        {
            int oldIndex = Array.FindIndex(parent.GetChildren().ToArray(), child => child.Id == id);
            if (oldIndex == insertion) return this;
        }
        var reduced = RemoveNode(id);
        // Removing the sole child may prune the source capsule. Resolve by identity again.
        var result = reduced.InsertNode(node, targetCapsuleId, parentId, insertion);
        return result.GetNode(id) is null ? this : result;
    }

    public HudCapsuleLayout RemoveNode(string id)
    {
        var source = FindNodeCapsule(id);
        if (source?.Root is not { } root) return this;
        int index = FindIsland(source.Id);
        var updated = root.Id == id ? HudLayoutNode.Create("column") : RemoveTreeChild(root, id);
        if (updated == root) return this;
        var islands = updated.GetChildren().Length == 0 && source.Id != MainId
            ? Islands.RemoveAt(index) : Islands.SetItem(index, TreeCapsule(source, updated));
        return this with { Islands = islands };
    }

    public HudCapsuleLayout DuplicateNode(string id)
    {
        var node = GetNode(id);
        var parent = FindNodeParent(id);
        var capsule = FindNodeCapsule(id);
        if (node is null || parent is null || capsule is null) return this;
        int index = Array.FindIndex(parent.GetChildren().ToArray(), child => child.Id == id);
        return InsertNode(CopyTree(node), capsule.Id, parent.Id, index + 1);
    }

    public HudCapsuleLayout UpdateNode(string id, Func<HudLayoutNode, HudLayoutNode> update)
    {
        var capsule = FindNodeCapsule(id);
        if (capsule?.Root is not { } root || GetNode(id) is not { } node) return this;
        var changed = update(node) with { Id = node.Id, Kind = node.Kind, Children = node.Children };
        if (changed == node) return this;
        var updated = RewriteTree(root, id, _ => changed);
        return Normalize(this with { Islands = Islands.SetItem(FindIsland(capsule.Id), TreeCapsule(capsule, updated)) });
    }

    private HudCapsuleLayout GatherTree()
    {
        var root = Islands.First(capsule => capsule.Id == MainId).Root!;
        var children = Islands.SelectMany(capsule => capsule.Root!.GetChildren()).ToImmutableArray();
        var result = this with { Islands = [TreeCapsule(Islands[0], root with { Children = children })] };
        return result == this ? this : result;
    }

    private bool CanInsertTree(HudLayoutNode node, HudLayoutNode parent)
    {
        var all = Islands.SelectMany(capsule => capsule.Root?.DescendantsAndSelf() ?? []).ToArray();
        var additions = node.DescendantsAndSelf().ToArray();
        if (all.Length + additions.Length > MaxTreeNodes || all.Count(candidate => !candidate.IsContainer) + additions.Count(candidate => !candidate.IsContainer) > MaxInstances)
            return false;
        if (additions.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != additions.Length ||
            additions.Any(candidate => !(candidate.IsContainer || IsKind(candidate.Kind)) || KindOf(candidate.Id) != candidate.Kind ||
                !IsInstanceId(candidate.Id) || all.Any(existing => existing.Id == candidate.Id))) return false;
        int parentDepth = 0;
        for (var current = parent; current is not null; current = FindNodeParent(current.Id)) parentDepth++;
        return parentDepth + TreeHeight(node) <= MaxTreeDepth;
    }

    private static int TreeHeight(HudLayoutNode node) => node.GetChildren().Length == 0 ? 1 : 1 + node.GetChildren().Max(TreeHeight);

    private static HudLayoutNode CopyTree(HudLayoutNode node) => node with
    {
        Id = $"{node.Kind}@{Guid.NewGuid():N}", Children = node.GetChildren().Select(CopyTree).ToImmutableArray()
    };

    private static HudLayoutNode RewriteTree(HudLayoutNode node, string id, Func<HudLayoutNode, HudLayoutNode> rewrite)
    {
        if (node.Id == id) return rewrite(node);
        var children = node.GetChildren();
        var updated = children.Select(child => RewriteTree(child, id, rewrite)).ToImmutableArray();
        return children.AsSpan().SequenceEqual(updated.AsSpan()) ? node : node with { Children = updated };
    }

    private static HudLayoutNode RemoveTreeChild(HudLayoutNode node, string id)
    {
        var children = node.GetChildren();
        var updated = children.Where(child => child.Id != id).Select(child => RemoveTreeChild(child, id)).ToImmutableArray();
        return children.AsSpan().SequenceEqual(updated.AsSpan()) ? node : node with { Children = updated };
    }

    private static HudCapsule TreeCapsule(HudCapsule capsule, HudLayoutNode root) => capsule with
    {
        Root = root, Items = string.Join(',', root.LeafIds()), Rows = null
    };

    private static HudCapsuleLayout NormalizeTree(HudCapsuleLayout layout)
    {
        var usedNodes = new HashSet<string>(StringComparer.Ordinal);
        var usedCapsules = new HashSet<string>(StringComparer.Ordinal);
        int leaves = 0;
        HudLayoutNode? ReadNode(HudLayoutNode? node, int depth)
        {
            if (node is null || depth > MaxTreeDepth || usedNodes.Count >= MaxTreeNodes) return null;
            string kind = node.Kind?.ToLowerInvariant() ?? string.Empty;
            string id = node.Id?.ToLowerInvariant() ?? string.Empty;
            bool container = kind is "row" or "column" or "grid";
            if (!(container || IsKind(kind)) || KindOf(id) != kind || !IsInstanceId(id) ||
                (!container && leaves >= MaxInstances) || !usedNodes.Add(id)) return null;
            if (!container) leaves++;
            var children = container ? node.GetChildren().Select(child => ReadNode(child, depth + 1))
                .Where(child => child is not null).Cast<HudLayoutNode>().ToImmutableArray() : ImmutableArray<HudLayoutNode>.Empty;
            var normalized = node with
            {
                Id = id, Kind = kind, Children = children, Columns = Math.Clamp(node.Columns, 1, 12),
                Gap = double.IsFinite(node.Gap) ? Math.Clamp(node.Gap, 0, 40) : 6,
                Padding = double.IsFinite(node.Padding) ? Math.Clamp(node.Padding, 0, 40) : 0,
                Alignment = node.Alignment is "start" or "center" or "end" or "stretch" ? node.Alignment : "start",
                Sizing = node.Sizing is "content" or "stretch" ? node.Sizing : "content"
            };
            return node == normalized ? node : normalized;
        }

        var islands = new List<HudCapsule>();
        var ordered = layout.Islands.OrderBy(capsule => capsule?.Id == MainId ? 0 : 1);
        foreach (var capsule in ordered)
        {
            if (usedNodes.Count >= MaxTreeNodes || islands.Count >= MaxTreeNodes) break;
            string? id = CanonicalId(capsule?.Id);
            if (id is null || !usedCapsules.Add(id)) continue;
            HudLayoutNode? root = ReadNode(capsule!.Root, 1);
            if (root is null || !root.IsContainer) root = HudLayoutNode.Create("column");
            if (id != MainId && root.GetChildren().Length == 0 && capsule.Root is null) continue;
            islands.Add(TreeCapsule(capsule with { Id = id, Position = id == MainId ? null : HudIslandPosition.Normalize(capsule.Position) }, root));
        }
        if (!usedCapsules.Contains(MainId)) islands.Insert(0, TreeCapsule(new(MainId, string.Empty), HudLayoutNode.Create("column")));
        var result = layout with { Islands = islands.ToImmutableArray(), HasInstances = true };
        return result == layout ? layout : result;
    }
}
