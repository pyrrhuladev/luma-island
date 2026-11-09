using System.Windows.Controls;
using Luma.Island.Services;

internal static partial class Program
{
    private static async Task RunWidgetDraftChecks()
    {
        var initial = new ComponentSettings(Music: false, Volume: false, Time: false, Date: false, Battery: false,
            Cpu: true, Charts: true, VolumeWheel: true);
        using var fixture = new Fixture("capsule-widget-draft", AppearanceSettings.Default, initial);
        var window = fixture.Window;
        window.Show(); await Settle();
        string[] initialBytes = fixture.BaseHashes();
        var appearance = window.Appearance;
        var notified = new List<ComponentSettings>();
        window.ComponentsChanged += notified.Add;
        Call(window, "SetCapsuleWidget", "time", true);
        Check(window.Components == initial && notified.Count == 0, "Widget toggles require an open layout draft");

        Call(window, "BeginCapsuleEdit");
        Call(window, "SetCapsuleWidget", "cpu", false);
        Call(window, "SetCapsuleWidget", "time", true);
        await Settle();
        Check(!window.Components.Cpu && window.Components.ShowCpuChart && !Named<TextBlock>(window, "CompactCpu").IsVisible &&
              Named<Border>(window, "CpuChartCard").IsVisible && Named<TextBlock>(window, "CompactClock").IsVisible,
            "Removing a number preserves its inherited graph and adding a clock updates the live HUD");
        Check(notified.Count == 2 && notified[^1] == window.Components && fixture.BaseHashes().SequenceEqual(initialBytes),
            "Widget previews reconfigure sampling without writing preferences");
        Call(window, "EndCapsuleEdit", false); await Settle();
        Check(window.Components == initial && notified[^1] == initial && fixture.BaseHashes().SequenceEqual(initialBytes) &&
              Named<TextBlock>(window, "CompactCpu").IsVisible && !Named<TextBlock>(window, "CompactClock").IsVisible,
            "Cancel restores widget visibility, sampling demand and stored preferences");

        Call(window, "BeginCapsuleEdit");
        Call(window, "SetCapsuleWidget", "fps-chart", true);
        Call(window, "SetCapsuleWidget", "date", true);
        window.ApplyFps(new(143, "Fixture", "Fixture")); await Settle();
        Check(!window.Components.Fps && window.Components.ShowFpsChart && Named<Border>(window, "FpsChartCard").IsVisible &&
              Named<TextBlock>(window, "FpsChartValue").Text == "143" && !Named<TextBlock>(window, "CompactFps").IsVisible,
            "Adding an FPS chart shows samples without enabling the numeric FPS tile");
        Call(window, "EndCapsuleEdit", true); await Settle();
        var saved = fixture.Components.Load();
        Check(saved.ShowFpsChart && saved.Date && !saved.Fps && saved.VolumeWheel && fixture.Appearance.Load() == appearance &&
              Named<Border>(window, "FpsChartCard").IsVisible,
            "Done saves the selected widgets without rewriting an unchanged layout or wheel setting");
    }
}
