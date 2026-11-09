using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Luma.Island;
using Luma.Island.Services;

internal static partial class Program
{
    private static async Task RunWidgetPickerUiChecks()
    {
        using var fixture = new Fixture("widget-picker", AppearanceSettings.Default, ComponentSettings.Default);
        var window = fixture.Window;
        window.Show(); await Settle();
        var original = window.CaptureHudSettings();
        var originalBytes = fixture.BaseHashes();
        var plus = Named<Button>(window, "CapsuleAddWidgetButton");
        Check(!plus.IsVisible && Field<Popup?>(window, "_capsuleWidgetPickerPopup") is null,
            "The widget picker is absent from ordinary HUD rendering and remains lazy");

        Call(window, "BeginCapsuleEdit"); await Settle();
        Check(plus.IsVisible && AutomationProperties.GetName(plus) == "Блоки HUD" &&
              Field<Popup?>(window, "_capsuleWidgetPickerPopup") is null,
            "The screen editor presents an accessible plus without allocating the catalog");

        // Exercise the real WPF popup with no visible surface or desktop mouse capture.
        var picker = (CapsuleWidgetPicker)Call(window, "GetCapsuleWidgetPicker")!;
        var popup = Field<Popup>(window, "_capsuleWidgetPickerPopup");
        picker.Opacity = 0;
        popup.StaysOpen = true;
        Click(plus); await Settle();
        Check(popup.IsOpen && !window.IsActive, "The plus opens the isolated popup without activating the test HUD");

        var options = Named<StackPanel>(picker, "WidgetGroups").Children.OfType<UniformGrid>()
            .SelectMany(group => group.Children.OfType<CheckBox>()).ToDictionary(option => (string)option.Tag);
        Check(options.Count == 18 && options.Values.All(option => !string.IsNullOrEmpty(AutomationProperties.GetName(option))) &&
              new[] { "cpu-chart", "gpu-chart", "memory-chart", "fps-chart", "cpu-temp", "gpu-temp", "memory-temp" }.All(options.ContainsKey),
            "The catalog exposes separately named metrics, charts and temperature switches");
        Check(options["date"].IsChecked == true && options["cpu-chart"].IsChecked == false,
            "The catalog opens with the current widget selection");

        options["date"].IsChecked = false;
        Click(options["date"]);
        options["cpu-chart"].IsChecked = true;
        Click(options["cpu-chart"]);
        Check(popup.IsOpen && !window.Components.Date && !window.Components.Cpu && window.Components.ShowCpuChart &&
              fixture.BaseHashes().SequenceEqual(originalBytes),
            "Multiple catalog toggles affect the visible draft and keep the popup open without saving");

        var popupSource = PresentationSource.FromVisual(picker) ?? throw new InvalidOperationException("The open catalog has no presentation source.");
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, popupSource, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        picker.RaiseEvent(escape); await Settle();
        Check(escape.Handled && !popup.IsOpen && window.IsHudDragging && window.Components.ShowCpuChart,
            "Escape dismisses the catalog without canceling the active draft");

        Click(plus); await Settle();
        Check(popup.IsOpen && options["date"].IsChecked == false && options["cpu-chart"].IsChecked == true,
            "Reopening the catalog reflects the unsaved draft");
        Call(window, "EndCapsuleEdit", false); await Settle();
        Check(!popup.IsOpen && !plus.IsVisible && window.CaptureHudSettings() == original && fixture.BaseHashes().SequenceEqual(originalBytes),
            "Cancel closes the picker and restores the previous HUD without writing preferences");
    }
}
