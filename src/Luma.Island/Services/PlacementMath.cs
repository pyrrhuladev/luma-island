using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Luma.Island.Services;

public static class PlacementMath
{
    public static Point Fractions(PlacementSettings settings) => settings.Placement switch
    {
        IslandPlacement.TopLeft => new(0, 0), IslandPlacement.TopCenter => new(.5, 0),
        IslandPlacement.TopRight => new(1, 0), IslandPlacement.MiddleLeft => new(0, .5),
        IslandPlacement.Center => new(.5, .5), IslandPlacement.MiddleRight => new(1, .5),
        IslandPlacement.BottomLeft => new(0, 1), IslandPlacement.BottomCenter => new(.5, 1),
        IslandPlacement.BottomRight => new(1, 1),
        _ => new(FiniteFraction(settings.XFraction, .5), FiniteFraction(settings.YFraction, 0))
    };

    public static Rect Bounds(Rect work, Size size, Point fractions, double margin)
    {
        var x = Axis(work.Left, work.Width, size.Width, margin, fractions.X);
        var y = Axis(work.Top, work.Height, size.Height, margin, fractions.Y);
        return new Rect(x, y, size.Width, size.Height);
    }

    public static Point Capture(Rect work, Rect window, double margin)
    {
        var min = Bounds(work, window.Size, new Point(0, 0), margin);
        var max = Bounds(work, window.Size, new Point(1, 1), margin);
        return new Point(max.X > min.X ? Math.Clamp((window.X - min.X) / (max.X - min.X), 0, 1) : .5,
            max.Y > min.Y ? Math.Clamp((window.Y - min.Y) / (max.Y - min.Y), 0, 1) : 0);
    }

    public static DisplayInfo NearestDisplay(Rect window, IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) throw new ArgumentException("No displays available.", nameof(displays));
        return displays.OrderByDescending(display => IntersectionArea(window, display.WorkArea))
            .ThenBy(display => DistanceSquared(window, display.WorkArea)).First();
    }

    private static double Axis(double origin, double extent, double size, double margin, double fraction)
    {
        double padding = Math.Min(Math.Max(0, margin), Math.Max(0, (extent - size) / 2));
        return origin + padding + Math.Max(0, extent - size - padding * 2) * FiniteFraction(fraction, 0);
    }

    private static double FiniteFraction(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
    private static double IntersectionArea(Rect a, Rect b)
    {
        a.Intersect(b);
        return a.IsEmpty ? 0 : a.Width * a.Height;
    }
    private static double DistanceSquared(Rect window, Rect area)
    {
        double x = window.X + window.Width / 2, y = window.Y + window.Height / 2;
        double dx = x - Math.Clamp(x, area.Left, area.Right), dy = y - Math.Clamp(y, area.Top, area.Bottom);
        return dx * dx + dy * dy;
    }
}
