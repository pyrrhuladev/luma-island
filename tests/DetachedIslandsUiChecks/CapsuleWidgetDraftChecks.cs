using System.Windows.Controls;
using System.Windows;
using Luma.Island.Services;

internal static partial class Program
{
    private static async Task RunWidgetDraftChecks()
    {
        var initial = new ComponentSettings(Music: false, Volume: false, Time: true, Date: false, Battery: false,
            Cpu: true, Charts: true, VolumeWheel: true);
        using var fixture = new Fixture("capsule-widget-draft", AppearanceSettings.Default, initial);
        var window = fixture.Window;
        window.Show(); await Settle();
        string[] initialBytes = fixture.BaseHashes();
        var appearance = window.Appearance;
        var notified = new List<ComponentSettings>();
        window.ComponentsChanged += notified.Add;
        Call(window, "AddCapsuleWidget", "time");
        Check(window.Components == initial && notified.Count == 0, "Widget toggles require an open layout draft");

        Call(window, "BeginCapsuleEdit");
        Call(window, "AddCapsuleWidget", "time");
        Call(window, "RemoveCapsuleWidget", "cpu");
        await Settle();
        var layout = window.Appearance.Layout.Capsules!;
        var secondClock = layout.Islands[0].GetItems().Single(id => id != "time" && HudCapsuleLayout.KindOf(id) == "time");
        Check(!window.Components.Cpu && window.Components.ShowCpuChart && !Named<TextBlock>(window, "CompactCpu").IsVisible &&
              Named<Border>(window, "CpuChartCard").IsVisible && Named<TextBlock>(window, "CompactClock").IsVisible,
            "Removing a number preserves its migrated graph, and both clock instances are in the layout");
        Check(notified.Count == 1 && notified[^1] == window.Components && fixture.BaseHashes().SequenceEqual(initialBytes),
            "Adding a duplicate reuses sampling, and removing the last numeric tile reconfigures it without writing preferences");
        var workArea = window.Placement.CurrentDisplay.WorkArea;
        Call(window, "MoveCapsuleItem", secondClock, null, 0, new Point(workArea.Left + workArea.Width * .6, workArea.Top + workArea.Height * .6));
        await Settle();
        var moved = window.Appearance.Layout.Capsules!;
        Check(moved.Islands.Length == 2 && moved.Islands[1].GetItems().SequenceEqual(new[] { secondClock }) &&
              moved.Islands[0].GetItems().Contains("time"), "Dragging a duplicate moves only its own instance");
        Call(window, "RemoveCapsuleWidget", secondClock); await Settle();
        var removed = window.Appearance.Layout.Capsules!;
        Check(removed.Islands.Length == 1 && removed.Islands[0].GetItems().Contains("time") && window.Components.Time,
            "Removing a detached duplicate prunes its empty capsule and leaves the other clock visible");
        Call(window, "EndCapsuleEdit", false); await Settle();
        Check(window.Components == initial && notified[^1] == initial && fixture.BaseHashes().SequenceEqual(initialBytes) &&
              Named<TextBlock>(window, "CompactCpu").IsVisible && Named<TextBlock>(window, "CompactClock").IsVisible &&
              window.Appearance == appearance,
            "Cancel restores legacy widget visibility, sampling demand and stored preferences");

        Call(window, "BeginCapsuleEdit");
        Call(window, "AddCapsuleWidget", "fps-chart");
        Call(window, "AddCapsuleWidget", "date");
        Call(window, "AddCapsuleWidget", "time");
        window.ApplyFps(new(143, "Fixture", "Fixture")); await Settle();
        Check(!window.Components.Fps && window.Components.ShowFpsChart && Named<Border>(window, "FpsChartCard").IsVisible &&
              Named<TextBlock>(window, "FpsChartValue").Text == "143" && !Named<TextBlock>(window, "CompactFps").IsVisible,
            "Adding an FPS chart shows samples without enabling the numeric FPS tile");
        Call(window, "EndCapsuleEdit", true); await Settle();
        var saved = fixture.Components.Load();
        var savedLayout = fixture.Appearance.Load().Layout.Capsules!;
        Check(saved.ShowFpsChart && saved.Date && !saved.Fps && saved.VolumeWheel && savedLayout.HasInstances &&
              savedLayout.Islands[0].GetItems().Count(id => HudCapsuleLayout.KindOf(id) == "time") == 2 &&
              Named<Border>(window, "FpsChartCard").IsVisible,
            "Done saves both clock instances and their layout alongside the wheel preference");
    }
}
