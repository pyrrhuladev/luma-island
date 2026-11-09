using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Luma.Island;
using Luma.Island.Services;

internal static partial class Program
{
    private static void CheckCapsuleGestureGeometry(MainWindow window)
    {
        var surface = Named<Border>(window, "IslandSurface");
        Point Center(string name)
        {
            var element = Named<FrameworkElement>(window, name);
            return element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), surface);
        }

        foreach (var (name, id) in new[] { ("CompactCpu", "cpu"), ("CompactGpu", "gpu"), ("CompactMemory", "memory"), ("CompactMusicRow", "music") })
            Check(Equals(Call(window, "CapsuleItemAt", Center(name)), id), $"The visible {id} tile is a drag source at its actual arranged center");
        Check(Call(window, "CapsuleItemAt", Center("HudHeader")) is null &&
              Call(window, "CapsuleItemAt", new Point(-1, -1)) is null,
            "The fixed header and space outside the capsule are not widget drag sources");

        string[] items = window.Appearance.Layout.Capsules!.Islands[0].GetItems();
        Point cpu = Center("CompactCpu");
        var beforeCpu = Insert("gpu", new Point(cpu.X - 4, cpu.Y));
        var afterCpu = Insert("gpu", new Point(cpu.X + 4, cpu.Y));
        Check(beforeCpu.Index == Array.IndexOf(items, "cpu") && afterCpu.Index == beforeCpu.Index + 1 &&
              beforeCpu.Marker.Left < afterCpu.Marker.Left && beforeCpu.Marker.Width < beforeCpu.Marker.Height,
            "Dropping to either side of the CPU cell selects the corresponding hardware insertion edge");

        var hardware = Named<UniformGrid>(window, "CompactPerformance");
        Point hardwareTop = hardware.TranslatePoint(new Point(hardware.ActualWidth / 2, 2), surface);
        var beforeHardware = Insert("music", hardwareTop);
        Check(beforeHardware.Index == Array.IndexOf(items, "cpu") && beforeHardware.Marker.Width > beforeHardware.Marker.Height,
            "Moving music toward the upper half of the hardware band previews insertion above the complete group");

        (int Index, Rect Marker) Insert(string id, Point pointer)
        {
            object target = Call(window, "CapsuleInsertion", HudCapsuleLayout.MainId, items, id, pointer)!;
            var targetType = target.GetType();
            return ((int)targetType.GetProperty("InsertIndex")!.GetValue(target)!, (Rect)targetType.GetProperty("Marker")!.GetValue(target)!);
        }
    }
}
