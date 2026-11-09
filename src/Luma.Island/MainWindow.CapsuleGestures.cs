using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private MainWindow? _capsuleGestureSource;
    private CapsuleGesture? _capsuleGesture;
    private bool IsCapsuleGestureActive => (_islandOwner ?? this)._capsuleGestureSource is not null;

    private bool TryHandleCapsulePointerDown(MouseButtonEventArgs e)
    {
        MainWindow owner = _islandOwner ?? this;
        if (!owner.IsCapsuleLayoutEditing || e.ChangedButton != MouseButton.Left) return false;
        if (e.ButtonState != MouseButtonState.Pressed || IsClickThrough || _closed) return true;

        // Keep the playback and volume controls clickable while the layout is editable.
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null && node != IslandSurface;
             node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase or RangeBase) return true;

        Point origin = e.GetPosition(IslandSurface);
        string? item = CapsuleItemAt(origin);
        if (item is null) return true;

        owner.CancelCapsuleGesture();
        if (!IslandSurface.CaptureMouse()) return true;
        _capsuleGesture = new CapsuleGesture(item, origin);
        owner._capsuleGestureSource = this;
        IslandSurface.PreviewMouseMove += OnCapsulePointerMove;
        IslandSurface.PreviewMouseLeftButtonUp += OnCapsulePointerUp;
        IslandSurface.LostMouseCapture += OnCapsuleCaptureLost;
        e.Handled = true;
        return true;
    }

    private string? CapsuleItemAt(Point origin)
    {
        var owner = _islandOwner ?? this;
        if (owner.CapsuleDraft is not { } draft) return null;
        string id = _capsuleId ?? HudCapsuleLayout.MainId;
        var capsule = draft.Islands.FirstOrDefault(candidate => candidate.Id == id);
        if (capsule is null) return null;
        foreach (string item in capsule.GetItems())
            if (CapsuleWidgetBounds(item).Contains(origin)) return item;
        return null;
    }

    private void OnCapsulePointerMove(object sender, MouseEventArgs e)
    {
        if (_capsuleGesture is not { } drag) return;
        e.Handled = true;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelCapsuleGesture();
            return;
        }

        Point pointer = e.GetPosition(IslandSurface);
        Vector travel = pointer - drag.Origin;
        if (drag.Preview is null && Math.Abs(travel.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(travel.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        Point screenPoint = IslandSurface.PointToScreen(pointer);
        MainWindow owner = _islandOwner ?? this;
        var target = owner.FindCapsuleDropTarget(drag.Item, screenPoint);
        drag.ShowTarget(target);
        try
        {
            if (drag.Preview is { } preview) PositionCapsuleDragPreview(preview, screenPoint);
            else
            {
                Window created = CreateCapsuleDragPreview(drag.Item, screenPoint);
                if (_capsuleGesture == drag) drag.Preview = created;
                else created.Close(); // Showing a window can synchronously release capture.
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            App.Log(error);
            CancelCapsuleGesture();
        }
    }

    private void OnCapsulePointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_capsuleGesture is not { } drag) return;
        MainWindow owner = _islandOwner ?? this;
        e.Handled = true;
        bool moved = drag.Preview is not null && owner.IsCapsuleLayoutEditing;
        Point screenPoint = moved ? IslandSurface.PointToScreen(e.GetPosition(IslandSurface)) : default;
        var target = moved ? owner.FindCapsuleDropTarget(drag.Item, screenPoint) : null;
        CancelCapsuleGesture();
        // Applying the draft can close the window that just released the pointer.
        if (!moved) return;
        if (target?.Row is { } row)
            owner.MoveCapsuleItemToRow(drag.Item, target.Id, row.RowIndex, row.ColumnIndex, row.NewRow);
        else owner.MoveCapsuleItem(drag.Item, target?.Id, target?.InsertIndex ?? 0, screenPoint);
    }

    private void OnCapsuleCaptureLost(object sender, MouseEventArgs e)
    {
        if (Mouse.Captured != IslandSurface) CancelCapsuleGesture();
    }

    private void CancelCapsuleGesture()
    {
        MainWindow owner = _islandOwner ?? this;
        if (owner._capsuleGestureSource is not { } source) return;
        owner._capsuleGestureSource = null;
        var drag = source._capsuleGesture;
        source._capsuleGesture = null;
        source.IslandSurface.PreviewMouseMove -= source.OnCapsulePointerMove;
        source.IslandSurface.PreviewMouseLeftButtonUp -= source.OnCapsulePointerUp;
        source.IslandSurface.LostMouseCapture -= source.OnCapsuleCaptureLost;
        if (Mouse.Captured == source.IslandSurface) source.IslandSurface.ReleaseMouseCapture();
        drag?.ShowTarget(null);
        drag?.Preview?.Close();
    }

    private CapsuleDropTarget? FindCapsuleDropTarget(string item, Point screenPoint)
    {
        if (CapsuleDraft is not { } draft) return null;
        IntPtr hit = WindowFromCapsulePoint(new CapsuleScreenPoint((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
        foreach (var (id, window) in LayoutWindows())
        {
            if (!window.IsVisible || new WindowInteropHelper(window).Handle != hit) continue;
            var capsule = draft.Islands.FirstOrDefault(candidate => candidate.Id == id);
            if (capsule is not null) return window.CapsuleDropTargetAt(capsule, item, screenPoint);
        }
        return null;
    }

    private CapsuleDropTarget? CapsuleDropTargetAt(HudCapsule capsule, string item, Point screenPoint)
    {
        if (IslandSurface.IsVisible)
        {
            Point local = IslandSurface.PointFromScreen(screenPoint);
            if (!new Rect(IslandSurface.RenderSize).Contains(local)) return null;
            return CapsuleRowInsertion(capsule, item, local) ?? CapsuleInsertion(capsule, item, local);
        }
        if (capsule.Id == HudCapsuleLayout.MainId && CapsuleEditDropZone.IsVisible &&
            new Rect(CapsuleEditDropZone.RenderSize).Contains(CapsuleEditDropZone.PointFromScreen(screenPoint)))
            return new(capsule.Id, CapsuleEditDropZone, 0, Rect.Empty);
        return null;
    }

    private CapsuleDropTarget? CapsuleRowInsertion(HudCapsule capsule, string item, Point local)
    {
        string[][] rows = capsule.GetRows();
        bool explicitRows = capsule.HasExplicitRows;
        int nearest = -1;
        Rect nearestBounds = Rect.Empty;
        double nearestDistance = double.PositiveInfinity;
        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            string[] row = rows[rowIndex];
            if (row.Length == 0) continue;
            Rect bounds = Rect.Empty;
            if (explicitRows)
            {
                foreach (string widget in row) bounds.Union(CapsuleWidgetBounds(widget));
            }
            else bounds = CapsuleElementBounds(PresentedWidgetGroup(HudCapsuleLayout.Group(row[0])));
            if (bounds.IsEmpty) continue;
            double distance = Math.Abs(local.Y - Math.Clamp(local.Y, bounds.Top, bounds.Bottom));
            if (distance >= nearestDistance) continue;
            nearest = rowIndex;
            nearestBounds = bounds;
            nearestDistance = distance;
        }
        if (nearest < 0) return null;

        double sideBand = Math.Clamp(nearestBounds.Width * .2, 12, 48);
        double rowBand = Math.Clamp(nearestBounds.Height * .2, 3, 9);
        bool nearSide = local.X <= nearestBounds.Left + sideBand || local.X >= nearestBounds.Right - sideBand;
        bool insideRow = local.Y >= nearestBounds.Top && local.Y <= nearestBounds.Bottom;
        bool nearRowEdge = local.Y < nearestBounds.Top + rowBand || local.Y > nearestBounds.Bottom - rowBand;
        string group = HudCapsuleLayout.Group(item);
        bool canSeparate = explicitRows || group != item && rows.Any(row =>
            row.Any(widget => widget != item && HudCapsuleLayout.Group(widget) == group));

        if (nearRowEdge && canSeparate && (!nearSide || !insideRow))
        {
            bool below = local.Y >= CapsuleCenter(nearestBounds).Y;
            return new(capsule.Id, IslandSurface, 0, CapsuleRowMarker(nearestBounds, below),
                new(nearest + (below ? 1 : 0), 0, NewRow: true));
        }
        if (!insideRow || !explicitRows && !nearSide) return null;

        Rect closest = Rect.Empty;
        int column = -1;
        double closestDistance = double.PositiveInfinity;
        string[] widgets = rows[nearest];
        for (int index = 0; index < widgets.Length; index++)
        {
            Rect bounds = CapsuleWidgetBounds(widgets[index]);
            if (bounds.IsEmpty) continue;
            Point within = new(Math.Clamp(local.X, bounds.Left, bounds.Right), Math.Clamp(local.Y, bounds.Top, bounds.Bottom));
            double distance = (within - local).LengthSquared;
            if (distance >= closestDistance) continue;
            closest = bounds;
            closestDistance = distance;
            column = index;
        }
        if (column < 0) return null;

        bool after = local.X >= CapsuleCenter(closest).X;
        double x = after ? closest.Right : closest.Left;
        return new(capsule.Id, IslandSurface, 0, new Rect(x - 1, closest.Top + 1, 2, Math.Max(1, closest.Height - 2)),
            new(nearest, column + (after ? 1 : 0), NewRow: false));
    }

    private CapsuleDropTarget CapsuleInsertion(HudCapsule capsule, string item, Point local)
    {
        string[] items = capsule.GetItems();
        string group = HudCapsuleLayout.Group(item);
        CapsuleDropTarget At(int index, Rect marker) => new(capsule.Id, IslandSurface, capsule.GetInsertionIndex(item, index), marker);

        // A group occupies one row or grid. Drop between its arranged tiles if
        // the destination already holds other members of that group.
        int first = Array.FindIndex(items, key => HudCapsuleLayout.Group(key) == group);
        int last = Array.FindLastIndex(items, key => HudCapsuleLayout.Group(key) == group);
        if (group != item && first >= 0 && (first != last || items[first] != item) &&
            CapsuleElementBounds(PresentedWidgetGroup(group)) is { IsEmpty: false } groupBounds)
        {
            if (groupBounds.Contains(local))
            {
                Rect closest = Rect.Empty;
                int closestIndex = -1;
                double closestDistance = double.PositiveInfinity;
                for (int index = first; index <= last; index++)
                {
                    Rect bounds = CapsuleWidgetBounds(items[index]);
                    if (bounds.IsEmpty) continue;
                    double distance = (CapsuleCenter(bounds) - local).LengthSquared;
                    if (distance >= closestDistance) continue;
                    closest = bounds;
                    closestIndex = index;
                    closestDistance = distance;
                }
                if (closestIndex >= 0)
                {
                    bool after = local.X >= CapsuleCenter(closest).X;
                    double x = after ? closest.Right : closest.Left;
                    return At(closestIndex + (after ? 1 : 0),
                        new Rect(x - 1, closest.Top + 1, 2, Math.Max(1, closest.Height - 2)));
                }
            }
            bool below = local.Y >= CapsuleCenter(groupBounds).Y;
            return At(below ? last + 1 : first, CapsuleRowMarker(groupBounds, below));
        }

        // Standalone widgets and newly introduced groups can move between rows.
        // Keep invisible rows in the model, but skip their absent geometry.
        var rows = new List<(Rect Bounds, int First, int Last)>();
        for (int start = 0; start < items.Length;)
        {
            string row = HudCapsuleLayout.Group(items[start]);
            int end = start + 1;
            while (end < items.Length && HudCapsuleLayout.Group(items[end]) == row) end++;
            Rect bounds = CapsuleElementBounds(PresentedWidgetGroup(row));
            if (!bounds.IsEmpty) rows.Add((bounds, start, end - 1));
            start = end;
        }
        if (rows.Count == 0) return At(items.Length, Rect.Empty);

        int rawIndex = rows[^1].Last + 1;
        foreach (var row in rows)
            if (local.Y < CapsuleCenter(row.Bounds).Y) { rawIndex = row.First; break; }
        int insertIndex = capsule.GetInsertionIndex(item, rawIndex);
        foreach (var row in rows)
            if (insertIndex <= row.First)
                return new(capsule.Id, IslandSurface, insertIndex, CapsuleRowMarker(row.Bounds, below: false));
        return new(capsule.Id, IslandSurface, insertIndex, CapsuleRowMarker(rows[^1].Bounds, below: true));
    }

    private Rect CapsuleRowMarker(Rect row, bool below)
    {
        double y = Math.Clamp(below ? row.Bottom + 1 : row.Top - 3, 4, Math.Max(4, IslandSurface.ActualHeight - 6));
        return new Rect(10, y, Math.Max(1, IslandSurface.ActualWidth - 20), 2);
    }

    private Rect CapsuleWidgetBounds(string item)
    {
        if (CapsuleContainerCell(item) is { } cell) return CapsuleElementBounds(cell);
        FrameworkElement? element = PresentedWidget(item);
        Rect visible = CapsuleElementBounds(element);
        string group = HudCapsuleLayout.Group(item);
        if (visible.IsEmpty || group == item || element is null ||
            VisualTreeHelper.GetParent(element) is not FrameworkElement parent) return visible;

        Rect slot = LayoutInformation.GetLayoutSlot(element);
        if (slot.IsEmpty || slot.Width <= 0 || slot.Height <= 0) return visible;
        Rect groupBounds = CapsuleElementBounds(PresentedWidgetGroup(group));
        return Rect.Intersect(parent.TransformToAncestor(IslandSurface).TransformBounds(slot), groupBounds);
    }

    private Rect CapsuleElementBounds(FrameworkElement? element) => element is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 }
        ? element.TransformToAncestor(IslandSurface).TransformBounds(new Rect(element.RenderSize)) : Rect.Empty;

    private static Point CapsuleCenter(Rect bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    private Window CreateCapsuleDragPreview(string item, Point screenPoint)
    {
        string text = item switch
        {
            "cpu" => CompactCpu.Text, "gpu" => CompactGpu.Text, "memory" => CompactMemory.Text,
            "music" => string.IsNullOrWhiteSpace(CompactTitle.Text) ? "Музыка" : CompactTitle.Text,
            "fps" => CompactFps.Text + " FPS", "low" => "1% Low · " + CompactOnePercentLow.Text,
            "time" => CompactClock.Text, "battery" => "Батарея · " + CompactBattery.Text,
            "frame-time" => "Время кадра · " + FrameTimeValue.Text,
            "volume" => "Громкость · " + VolumeLabel.Text, "date" => DateLabel.Text,
            "cpu-chart" => "График CPU", "gpu-chart" => "График GPU", "memory-chart" => "График RAM",
            "fps-chart" => "График FPS", _ => item
        };
        var preview = new Window
        {
            Owner = this, ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            WindowStartupLocation = WindowStartupLocation.Manual, Background = Brushes.Transparent,
            IsHitTestVisible = false, Focusable = false, SizeToContent = SizeToContent.WidthAndHeight,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(216, 18, 23, 28)),
                BorderBrush = CapsuleDropAdorner.Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(13, 7, 13, 7), Child = new TextBlock
                {
                    Text = text, Foreground = Brushes.White, FontFamily = FontFamily, FontSize = 13,
                    MaxWidth = 216, TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
        try
        {
            var handle = new WindowInteropHelper(preview).EnsureHandle();
            long style = GetExtendedWindowStyle(handle, -20).ToInt64();
            SetExtendedWindowStyle(handle, -20, new IntPtr((style | ClickThroughStyleMask | 0x80L) & ~0x40000L));
            PositionCapsuleDragPreview(preview, screenPoint);
            preview.Show();
            PositionCapsuleDragPreview(preview, screenPoint);
            return preview;
        }
        catch
        {
            preview.Close();
            throw;
        }
    }

    private static void PositionCapsuleDragPreview(Window preview, Point screenPoint)
        => DisplayService.MoveWindow(preview, screenPoint.X + 16, screenPoint.Y + 18);

    private sealed record CapsuleRowDrop(int RowIndex, int ColumnIndex, bool NewRow);
    private sealed record CapsuleDropTarget(string Id, FrameworkElement Surface, int InsertIndex, Rect Marker, CapsuleRowDrop? Row = null);

    private sealed class CapsuleGesture(string item, Point origin)
    {
        private AdornerLayer? _layer;
        private CapsuleDropAdorner? _adorner;
        private FrameworkElement? _targetSurface;
        public string Item { get; } = item;
        public Point Origin { get; } = origin;
        public Window? Preview { get; set; }

        public void ShowTarget(CapsuleDropTarget? target)
        {
            if (_targetSurface != target?.Surface)
            {
                if (_adorner is not null) _layer?.Remove(_adorner);
                _targetSurface = target?.Surface;
                _layer = _targetSurface is null ? null : AdornerLayer.GetAdornerLayer(_targetSurface);
                _adorner = _layer is null || _targetSurface is null ? null : new CapsuleDropAdorner(_targetSurface);
                if (_adorner is not null) _layer?.Add(_adorner);
            }
            if (target is not null) _adorner?.SetMarker(target.Marker);
        }
    }

    private sealed class CapsuleDropAdorner : Adorner
    {
        public static Brush Accent { get; } = FrozenAccent();
        private static readonly Pen Outline = new(Accent, 1.5);
        private Rect _marker = Rect.Empty;

        public CapsuleDropAdorner(UIElement element) : base(element) => IsHitTestVisible = false;

        public void SetMarker(Rect marker)
        {
            if (_marker == marker) return;
            _marker = marker;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            Size size = AdornedElement.RenderSize;
            if (size.Width <= 2 || size.Height <= 2) return;
            drawingContext.DrawRoundedRectangle(null, Outline, new Rect(1, 1, size.Width - 2, size.Height - 2), 15, 15);
            if (!_marker.IsEmpty) drawingContext.DrawRoundedRectangle(Accent, null, _marker, 1, 1);
        }

        private static Brush FrozenAccent()
        {
            var brush = new SolidColorBrush(Color.FromRgb(166, 232, 207));
            brush.Freeze();
            return brush;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CapsuleScreenPoint(int x, int y)
    {
        public readonly int X = x, Y = y;
    }

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr WindowFromCapsulePoint(CapsuleScreenPoint point);
}
