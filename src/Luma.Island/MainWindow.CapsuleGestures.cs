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
        foreach (var tile in CapsuleMetricTiles())
            if (tile.Bounds.Contains(origin)) return tile.Id;
        if (CapsuleElementBounds(CompactMusicRow).Contains(origin) || CapsuleElementBounds(CompactVolume).Contains(origin))
            return "music";
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
        if (moved) owner.MoveCapsuleItem(drag.Item, target?.Id, target?.InsertIndex ?? 0, screenPoint);
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
            if (!window.IsVisible || !window.IslandSurface.IsVisible || new WindowInteropHelper(window).Handle != hit) continue;
            var local = window.IslandSurface.PointFromScreen(screenPoint);
            if (!new Rect(window.IslandSurface.RenderSize).Contains(local)) continue;
            var capsule = draft.Islands.FirstOrDefault(candidate => candidate.Id == id);
            if (capsule is not null) return window.CapsuleInsertion(id, capsule.GetItems(), item, local);
        }
        return null;
    }

    private CapsuleDropTarget CapsuleInsertion(string id, string[] items, string item, Point local)
    {
        Rect header = CapsuleElementBounds(HudHeader);
        Rect metrics = CapsuleElementBounds(CompactPerformance);
        Rect music = CapsuleElementBounds(CompactMusicRow);
        if (music.IsEmpty) music = CapsuleElementBounds(CompactVolume);

        int firstMetric = Array.FindIndex(items, HudCapsuleLayout.IsMetric);
        int lastMetric = Array.FindLastIndex(items, HudCapsuleLayout.IsMetric);
        if (HudCapsuleLayout.IsMetric(item) && firstMetric >= 0 && !metrics.IsEmpty)
        {
            if (metrics.Contains(local))
            {
                var closest = CapsuleMetricTiles().OrderBy(tile => (CapsuleCenter(tile.Bounds) - local).LengthSquared).FirstOrDefault();
                int index = Array.IndexOf(items, closest.Id);
                if (index >= 0)
                {
                    bool after = local.X >= CapsuleCenter(closest.Bounds).X;
                    double x = after ? closest.Bounds.Right : closest.Bounds.Left;
                    return new CapsuleDropTarget(id, this, index + (after ? 1 : 0),
                        new Rect(x - 1, closest.Bounds.Top + 1, 2, Math.Max(1, closest.Bounds.Height - 2)));
                }
            }
            bool below = local.Y >= CapsuleCenter(metrics).Y;
            return new CapsuleDropTarget(id, this, below ? lastMetric + 1 : firstMetric, CapsuleRowMarker(metrics, below));
        }

        var rows = new List<(Rect Bounds, int First, int Last)>(3);
        int headerIndex = Array.IndexOf(items, "header"), musicIndex = Array.IndexOf(items, "music");
        if (!header.IsEmpty && headerIndex >= 0) rows.Add((header, headerIndex, headerIndex));
        if (!metrics.IsEmpty && firstMetric >= 0) rows.Add((metrics, firstMetric, lastMetric));
        if (!music.IsEmpty && musicIndex >= 0) rows.Add((music, musicIndex, musicIndex));
        if (rows.Count == 0) return new CapsuleDropTarget(id, this, items.Length, Rect.Empty);

        rows.Sort((first, second) => first.Bounds.Top.CompareTo(second.Bounds.Top));
        foreach (var row in rows)
            if (local.Y < CapsuleCenter(row.Bounds).Y)
                return new CapsuleDropTarget(id, this, row.First, CapsuleRowMarker(row.Bounds, below: false));
        var last = rows[^1];
        return new CapsuleDropTarget(id, this, last.Last + 1, CapsuleRowMarker(last.Bounds, below: true));
    }

    private Rect CapsuleRowMarker(Rect row, bool below)
    {
        double y = Math.Clamp(below ? row.Bottom + 1 : row.Top - 3, 4, Math.Max(4, IslandSurface.ActualHeight - 6));
        return new Rect(10, y, Math.Max(1, IslandSurface.ActualWidth - 20), 2);
    }

    private IEnumerable<(string? Id, Rect Bounds)> CapsuleMetricTiles()
    {
        if (!CompactPerformance.IsVisible || CompactPerformance.ActualWidth <= 0) yield break;
        GeneralTransform transform = CompactPerformance.TransformToAncestor(IslandSurface);
        foreach (var (id, metric) in new[] { ("cpu", CompactCpu), ("gpu", CompactGpu), ("memory", CompactMemory) })
            if (metric.IsVisible && LayoutInformation.GetLayoutSlot(metric) is { IsEmpty: false } slot)
                yield return (id, transform.TransformBounds(slot));
    }

    private Rect CapsuleElementBounds(FrameworkElement element) => element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0
        ? element.TransformToAncestor(IslandSurface).TransformBounds(new Rect(element.RenderSize)) : Rect.Empty;

    private static Point CapsuleCenter(Rect bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    private Window CreateCapsuleDragPreview(string item, Point screenPoint)
    {
        string text = item switch
        {
            "cpu" => CompactCpu.Text, "gpu" => CompactGpu.Text, "memory" => CompactMemory.Text,
            _ => string.IsNullOrWhiteSpace(CompactTitle.Text) ? "Музыка и звук" : CompactTitle.Text
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

    private sealed record CapsuleDropTarget(string Id, MainWindow Window, int InsertIndex, Rect Marker);

    private sealed class CapsuleGesture(string item, Point origin)
    {
        private AdornerLayer? _layer;
        private CapsuleDropAdorner? _adorner;
        private MainWindow? _targetWindow;
        public string Item { get; } = item;
        public Point Origin { get; } = origin;
        public Window? Preview { get; set; }

        public void ShowTarget(CapsuleDropTarget? target)
        {
            if (_targetWindow != target?.Window)
            {
                if (_adorner is not null) _layer?.Remove(_adorner);
                _targetWindow = target?.Window;
                _layer = target is null ? null : AdornerLayer.GetAdornerLayer(target.Window.IslandSurface);
                _adorner = _layer is null || target is null ? null : new CapsuleDropAdorner(target.Window.IslandSurface);
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
