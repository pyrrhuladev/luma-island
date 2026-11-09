using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Luma.Island;
using Luma.Island.Services;

internal static partial class Program
{
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
    }
}
