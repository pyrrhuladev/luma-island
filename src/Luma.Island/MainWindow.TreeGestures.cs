using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private TreeGesture? _treeGesture;
    private string? CapsuleDraggingNodeId => (_islandOwner ?? this)._treeGesture is { Started: true } drag ? drag.NodeId : null;

    private bool TryHandleTreePointerDown(MouseButtonEventArgs e)
    {
        var owner = _islandOwner ?? this;
        // Audio buttons remain usable. The surrounding widget and the container
        // header are the drag handles for their separate layout nodes.
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null && node != IslandSurface;
             node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase or RangeBase) return true;

        string? id = CapsuleItemAt(e.GetPosition(IslandSurface));
        if (id is null) return true;
        if (owner.CapsuleDraft.FindNodeParent(id) is null)
        {
            owner.SelectCapsuleNode(id);
            e.Handled = true;
            return true;
        }
        owner.BeginCapsuleNodeDrag(id, IslandSurface, e);
        return true;
    }

    private string? TreeNodeAt(HudLayoutNode node, Point point)
    {
        // The smallest contained node wins. A container's own header and empty
        // region therefore remain available for selecting and moving the group.
        foreach (HudLayoutNode child in node.GetChildren().Reverse())
        {
            if (child.IsContainer && TreeNodeAt(child, point) is { } nested) return nested;
            if (!child.IsContainer && CapsuleNodeBounds(child.Id).Contains(point)) return child.Id;
        }
        return node.IsContainer && CapsuleNodeBounds(node.Id).Contains(point) ? node.Id : null;
    }

    private void OnCapsuleContainerGripDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } source)
            (_islandOwner ?? this).BeginCapsuleNodeDrag(id, source, e);
    }

    private void BeginCapsuleNodeDrag(string nodeId, FrameworkElement source, MouseButtonEventArgs e)
    {
        var owner = _islandOwner ?? this;
        if (!ReferenceEquals(owner, this)) { owner.BeginCapsuleNodeDrag(nodeId, source, e); return; }
        if (_capsuleEdit is null || e.ChangedButton != MouseButton.Left || CapsuleDraft.GetNode(nodeId) is null) return;
        SelectCapsuleNode(nodeId);
        BeginTreeGesture(new TreeGesture(nodeId, CapsuleDraft, source.PointToScreen(e.GetPosition(source)), null), source, e);
    }

    private void BeginPaletteDrag(string kind, FrameworkElement source, MouseButtonEventArgs e)
    {
        var owner = _islandOwner ?? this;
        if (!ReferenceEquals(owner, this)) { owner.BeginPaletteDrag(kind, source, e); return; }
        if (_capsuleEdit is null || e.ChangedButton != MouseButton.Left ||
            kind is not ("row" or "column" or "grid") && !CapsuleDraft.CanAddInstance) return;
        var node = HudLayoutNode.Create(kind);
        BeginTreeGesture(new TreeGesture(node.Id, CapsuleDraft, source.PointToScreen(e.GetPosition(source)), node), source, e);
    }

    private void BeginTreeGesture(TreeGesture drag, FrameworkElement source, MouseButtonEventArgs e)
    {
        CancelCapsuleGesture();
        var capture = CapsuleEditorCaptureSurface ?? source;
        drag.Capture = capture;
        _treeGesture = drag;
        capture.PreviewMouseMove += OnTreePointerMove;
        capture.PreviewMouseLeftButtonUp += OnTreePointerUp;
        capture.LostMouseCapture += OnTreeCaptureLost;
        capture.PreviewKeyDown += OnTreeGestureKeyDown;
        if (!capture.CaptureMouse()) ReleaseTreeGesture(drag);
        e.Handled = true;
    }

    private void OnTreePointerMove(object sender, MouseEventArgs e)
    {
        if (_treeGesture is not { } drag) return;
        e.Handled = true;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelTreeGesture(); return; }
        Point screen = drag.Capture!.PointToScreen(e.GetPosition(drag.Capture));
        var travel = screen - drag.Origin;
        if (!drag.Started && Math.Abs(travel.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(travel.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        try
        {
            if (!drag.Started)
            {
                drag.Started = true;
                drag.CreatingPreview = true;
                try { drag.Preview = CreateCapsuleDragPreview(drag.NewNode?.Kind ?? drag.NodeId, screen); }
                finally { drag.CreatingPreview = false; }
                if (_treeGesture != drag) return;
                // Opening the nonactivating preview can release capture on some
                // WPF versions. Reacquire only during that known transition.
                if (Mouse.Captured != drag.Capture && !drag.Capture.CaptureMouse()) { CancelTreeGesture(); return; }
                PreviewCapsuleLayout(drag.Before);
            }
            if (drag.Preview is { } preview) PositionCapsuleDragPreview(preview, screen);
            UpdateTreeGesture(drag, screen);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            App.Log(error);
            CancelTreeGesture();
        }
    }

    private void UpdateTreeGesture(TreeGesture drag, Point screen)
    {
        if (TreeDragBlocked(drag, screen))
        {
            drag.Blocked = true;
            SetTreeGestureTarget(drag, null);
            return;
        }
        drag.Blocked = false;
        // Keep a previewed slot stable while the pointer is over the slot itself.
        // Otherwise its newly arranged neighbors would flip the insertion edge.
        if (!drag.InvalidPlacement && drag.Target is { } current && current.Window.IsVisible &&
            current.Window.CapsuleNodeBounds(drag.NodeId).Contains(current.Window.IslandSurface.PointFromScreen(screen))) return;
        SetTreeGestureTarget(drag, FindTreeDropTarget(drag, screen));
    }

    private bool TreeDragBlocked(TreeGesture drag, Point screen)
    {
        IntPtr hit = WindowFromCapsulePoint(new CapsuleScreenPoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y)));
        return IsLayoutEditorWindowHandle(hit);
    }

    private void SetTreeGestureTarget(TreeGesture drag, TreeDropTarget? target)
    {
        if (drag.Target?.Location == target?.Location) return;
        drag.Target = target;
        var layout = target is null ? drag.Before : PlaceTreeGesture(drag, target);
        bool unchangedMove = drag.NewNode is null && target is not null &&
            drag.Before.FindNodeParent(drag.NodeId) is { } parent && parent.Id == target.ParentId &&
            Array.FindIndex(parent.GetChildren().ToArray(), node => node.Id == drag.NodeId) == target.Index;
        drag.InvalidPlacement = target is not null && layout == drag.Before && !unchangedMove;
        SetLayoutEditorStatus(drag.InvalidPlacement ? "Здесь превышен предел компонентов или вложенности." : null);
        PreviewCapsuleLayout(layout);
        drag.ShowTarget(drag.InvalidPlacement ? null : target);
    }

    private static HudCapsuleLayout PlaceTreeGesture(TreeGesture drag, TreeDropTarget target) => drag.NewNode is { } node
        ? drag.Before.InsertNode(node, target.CapsuleId, target.ParentId, target.Index)
        : drag.Before.MoveNode(drag.NodeId, target.CapsuleId, target.ParentId, target.Index);

    private TreeDropTarget? FindTreeDropTarget(TreeGesture drag, Point screen)
    {
        IntPtr hit = WindowFromCapsulePoint(new CapsuleScreenPoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y)));
        var excluded = drag.Before.GetNode(drag.NodeId)?.DescendantsAndSelf().Select(node => node.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, window) in LayoutWindows())
        {
            if (!window.IsVisible || new WindowInteropHelper(window).Handle != hit) continue;
            var capsule = CapsuleDraft.Islands.FirstOrDefault(item => item.Id == id);
            if (capsule?.Root is not { } root) continue;
            Point local = window.IslandSurface.PointFromScreen(screen);
            if (!new Rect(window.IslandSurface.RenderSize).Contains(local)) continue;
            HudLayoutNode parent = window.TreeDropParent(root, local, excluded);
            return window.TreeInsertion(drag, capsule.Id, parent, local);
        }
        return null;
    }

    private HudLayoutNode TreeDropParent(HudLayoutNode root, Point local, HashSet<string> excluded)
    {
        foreach (HudLayoutNode child in root.GetChildren().Reverse())
        {
            if (!child.IsContainer || excluded.Contains(child.Id)) continue;
            Rect bounds = CapsuleNodeBounds(child.Id);
            if (bounds.IsEmpty || !bounds.Contains(local)) continue;
            // The edge belongs to the outer container, the middle to this one.
            var middle = bounds;
            middle.Inflate(-Math.Min(9, bounds.Width / 5), -Math.Min(9, bounds.Height / 5));
            if (middle.Contains(local)) return TreeDropParent(child, local, excluded);
        }
        return root;
    }

    private TreeDropTarget? TreeInsertion(TreeGesture drag, string capsuleId, HudLayoutNode parent, Point local)
    {
        if (drag.Before.GetNode(parent.Id) is not { } originalParent || originalParent.Id == drag.NodeId) return null;
        var originalChildren = originalParent.GetChildren().Where(child => child.Id != drag.NodeId).ToArray();
        var children = parent.GetChildren().Where(child => child.Id != drag.NodeId)
            .Select(child => (Node: child, Bounds: CapsuleNodeBounds(child.Id))).Where(entry => !entry.Bounds.IsEmpty).ToArray();
        Rect parentBounds = CapsuleNodeBounds(parent.Id);
        if (parentBounds.IsEmpty) parentBounds = new Rect(IslandSurface.RenderSize);
        if (children.Length == 0)
            return new(this, capsuleId, parent.Id, 0, parentBounds);

        bool horizontal = parent.Kind == "row";
        var anchor = children[^1];
        bool before = false;
        foreach (var child in children)
        {
            bool precedes = horizontal ? local.X < CapsuleCenter(child.Bounds).X
                : parent.Kind == "grid" ? local.Y < child.Bounds.Top || local.Y <= child.Bounds.Bottom && local.X < CapsuleCenter(child.Bounds).X
                : local.Y < CapsuleCenter(child.Bounds).Y;
            if (!precedes) continue;
            anchor = child;
            before = true;
            break;
        }
        int anchorIndex = Array.FindIndex(originalChildren, node => node.Id == anchor.Node.Id);
        int index = anchorIndex < 0 ? originalChildren.Length : anchorIndex + (before ? 0 : 1);
        Rect marker = horizontal || parent.Kind == "grid"
            ? new Rect((before ? anchor.Bounds.Left : anchor.Bounds.Right) - 1, anchor.Bounds.Top, 2, Math.Max(1, anchor.Bounds.Height))
            : new Rect(anchor.Bounds.Left, (before ? anchor.Bounds.Top : anchor.Bounds.Bottom) - 1, Math.Max(1, anchor.Bounds.Width), 2);
        return new(this, capsuleId, parent.Id, index, marker);
    }

    private void OnTreePointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_treeGesture is not { } drag || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        if (!drag.Started) { ReleaseTreeGesture(drag); return; }
        Point screen = drag.Capture!.PointToScreen(e.GetPosition(drag.Capture));
        UpdateTreeGesture(drag, screen);
        if (drag.Blocked) { CancelTreeGesture(); return; }
        if (drag.InvalidPlacement)
        {
            CancelTreeGesture();
            SetLayoutEditorStatus("Здесь превышен предел компонентов или вложенности.");
            return;
        }
        if (drag.Target is null)
        {
            var detached = DetachTreeGesture(drag, screen);
            if (detached == drag.Before)
            {
                CancelTreeGesture();
                SetLayoutEditorStatus("Здесь превышен предел компонентов или вложенности.");
                return;
            }
            PreviewCapsuleLayout(detached);
        }
        var result = CapsuleDraft;
        ReleaseTreeGesture(drag);
        PreviewCapsuleLayout(result);
        SetLayoutEditorStatus(null);
        SelectCapsuleNode(drag.NodeId);
        CommitCapsuleGesture(drag.Before);
    }

    private HudCapsuleLayout DetachTreeGesture(TreeGesture drag, Point screen)
    {
        if (_placement is null) return drag.Before;
        var display = PlacementMath.NearestDisplay(new Rect(screen, new Size(1, 1)), _placement.Displays);
        var position = PositionAt(display, screen, new Size(1, 1), 0);
        var seeded = drag.NewNode is { } node ? drag.Before.InsertNode(node, HudCapsuleLayout.MainId, null, int.MaxValue) : drag.Before;
        var detached = seeded.MoveNode(drag.NodeId, null, null, 0, position);
        PreviewCapsuleLayout(detached);
        var island = detached.FindNodeCapsule(drag.NodeId);
        var window = island is null ? null : LayoutWindows().FirstOrDefault(entry => entry.Id == island.Id).Window;
        if (window is null || !window.IsVisible || island is null) return detached;
        Rect bounds = DisplayService.GetWindowBounds(window);
        return detached.WithPosition(island.Id, PositionAt(display, screen, bounds.Size, 8 * DisplayService.GetWindowScale(window)));
    }

    private void OnTreeCaptureLost(object sender, MouseEventArgs e)
    {
        if (_treeGesture is { CreatingPreview: false } drag && Mouse.Captured != drag.Capture) CancelTreeGesture();
    }

    private void OnTreeGestureKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _treeGesture is null) return;
        e.Handled = true;
        CancelTreeGesture();
    }

    private void CancelTreeGesture()
    {
        if (_treeGesture is not { } drag) return;
        ReleaseTreeGesture(drag);
        if (drag.Started && IsCapsuleLayoutEditing) PreviewCapsuleLayout(drag.Before);
        SetLayoutEditorStatus(null);
    }

    private void ReleaseTreeGesture(TreeGesture drag)
    {
        if (_treeGesture != drag) return;
        _treeGesture = null;
        if (drag.Capture is { } capture)
        {
            capture.PreviewMouseMove -= OnTreePointerMove;
            capture.PreviewMouseLeftButtonUp -= OnTreePointerUp;
            capture.LostMouseCapture -= OnTreeCaptureLost;
            capture.PreviewKeyDown -= OnTreeGestureKeyDown;
            if (Mouse.Captured == capture) capture.ReleaseMouseCapture();
        }
        drag.ShowTarget(null);
        drag.Preview?.Close();
    }

    private sealed record TreeDropTarget(MainWindow Window, string CapsuleId, string ParentId, int Index, Rect Marker)
    {
        public string Location => CapsuleId + "/" + ParentId + "/" + Index;
    }

    private sealed class TreeGesture(string nodeId, HudCapsuleLayout before, Point origin, HudLayoutNode? newNode)
    {
        private readonly CapsuleGesture _marker = new(nodeId, origin);
        public string NodeId { get; } = nodeId;
        public HudCapsuleLayout Before { get; } = before;
        public Point Origin { get; } = origin;
        public HudLayoutNode? NewNode { get; } = newNode;
        public FrameworkElement? Capture { get; set; }
        public bool Started { get; set; }
        public bool CreatingPreview { get; set; }
        public bool Blocked { get; set; }
        public bool InvalidPlacement { get; set; }
        public Window? Preview { get; set; }
        public TreeDropTarget? Target { get; set; }

        public void ShowTarget(TreeDropTarget? target) => _marker.ShowTarget(target is null ? null
            : new CapsuleDropTarget(target.CapsuleId, target.Window.IslandSurface, target.Index, target.Marker));
    }
}
