using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Luma.Island;
using Luma.Island.Services;

internal static partial class Program
{
    private static async Task RunWidgetPickerUiChecks()
    {
        using var fixture = new Fixture("layout-editor-panels", AppearanceSettings.Default, ComponentSettings.Default);
        var window = fixture.Window;
        window.Show(); await Settle();
        var original = window.CaptureHudSettings();
        string[] originalBytes = fixture.BaseHashes();
        Check(Field<LayoutEditorWindow?>(window, "_layoutPalette") is null && window.FindName("CapsuleAddWidgetButton") is null,
            "The HUD does not allocate an editor or display a plus during normal use");

        Call(window, "BeginCapsuleEdit"); await Settle();
        var palette = Field<LayoutEditorWindow>(window, "_layoutPalette");
        var inspector = Field<LayoutEditorWindow>(window, "_layoutInspector");
        var components = Named<StackPanel>(palette, "ComponentCards").Children.OfType<Border>().ToArray();
        var containers = Named<StackPanel>(palette, "ContainerCards").Children.OfType<Border>().ToArray();
        Check(!palette.IsVisible && !inspector.IsVisible && !window.IsActive && window.Appearance.Layout.Capsules!.HasTree,
            "Opening a hidden fixture constructs isolated editor panels and a migrated tree without activation");
        Check(components.Length == 18 && containers.Select(card => (string)card.Tag).SequenceEqual(new[] { "row", "column", "grid" }),
            "The palette provides eighteen component drag cards and three container drag cards");
        Named<TextBox>(palette, "PaletteSearch").Text = "температура";
        Check(components.Count(card => card.Visibility == Visibility.Visible) == 3,
            "The palette search narrows the list to the three temperature components");
        Named<TextBox>(palette, "PaletteSearch").Clear();

        var clockCard = components.Single(card => (string)card.Tag == "time");
        var source = PresentationSource.FromVisual(window)!;
        for (int i = 0; i < 20; i++)
            clockCard.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        var tree = window.Appearance.Layout.Capsules!;
        Check(tree.Islands.SelectMany(capsule => capsule.GetItems()).Count(id => HudCapsuleLayout.KindOf(id) == "time") == 21 &&
              fixture.BaseHashes().SequenceEqual(originalBytes), "The accessible palette adds twenty unique copies without writing the draft");
        Call(window, "UndoCapsuleEdit");
        Check(window.Appearance.Layout.Capsules!.Islands.SelectMany(capsule => capsule.GetItems()).Count(id => HudCapsuleLayout.KindOf(id) == "time") == 20,
            "A single undo removes only the most recently added clock");
        Call(window, "RedoCapsuleEdit");
        Call(window, "AddCapsuleWidget", "grid");
        var layout = window.Appearance.Layout.Capsules!;
        var grid = layout.Islands.SelectMany(capsule => capsule.Root!.DescendantsAndSelf()).Last(node => node.Kind == "grid");
        Call(window, "SelectCapsuleNode", grid.Id);
        Check(Named<StackPanel>(inspector, "ContainerProperties").Visibility == Visibility.Visible &&
              Named<StackPanel>(inspector, "ColumnsField").Visibility == Visibility.Visible,
            "Selecting a grid displays its layout properties in the inspector");
        Named<Slider>(inspector, "ColumnsSlider").Value = 3;
        Named<Slider>(inspector, "GapSlider").Value = 11;
        Check(window.Appearance.Layout.Capsules!.GetNode(grid.Id) is { Columns: 3, Gap: 11 },
            "Changing inspector controls updates the real grid draft");
        Call(window, "UndoCapsuleEdit");
        Check(window.Appearance.Layout.Capsules!.GetNode(grid.Id)?.Gap == 6,
            "The inspector property change participates in editor history");
        Call(window, "DuplicateCapsuleSelection");
        Check(window.Appearance.Layout.Capsules!.Islands.SelectMany(capsule => capsule.Root!.DescendantsAndSelf()).Count(node => node.Kind == "grid") == 2,
            "The inspector can duplicate an empty container");
        Call(window, "RemoveCapsuleSelection");
        Check(window.Appearance.Layout.Capsules!.GetNode(grid.Id) is null,
            "Delete removes the selected empty container without affecting its copy");

        var remainingGrid = window.Appearance.Layout.Capsules!.Islands.SelectMany(capsule => capsule.Root!.DescendantsAndSelf()).Single(node => node.Kind == "grid");
        Call(window, "SelectCapsuleNode", remainingGrid.Id);

        RenderEditorPanel(palette, "layout-palette.png");
        RenderEditorPanel(inspector, "layout-inspector.png");
        Call(window, "EndCapsuleEdit", false); await Settle();
        Check(Field<LayoutEditorWindow?>(window, "_layoutPalette") is null && Field<LayoutEditorWindow?>(window, "_layoutInspector") is null &&
              window.CaptureHudSettings() == original && fixture.BaseHashes().SequenceEqual(originalBytes),
            "Cancel disposes both panels and restores every original store and layout");
    }

    private static void RenderEditorPanel(Window panel, string name)
    {
        var content = (FrameworkElement)panel.Content;
        content.Measure(new Size(270, 660));
        content.Arrange(new Rect(0, 0, 270, 660));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(270, 660, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(panel.Background, null, new Rect(0, 0, 270, 660));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, name));
        encoder.Save(stream);
    }
}
