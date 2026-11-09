using System.Windows;
using System.Windows.Controls;
using Luma.Island;
using Luma.Island.Services;

internal static partial class Program
{
    private static async Task RunContainerChecks()
    {
        var components = ComponentSettings.Default with { Battery = false, Date = true, Time = true };
        using var fixture = new Fixture("capsule-containers", AppearanceSettings.Default, components);
        var window = fixture.Window;
        window.Show(); await Settle();
        var before = window.CaptureHudSettings();
        string[] beforeBytes = fixture.BaseHashes();
        var surface = Named<Border>(window, "IslandSurface");
        var date = Named<TextBlock>(window, "DateLabel");
        var clock = Named<TextBlock>(window, "CompactClock");

        Rect Bounds(FrameworkElement widget) => widget.TransformToAncestor(surface).TransformBounds(new Rect(widget.RenderSize));

        void PutClockByDate()
        {
            var main = window.Appearance.Layout.Capsules!.Islands[0];
            int row = Array.FindIndex(main.GetRows(), items => items.Contains("date"));
            Call(window, "MoveCapsuleItemToRow", "time", HudCapsuleLayout.MainId, row, 1, false);
        }

        Call(window, "BeginCapsuleEdit");
        PutClockByDate(); await Settle();
        var joined = window.Appearance.Layout.Capsules!;
        Check(joined.Islands[0].Rows is not null && joined.Islands[0].GetRows().Any(row => row.SequenceEqual(new[] { "date", "time" })),
            "A clock can join a date in the same explicit row");
        Rect dateBounds = Bounds(date), timeBounds = Bounds(clock);
        Check(dateBounds.Right <= timeBounds.Left + 1 && Math.Max(dateBounds.Top, timeBounds.Top) < Math.Min(dateBounds.Bottom, timeBounds.Bottom),
            "The rendered clock sits to the right of the date on the same line");
        Check(fixture.BaseHashes().SequenceEqual(beforeBytes), "Placing a block in a row is a draft operation");

        Call(window, "MoveCapsuleItemToRow", "time", HudCapsuleLayout.MainId, 0, 0, true); await Settle();
        Check(Bounds(clock).Bottom < Bounds(date).Top && window.Appearance.Layout.Capsules!.Islands[0].GetRows()[0].SequenceEqual(new[] { "time" }),
            "Inserting an upper row separates a previously side-by-side clock");
        Call(window, "EndCapsuleEdit", false); await Settle();
        Check(window.CaptureHudSettings() == before && fixture.BaseHashes().SequenceEqual(beforeBytes) &&
              Bounds(clock).Bottom < Bounds(date).Top, "Cancel restores the original grouping and render tree");

        Call(window, "BeginCapsuleEdit");
        PutClockByDate(); await Settle();
        Call(window, "EndCapsuleEdit", true); await Settle();
        Check(fixture.Appearance.Load().Layout.Capsules == joined && Bounds(date).Right <= Bounds(clock).Left + 1,
            "Done persists the shared row and keeps the joined widgets visible");
        var preview = MainWindow.RenderHudPreview(fixture.Appearance.Load(), components);
        Check(preview.IsFrozen && preview.PixelWidth > 0 && preview.PixelHeight > 0,
            "The settings preview can render a saved custom row without a floating window");
    }
}
