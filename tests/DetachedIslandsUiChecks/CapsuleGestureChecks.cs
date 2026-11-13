using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Reflection;
using Luma.Island;
using Luma.Island.Services;

internal static partial class Program
{
    private static async Task RunTreeGestureChecks()
    {
        var firstClock = HudLayoutNode.Create("time", "time");
        var secondClock = HudLayoutNode.Create("time");
        var date = HudLayoutNode.Create("date", "date");
        var row = HudLayoutNode.Create("row") with { Children = [firstClock, secondClock] };
        var root = HudLayoutNode.Create("column") with { Children = [row, date] };
        var layout = HudCapsuleLayout.Normalize(new([new(HudCapsuleLayout.MainId, string.Empty) { Root = root }]) { HasInstances = true });
        var appearance = AppearanceSettings.Default with { Layout = HudLayoutSettings.Default with { Capsules = layout } };
        using var fixture = new Fixture("tree-gestures", appearance,
            ComponentSettings.Default with { Music = false, Volume = false, Battery = false });
        var window = fixture.Window;
        window.Show(); await Settle();
        var hashes = fixture.BaseHashes();
        Call(window, "BeginCapsuleEdit"); await Settle();
        var before = window.Appearance.Layout.Capsules!;
        var clockBounds = (Rect)Call(window, "CapsuleNodeBounds", secondClock.Id)!;
        var gesture = NewTreeGesture(date.Id, before);
        SetPrivate(gesture, "Started", true);
        typeof(MainWindow).GetField("_treeGesture", Private)!.SetValue(window, gesture);
        object target = Call(window, "TreeInsertion", gesture, HudCapsuleLayout.MainId, before.GetNode(row.Id)!,
            new Point(clockBounds.Left + 1, clockBounds.Top + clockBounds.Height / 2))!;
        Call(window, "SetTreeGestureTarget", gesture, target); await Settle();
        var live = window.Appearance.Layout.Capsules!;
        var first = (Rect)Call(window, "CapsuleNodeBounds", firstClock.Id)!;
        var moved = (Rect)Call(window, "CapsuleNodeBounds", date.Id)!;
        var second = (Rect)Call(window, "CapsuleNodeBounds", secondClock.Id)!;
        Check(live.GetNode(row.Id)!.GetChildren().Select(node => node.Id).SequenceEqual(new[] { firstClock.Id, date.Id, secondClock.Id }) &&
              first.Right <= moved.Left + 1 && moved.Right <= second.Left + 1,
            "A target preview reflows the actual neighboring widgets before the pointer is released");
        Check(fixture.BaseHashes().SequenceEqual(hashes) && Field<List<HudProfileSettings>>(window, "_capsuleUndo").Count == 0,
            "Live drag previews write neither preference files nor history entries");
        Call(window, "CancelTreeGesture"); await Settle();
        Check(window.Appearance.Layout.Capsules == before && Field<List<HudProfileSettings>>(window, "_capsuleUndo").Count == 0,
            "Canceling the active tree gesture restores the complete pre-drag tree");

        gesture = NewTreeGesture(date.Id, before);
        SetPrivate(gesture, "Started", true);
        typeof(MainWindow).GetField("_treeGesture", Private)!.SetValue(window, gesture);
        Call(window, "SetTreeGestureTarget", gesture, target); await Settle();
        var committed = window.Appearance.Layout.Capsules!;
        Call(window, "ReleaseTreeGesture", gesture);
        Call(window, "CommitCapsuleGesture", before);
        Check(Field<List<HudProfileSettings>>(window, "_capsuleUndo").Count == 1 && window.Appearance.Layout.Capsules == committed,
            "Committing a tree drag adds one history entry for the entire move");
        Call(window, "UndoCapsuleEdit"); await Settle();
        Check(window.Appearance.Layout.Capsules == before, "Undo restores the tree from before the complete drag");
        Call(window, "RedoCapsuleEdit"); await Settle();
        Check(window.Appearance.Layout.Capsules == committed, "Redo restores the committed nested placement");

        var added = HudLayoutNode.Create("time");
        gesture = NewTreeGesture(added.Id, committed, added);
        SetPrivate(gesture, "Started", true);
        typeof(MainWindow).GetField("_treeGesture", Private)!.SetValue(window, gesture);
        target = NewTreeTarget(window, HudCapsuleLayout.MainId, root.Id, 0);
        Call(window, "SetTreeGestureTarget", gesture, target); await Settle();
        Check(window.Appearance.Layout.Capsules!.GetNode(added.Id) is not null &&
              window.Appearance.Layout.Capsules.GetNode(firstClock.Id) is not null && window.Appearance.Layout.Capsules.GetNode(secondClock.Id) is not null,
            "Dragging a clock from the palette previews a fresh instance alongside the existing clocks");
        Call(window, "CancelTreeGesture"); await Settle();
        Check(window.Appearance.Layout.Capsules == committed && fixture.BaseHashes().SequenceEqual(hashes),
            "Canceling a palette drag discards only its unsaved new instance");

        gesture = NewTreeGesture(added.Id, committed, added);
        SetPrivate(gesture, "Started", true);
        typeof(MainWindow).GetField("_treeGesture", Private)!.SetValue(window, gesture);
        var area = window.Placement.CurrentDisplay.WorkArea;
        var detached = (HudCapsuleLayout)Call(window, "DetachTreeGesture", gesture,
            new Point(area.Left + area.Width * .7, area.Top + area.Height * .6))!;
        Call(window, "ReleaseTreeGesture", gesture);
        Call(window, "PreviewCapsuleLayout", detached);
        Call(window, "CommitCapsuleGesture", committed); await Settle();
        Check(detached.Islands.Length == 2 && CapsuleWindows(window).Length == 2 && detached.GetNode(added.Id) is not null,
            "A palette drop on free space creates and positions a new capsule with the fresh instance");

        gesture = NewTreeGesture(added.Id, detached);
        SetPrivate(gesture, "Started", true);
        typeof(MainWindow).GetField("_treeGesture", Private)!.SetValue(window, gesture);
        target = NewTreeTarget(window, HudCapsuleLayout.MainId, root.Id, 0);
        Call(window, "SetTreeGestureTarget", gesture, target); await Settle();
        Check(CapsuleWindows(window).Length == 1 && ReferenceEquals(Field<object>(window, "_treeGesture"), gesture),
            "Reflow can close an empty source capsule without canceling the editor-owned gesture");
        Call(window, "CancelTreeGesture"); await Settle();
        Check(window.Appearance.Layout.Capsules == detached && CapsuleWindows(window).Length == 2,
            "Cancel restores a source capsule that disappeared during the live preview");
        Call(window, "EndCapsuleEdit", false); await Settle();
    }

    private static object NewTreeGesture(string id, HudCapsuleLayout layout, HudLayoutNode? added = null)
    {
        var type = typeof(MainWindow).GetNestedType("TreeGesture", BindingFlags.NonPublic)!;
        return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single()
            .Invoke(new object?[] { id, layout, new Point(), added });
    }

    private static object NewTreeTarget(MainWindow window, string capsuleId, string parentId, int index)
    {
        var type = typeof(MainWindow).GetNestedType("TreeDropTarget", BindingFlags.NonPublic)!;
        return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(constructor => constructor.GetParameters().Length == 5).Invoke(new object[] { window, capsuleId, parentId, index, Rect.Empty });
    }

    private static void SetPrivate(object target, string property, object value) =>
        target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void CheckCapsuleGestureGeometry(MainWindow window)
    {
        Call(window, "ApplyBattery", new BatterySnapshot(true, 63, false));
        window.UpdateLayout();
        var surface = Named<Border>(window, "IslandSurface");
        Point Center(string name)
        {
            var element = Named<FrameworkElement>(window, name);
            return element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), surface);
        }

        var tiles = new[]
        {
            ("CompactCpu", "cpu"), ("CompactGpu", "gpu"), ("CompactMemory", "memory"), ("CompactMusicRow", "music"),
            ("CompactFpsPanel", "fps"), ("CompactLowPanel", "low"), ("CompactClock", "time"), ("CompactBattery", "battery"),
            ("FrameTimeCard", "frame-time"), ("VolumeLabel", "volume"), ("DateLabel", "date"),
            ("CpuChartCard", "cpu-chart"), ("GpuChartCard", "gpu-chart"), ("MemoryChartCard", "memory-chart"), ("FpsChartCard", "fps-chart")
        };
        foreach (var (name, id) in tiles)
            Check(Equals(Call(window, "CapsuleItemAt", Center(name)), id), $"The visible {id} tile is a drag source at its actual arranged center");
        Check(Call(window, "CapsuleItemAt", new Point(-1, -1)) is null,
            "Space outside the capsule is not a widget drag source");

        // Nested containers use their own per-node insertion geometry. The classic
        // group checks below remain applicable to saved pre-tree layouts only.
        if (window.Appearance.Layout.Capsules?.HasTree == true) return;

        HudCapsule capsule = window.Appearance.Layout.Capsules!.Islands[0];
        string[] items = capsule.GetItems();
        CheckInline("gpu", "cpu", "hardware");
        CheckInline("low", "fps", "header");
        CheckInline("gpu-chart", "cpu-chart", "mini-chart");

        var hardware = Named<UniformGrid>(window, "CompactPerformance");
        Point hardwareTop = hardware.TranslatePoint(new Point(hardware.ActualWidth / 2, 2), surface);
        var beforeHardware = Insert("music", hardwareTop);
        Check(beforeHardware.Index == Array.IndexOf(items, "cpu") && beforeHardware.Marker.Width > beforeHardware.Marker.Height,
            "Moving music toward the upper half of the hardware band previews insertion above the complete group");

        var frameTime = Named<Border>(window, "FrameTimeCard");
        var beforeFrameTime = Insert("volume", frameTime.TranslatePoint(new Point(frameTime.ActualWidth / 2, 2), surface));
        Check(beforeFrameTime.Index == Array.IndexOf(items, "frame-time") && beforeFrameTime.Marker.Width > beforeFrameTime.Marker.Height,
            "The volume label can move the volume row independently of the player");

        var date = (Rect)Call(window, "CapsuleWidgetBounds", "date")!;
        int dateRow = Array.FindIndex(capsule.GetRows(), row => row.Contains("date"));
        double middleY = date.Top + date.Height / 2;
        var rightOfDate = Drop("time", new Point(date.Right - 4, middleY));
        var leftOfDate = Drop("time", new Point(date.Left + 4, middleY));
        Check(rightOfDate is { RowIndex: var rightRow, ColumnIndex: 1, NewRow: false } && rightRow == dateRow &&
              leftOfDate is { RowIndex: var leftRow, ColumnIndex: 0, NewRow: false } && leftRow == dateRow,
            "The arranged date's two side edges can accept the clock in the same row");
        var aboveDate = Drop("time", new Point(date.Left + date.Width / 2, date.Top + 1));
        Check(aboveDate is { RowIndex: var aboveRow, NewRow: true } && aboveRow == dateRow,
            "The upper edge can place a formerly grouped clock on its own row above the date");

        HudCapsuleLayout original = window.Appearance.Layout.Capsules!;
        try
        {
            Call(window, "MoveCapsuleItemToRow", "time", capsule.Id, dateRow, 1, false);
            window.UpdateLayout();
            HudCapsule arranged = window.Appearance.Layout.Capsules!.Islands[0];
            var dateCell = (Rect)Call(window, "CapsuleWidgetBounds", "date")!;
            var clockCell = (Rect)Call(window, "CapsuleWidgetBounds", "time")!;
            Check(arranged.GetRows().Any(row => row.SequenceEqual(new[] { "date", "time" })) &&
                  !dateCell.IsEmpty && !clockCell.IsEmpty && dateCell.Right <= clockCell.Left + 1 &&
                  Math.Abs(dateCell.Top - clockCell.Top) < 1,
                "The date and clock actually arrange left to right in one row");
            Check(Equals(Call(window, "CapsuleItemAt", new Point(clockCell.Left + clockCell.Width / 2, clockCell.Top + clockCell.Height / 2)), "time"),
                "The clock remains a distinct drag source inside the custom row");
            int customDateRow = Array.FindIndex(arranged.GetRows(), row => row.Contains("date"));
            var rightOfClock = Drop("cpu", new Point(clockCell.Right - 4, clockCell.Top + clockCell.Height / 2), arranged);
            var aboveDateRow = Drop("cpu", new Point(dateCell.Left + dateCell.Width / 2, dateCell.Top + 1), arranged);
            Check(rightOfClock is { RowIndex: var inlineRow, ColumnIndex: 2, NewRow: false } && inlineRow == customDateRow &&
                  aboveDateRow is { RowIndex: var verticalRow, NewRow: true } && verticalRow == customDateRow,
                "A custom row accepts side insertion and its upper edge creates a distinct row");
        }
        finally
        {
            Call(window, "ApplyCapsuleDraft", original);
            window.UpdateLayout();
        }

        void CheckInline(string moved, string target, string group)
        {
            var tile = (Rect)Call(window, "CapsuleWidgetBounds", target)!;
            Check(!tile.IsEmpty, $"The {target} tile has a drop slot");
            double x = tile.Left + tile.Width / 2, y = tile.Top + tile.Height / 2;
            var before = Insert(moved, new Point(x - 4, y));
            var after = Insert(moved, new Point(x + 4, y));
            Check(before.Index == Array.IndexOf(items, target) && after.Index == before.Index + 1 &&
                  before.Marker.Left < after.Marker.Left && before.Marker.Width < before.Marker.Height,
                $"Dropping to either side of {target} previews the corresponding {group} insertion edge");
        }

        (int Index, Rect Marker) Insert(string id, Point pointer)
        {
            object target = Call(window, "CapsuleInsertion", capsule, id, pointer)!;
            var targetType = target.GetType();
            return ((int)targetType.GetProperty("InsertIndex")!.GetValue(target)!, (Rect)targetType.GetProperty("Marker")!.GetValue(target)!);
        }

        (int RowIndex, int ColumnIndex, bool NewRow)? Drop(string id, Point pointer, HudCapsule? destination = null)
        {
            object? target = Call(window, "CapsuleDropTargetAt", destination ?? capsule, id, surface.PointToScreen(pointer));
            object? row = target?.GetType().GetProperty("Row")!.GetValue(target);
            if (row is null) return null;
            var type = row.GetType();
            return ((int)type.GetProperty("RowIndex")!.GetValue(row)!, (int)type.GetProperty("ColumnIndex")!.GetValue(row)!,
                (bool)type.GetProperty("NewRow")!.GetValue(row)!);
        }
    }
}
