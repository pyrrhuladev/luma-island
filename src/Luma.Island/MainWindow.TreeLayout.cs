using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly Dictionary<string, TreeNodeVisual> _treeNodeVisuals = new(StringComparer.Ordinal);
    private Border? _treeHost;
    private HudLayoutNode? _appliedTree;
    private bool _treeWasEditing;

    private Rect CapsuleNodeBounds(string id)
    {
        FrameworkElement? element = CapsuleContainerCell(id);
        return element is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 }
            ? element.TransformToAncestor(IslandSurface).TransformBounds(new Rect(element.RenderSize)) : Rect.Empty;
    }

    private bool ApplyTreeContainers(HudLayoutNode tree)
    {
        if (_containerStack is not null) RestoreClassicContainers();
        string[] leaves = tree.LeafIds().ToArray();
        PrepareInstanceWidgets([leaves]);
        bool editing = IsCapsuleLayoutEditing;
        if (_treeHost is null)
        {
            _treeHost = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            CompactView.Children.Add(_treeHost);
            CompactContentGrid.Visibility = Visibility.Collapsed;
        }

        if (!Equals(_appliedTree, tree) || _treeWasEditing != editing)
        {
            foreach (TreeNodeVisual visual in _treeNodeVisuals.Values)
            {
                visual.Panel?.Children.Clear();
                if (visual.Panel is null) visual.Frame.Child = null;
            }
            _containerCells.Clear();
            _treeHost.Child = null;
            var used = new HashSet<string>(StringComparer.Ordinal);
            _treeHost.Child = BuildTreeNode(tree, used, root: true);
            foreach (string obsolete in _treeNodeVisuals.Keys.Where(id => !used.Contains(id)).ToArray()) _treeNodeVisuals.Remove(obsolete);
            _appliedTree = tree;
            _treeWasEditing = editing;
        }

        foreach (HudLayoutNode node in tree.DescendantsAndSelf())
        {
            if (!_treeNodeVisuals.TryGetValue(node.Id, out TreeNodeVisual? visual)) continue;
            ApplyTreeNodeSettings(node, visual, node.Id == tree.Id);
        }
        RefreshCapsuleNodeSelection();
        _treeHost.Width = double.NaN;
        _treeHost.Height = double.NaN;
        _treeHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double minimum = _capsuleId is null ? 348 : 68;
        double width = Math.Max(minimum, _treeHost.DesiredSize.Width);
        _treeHost.Width = width;
        _treeHost.Measure(new Size(width, double.PositiveInfinity));
        double height = _treeHost.DesiredSize.Height;
        _treeHost.Height = height;
        _containerContentSize = new Size(width, height);
        return true;
    }

    private Border BuildTreeNode(HudLayoutNode node, HashSet<string> used, bool root = false)
    {
        used.Add(node.Id);
        if (!_treeNodeVisuals.TryGetValue(node.Id, out TreeNodeVisual? visual))
        {
            var frame = new Border { Tag = node.Id, UseLayoutRounding = true, CornerRadius = new CornerRadius(5) };
            AutomationProperties.SetAutomationId(frame, "HudNode-" + node.Id);
            if (node.IsContainer)
            {
                var panel = new HudTreePanel();
                var label = new TextBlock { FontSize = 10, Foreground = TreeMutedBrush, VerticalAlignment = VerticalAlignment.Center };
                var grip = new Border
                {
                    Tag = node.Id, Child = label, Height = 18, Background = Brushes.Transparent,
                    Cursor = Cursors.SizeAll, ToolTip = "Переместить контейнер", Padding = new Thickness(4, 0, 4, 0)
                };
                grip.PreviewMouseLeftButtonDown += OnCapsuleContainerGripDown;
                AutomationProperties.SetName(grip, "Переместить контейнер");
                var content = new Grid();
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(panel, 1);
                content.Children.Add(grip);
                content.Children.Add(panel);
                frame.Child = content;
                visual = new(frame, panel, grip, label);
            }
            else visual = new(frame, null, null, null);
            _treeNodeVisuals.Add(node.Id, visual);
        }

        _containerCells[node.Id] = visual.Frame;
        if (node.IsContainer)
        {
            foreach (HudLayoutNode child in node.GetChildren()) visual.Panel!.Children.Add(BuildTreeNode(child, used));
            if (IsCapsuleLayoutEditing && node.GetChildren().IsEmpty)
                visual.Panel!.Children.Add(new TextBlock
                {
                    Text = "Перетащите сюда", FontSize = 11, Foreground = TreeMutedBrush,
                    Margin = new Thickness(9, 6, 9, 6), VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                });
        }
        else if (_instanceWidgets.TryGetValue(node.Id, out FrameworkElement? widget))
        {
            if (widget.Parent is Panel parent)
            {
                _classicWidgetParents.TryAdd(widget, parent);
                parent.Children.Remove(widget);
            }
            else if (widget.Parent is Border border) border.Child = null;
            visual.Frame.Child = widget;
        }
        ApplyTreeNodeSettings(node, visual, root);
        return visual.Frame;
    }

    private void ApplyTreeNodeSettings(HudLayoutNode node, TreeNodeVisual visual, bool root)
    {
        bool editing = IsCapsuleLayoutEditing;
        if (node.IsContainer)
        {
            visual.Panel!.Configure(node.Kind, node.Columns, node.Gap * LayoutGrowth, node.Alignment, node.GetChildren());
            visual.Panel.Margin = new Thickness(node.Padding * LayoutGrowth);
            visual.Frame.Visibility = editing || node.LeafIds().Any(id => _instanceWidgets.TryGetValue(id, out FrameworkElement? widget) &&
                widget.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
            visual.Grip!.Visibility = Show(editing && !root);
            visual.Label!.Text = node.Kind switch { "row" => "⋮ Строка", "grid" => "⋮ Сетка", _ => "⋮ Колонка" };
            visual.Frame.MinWidth = editing ? 96 : 0;
            visual.Panel.MinHeight = editing && !node.GetChildren().Any() ? 32 : 0;
            visual.Panel.Background = editing && !node.GetChildren().Any() ? TreeEmptyBrush : null;
        }
        else
        {
            visual.Frame.Visibility = _instanceWidgets.TryGetValue(node.Id, out FrameworkElement? widget) ? widget.Visibility : Visibility.Collapsed;
            visual.Frame.MinWidth = ContainerTileWidth(node.Id);
            visual.Frame.Height = ContainerTileHeight(node.Id);
        }
    }

    private void RefreshCapsuleNodeSelection()
    {
        foreach (var (id, visual) in _treeNodeVisuals)
        {
            bool selected = IsCapsuleLayoutEditing && id == SelectedCapsuleNodeId;
            visual.Frame.BorderBrush = selected ? TreeAccentBrush : IsCapsuleLayoutEditing && visual.Panel is not null ? TreeOutlineBrush : null;
            visual.Frame.BorderThickness = new Thickness(IsCapsuleLayoutEditing ? 1 : 0);
            visual.Frame.Background = selected ? TreeSelectionBrush : null;
            visual.Frame.Opacity = id == CapsuleDraggingNodeId ? .38 : 1;
        }
    }

    private void RestoreTreeContainers()
    {
        if (_treeHost is null) return;
        foreach (TreeNodeVisual visual in _treeNodeVisuals.Values)
        {
            visual.Panel?.Children.Clear();
            if (visual.Panel is null) visual.Frame.Child = null;
        }
        foreach (var (widget, parent) in _classicWidgetParents) if (widget.Parent is null) parent?.Children.Add(widget);
        _classicWidgetParents.Clear();
        _treeNodeVisuals.Clear();
        _containerCells.Clear();
        _treeHost.Child = null;
        CompactView.Children.Remove(_treeHost);
        _treeHost = null;
        _appliedTree = null;
        _containerContentSize = null;
        _appliedBlockOrder = _appliedMetricOrder = null;
        _appliedHeaderOrder = string.Empty;
        CompactContentGrid.Visibility = Visibility.Visible;
    }

    private sealed record TreeNodeVisual(Border Frame, HudTreePanel? Panel, Border? Grip, TextBlock? Label);

    private static readonly Brush TreeAccentBrush = TreeBrush(255, 157, 230, 209);
    private static readonly Brush TreeMutedBrush = TreeBrush(255, 150, 182, 174);
    private static readonly Brush TreeOutlineBrush = TreeBrush(100, 101, 144, 132);
    private static readonly Brush TreeEmptyBrush = TreeBrush(25, 157, 230, 209);
    private static readonly Brush TreeSelectionBrush = TreeBrush(18, 157, 230, 209);
    private static Brush TreeBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

internal sealed class HudTreePanel : Panel
{
    private string _kind = "column", _alignment = "start";
    private int _columns = 2;
    private double _gap = 6;
    private HashSet<string> _fillIds = new(StringComparer.Ordinal);

    internal void Configure(string kind, int columns, double gap, string alignment, IEnumerable<HudLayoutNode> children)
    {
        _kind = kind;
        _columns = Math.Max(1, columns);
        _gap = Math.Max(0, gap);
        _alignment = alignment;
        _fillIds = children.Where(child => child.Sizing == "stretch").Select(child => child.Id).ToHashSet(StringComparer.Ordinal);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        FrameworkElement[] visible = VisibleChildren();
        foreach (FrameworkElement child in visible) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (visible.Length == 0) return new Size(0, 0);
        if (_kind == "grid")
        {
            var (widths, heights) = GridDimensions(visible);
            return new Size(widths.Sum() + _gap * (widths.Length - 1), heights.Sum() + _gap * (heights.Length - 1));
        }
        bool horizontal = _kind == "row";
        double main = visible.Sum(child => horizontal ? child.DesiredSize.Width : child.DesiredSize.Height) + _gap * (visible.Length - 1);
        double cross = visible.Max(child => horizontal ? child.DesiredSize.Height : child.DesiredSize.Width);
        return horizontal ? new Size(main, cross) : new Size(cross, main);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        FrameworkElement[] children = VisibleChildren();
        if (children.Length == 0) return finalSize;
        if (_kind == "grid")
        {
            ArrangeGrid(children, finalSize);
            return finalSize;
        }
        bool horizontal = _kind == "row";
        double available = horizontal ? finalSize.Width : finalSize.Height;
        double natural = children.Sum(child => horizontal ? child.DesiredSize.Width : child.DesiredSize.Height) + _gap * (children.Length - 1);
        int fills = horizontal ? children.Count(IsFill) : 0;
        double extra = fills > 0 ? Math.Max(0, available - natural) / fills : 0;
        double offset = 0;
        foreach (FrameworkElement child in children)
        {
            double main = (horizontal ? child.DesiredSize.Width : child.DesiredSize.Height) + (horizontal && IsFill(child) ? extra : 0);
            double crossAvailable = horizontal ? finalSize.Height : finalSize.Width;
            double cross = _alignment == "stretch" || !horizontal && IsFill(child) ? crossAvailable : Math.Min(crossAvailable,
                horizontal ? child.DesiredSize.Height : child.DesiredSize.Width);
            double crossOffset = AlignmentOffset(crossAvailable, cross);
            child.Arrange(horizontal ? new Rect(offset, crossOffset, main, cross) : new Rect(crossOffset, offset, cross, main));
            offset += main + _gap;
        }
        return finalSize;
    }

    private void ArrangeGrid(FrameworkElement[] children, Size size)
    {
        var (widths, heights) = GridDimensions(children);
        if (_alignment == "stretch" || children.Any(IsFill))
        {
            double extra = Math.Max(0, size.Width - widths.Sum() - _gap * (widths.Length - 1)) / widths.Length;
            for (int column = 0; column < widths.Length; column++) widths[column] += extra;
        }
        double y = 0;
        for (int row = 0; row < heights.Length; row++)
        {
            double x = 0;
            for (int column = 0; column < widths.Length; column++)
            {
                int index = row * widths.Length + column;
                if (index < children.Length)
                {
                    FrameworkElement child = children[index];
                    bool fill = _alignment == "stretch" || IsFill(child);
                    double width = fill ? widths[column] : child.DesiredSize.Width;
                    double height = _alignment == "stretch" ? heights[row] : child.DesiredSize.Height;
                    child.Arrange(new Rect(x + AlignmentOffset(widths[column], width), y + AlignmentOffset(heights[row], height), width, height));
                }
                x += widths[column] + _gap;
            }
            y += heights[row] + _gap;
        }
    }

    private (double[] Widths, double[] Heights) GridDimensions(FrameworkElement[] children)
    {
        int columns = Math.Min(_columns, children.Length);
        var widths = new double[columns];
        var heights = new double[(children.Length + columns - 1) / columns];
        for (int index = 0; index < children.Length; index++)
        {
            widths[index % columns] = Math.Max(widths[index % columns], children[index].DesiredSize.Width);
            heights[index / columns] = Math.Max(heights[index / columns], children[index].DesiredSize.Height);
        }
        double columnWidth = widths.Max();
        Array.Fill(widths, columnWidth);
        return (widths, heights);
    }

    private FrameworkElement[] VisibleChildren() => Children.OfType<FrameworkElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
    private bool IsFill(FrameworkElement child) => child.Tag is string id && _fillIds.Contains(id);
    private double AlignmentOffset(double available, double desired) => _alignment switch
    {
        "center" => Math.Max(0, (available - desired) / 2), "end" => Math.Max(0, available - desired), _ => 0
    };
}
