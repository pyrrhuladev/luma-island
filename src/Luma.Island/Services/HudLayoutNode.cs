using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;

namespace Luma.Island.Services;

/// <summary>A component or layout container with its own persistent identity.</summary>
public sealed record HudLayoutNode(string Id, string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<HudLayoutNode> Children = default)
{
    public int Columns { get; init; } = 2;
    public double Gap { get; init; } = 6;
    public double Padding { get; init; }
    public string Alignment { get; init; } = "start";
    public string Sizing { get; init; } = "content";

    [JsonIgnore] public bool IsContainer => Kind is "row" or "column" or "grid";

    public static HudLayoutNode Create(string kind, string? id = null) =>
        new(id ?? $"{kind}@{Guid.NewGuid():N}", kind, ImmutableArray<HudLayoutNode>.Empty);

    public ImmutableArray<HudLayoutNode> GetChildren() => Children.IsDefault ? ImmutableArray<HudLayoutNode>.Empty : Children;

    public IEnumerable<HudLayoutNode> DescendantsAndSelf()
    {
        var pending = new Stack<HudLayoutNode>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            var children = node.GetChildren();
            for (int index = children.Length - 1; index >= 0; index--) pending.Push(children[index]);
        }
    }

    public IEnumerable<string> LeafIds() => DescendantsAndSelf().Where(node => !node.IsContainer).Select(node => node.Id);

    public bool Equals(HudLayoutNode? other) => other is not null && Id == other.Id && Kind == other.Kind &&
        Columns == other.Columns && Gap.Equals(other.Gap) && Padding.Equals(other.Padding) && Alignment == other.Alignment &&
        Sizing == other.Sizing && GetChildren().AsSpan().SequenceEqual(other.GetChildren().AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id); hash.Add(Kind); hash.Add(Columns); hash.Add(Gap); hash.Add(Padding); hash.Add(Alignment); hash.Add(Sizing);
        foreach (var child in GetChildren()) hash.Add(child);
        return hash.ToHashCode();
    }
}
