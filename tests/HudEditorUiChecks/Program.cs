using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static string Output = null!;
    private static int PreviewCalls;
    private static bool ModalWindowsStayedInactive = true;

    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("work", "hud-editor-ui"));
        Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(); }
            catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(exception); }
            finally
            {
                var assembly = typeof(MainWindow).Assembly;
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks,
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                    isolation = "Only in-process, invisible, nonactivating test windows and the production never-shown preview renderer. No App startup, media initialization, SDK session, collectors, tray, global input or real preference writes.",
                    demonstrationData = true, failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} HUD editor UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        var preferenceHashes = Preferences();
        var appearance = new AppearanceSettings(IslandAppearance.Glass, .4, ClickThrough: true);
        var components = ComponentSettings.Default with
        {
            Cpu = true, Gpu = true, Memory = true, Fps = true, Charts = true,
            CpuTemperature = true, GpuTemperature = true, MemoryTemperature = true,
            OnePercentLow = true, FrameTime = true
        };
        var editor = Create(appearance, components, "Тестовая игра");
        try
        {
            Check(editor.DraftAppearance == appearance && editor.DraftComponents == components,
                "Opening the editor preserves the supplied immutable settings");
            Check(PreviewCalls == 1 && editor.PreviewAvailable && !editor.HasPendingPreview,
                "Opening creates exactly one successful production preview without an idle timer");
            Check(Named<TextBlock>(editor, "TargetNameText").Text == "Тестовая игра", "The active save target is visible");
            Check(Named<RadioButton>(editor, "GlassRadio").IsChecked == true && Named<RadioButton>(editor, "MatteRadio").IsChecked == false,
                "The initial surface mode is selected without changing the draft");
            CheckPixels((BitmapSource)Named<Image>(editor, "HudPreviewImage").Source, "The production HUD preview contains rendered alpha pixels");
            ShowInvisible(editor);
            await Settle();
            Check(GetForegroundWindow() != new WindowInteropHelper(editor).Handle,
                "The invisible editor window never becomes the foreground app");
            Check(!Named<StackPanel>(editor, "AlertRulesPanel").IsEnabled,
                "Disabled warnings leave rule controls inactive");
            Check(!Named<Slider>(editor, "MemoryThresholdSlider").IsEnabled,
                "An individually disabled temperature rule has an inactive threshold");

            Named<Slider>(editor, "TextScaleSlider").Value = 115;
            Named<Slider>(editor, "SpacingSlider").Value = 9;
            Named<Slider>(editor, "ScaleSlider").Value = 125;
            Named<Slider>(editor, "TransparencySlider").Value = 90;
            Named<CheckBox>(editor, "CompactCheckBox").IsChecked = true;
            Named<RadioButton>(editor, "ColumnsTwoRadio").IsChecked = true;
            Named<ListBox>(editor, "BlockOrderList").SelectedIndex = 2;
            Click(Named<Button>(editor, "BlockUpButton"));
            Named<ListBox>(editor, "MetricOrderList").SelectedIndex = 0;
            Click(Named<Button>(editor, "MetricDownButton"));
            Named<CheckBox>(editor, "MusicCheckBox").IsChecked = false;
            Named<CheckBox>(editor, "AlertsEnabledCheckBox").IsChecked = true;
            Named<CheckBox>(editor, "AlertMemoryCheckBox").IsChecked = true;
            Named<Slider>(editor, "CpuThresholdSlider").Value = 96;
            Named<Slider>(editor, "GpuThresholdSlider").Value = 89;
            Named<Slider>(editor, "MemoryThresholdSlider").Value = 74;
            Named<Slider>(editor, "FrameThresholdSlider").Value = 55;
            Named<Slider>(editor, "StutterCountSlider").Value = 4;
            Check(PreviewCalls == 1 && editor.HasPendingPreview,
                "A burst of draft edits is coalesced instead of rendering per control event");
            Check(editor.CommitDraft() && PreviewCalls == 2 && !editor.HasPendingPreview,
                "Commit flushes one pending production preview immediately");
            Check(editor.DraftAppearance.Layout.BlockOrder == "header,music,metrics" &&
                  editor.DraftAppearance.Layout.MetricOrder == "gpu,cpu,memory", "The draft remembers both independent reorder lists");
            Check(editor.DraftAppearance.Layout.MetricColumns == 2 && editor.DraftAppearance.Layout.TextScale == 1.15 &&
                  editor.DraftAppearance.Layout.Spacing == 9, "The draft retains text size, spacing and metrics per row");
            Check(editor.DraftAppearance.Mode == IslandAppearance.Glass && editor.DraftAppearance.Compact && editor.DraftAppearance.Scale == 1.25 &&
                  Math.Abs(editor.DraftAppearance.GlassOpacity - .1) < 1e-10,
                "Existing compact, scale and transparency controls participate in the same draft");
            Check(editor.DraftAppearance.ClickThrough, "Editing the HUD leaves the independent click-through preference intact");
            Check(!editor.DraftComponents.Music && editor.DraftComponents.Fps && editor.DraftComponents.OnePercentLow &&
                  editor.DraftComponents.FrameTime && editor.DraftComponents.CpuTemperature,
                "Component edits retain unrelated enabled measurements");
            Check(editor.DraftAppearance.Alerts.Enabled && editor.DraftAppearance.Alerts.MemoryTemperature &&
                  editor.DraftAppearance.Alerts.CpuThreshold == 96 && editor.DraftAppearance.Alerts.GpuThreshold == 89 &&
                  editor.DraftAppearance.Alerts.MemoryThreshold == 74 && editor.DraftAppearance.Alerts.FrameTimeThreshold == 55 &&
                  editor.DraftAppearance.Alerts.StutterCount == 4, "Master switch and every warning threshold are kept in the local draft");
            Check(appearance.Layout == HudLayoutSettings.Default && !appearance.Alerts.Enabled && components.Music,
                "Editing does not mutate the original records or nested defaults");
            await Settle();
            Check(Named<Slider>(editor, "MemoryThresholdSlider").IsEnabled,
                "An enabled master and RAM rule activate the threshold without starting a sensor");
            CheckPixels((BitmapSource)Named<Image>(editor, "HudPreviewImage").Source,
                "Custom rows, reordered blocks and hidden music still produce a nonempty native preview");

            editor.Width = 940; editor.Height = 760; editor.UpdateLayout();
            SaveEditor(editor, "hud-editor-appearance.png");
            Named<TabControl>(editor, "EditorTabs").SelectedItem = Named<TabItem>(editor, "AlertsTab");
            await Settle(); editor.UpdateLayout();
            SaveEditor(editor, "hud-editor-alerts.png");
            editor.Width = 720; editor.Height = 420; editor.UpdateLayout();
            await Settle();
            CheckInside(Named<Button>(editor, "SaveDraftButton"), (FrameworkElement)editor.Content,
                "Save remains inside the visible footer at the smallest editor size");
            CheckInside(Named<Button>(editor, "CancelDraftButton"), (FrameworkElement)editor.Content,
                "Cancel remains inside the visible footer at the smallest editor size");
            Check(Named<Slider>(editor, "StutterCountSlider").IsLoaded,
                "Long warning controls remain available in a scrollable tab");
            SaveEditor(editor, "hud-editor-small.png");
            var alertsScroll = Ancestor<ScrollViewer>(Named<Slider>(editor, "StutterCountSlider"));
            Check(alertsScroll.ScrollableHeight > 0, "The small editor scrolls its warning content instead of clipping controls");
            alertsScroll.ScrollToEnd();
            await Settle(); editor.UpdateLayout();
            CheckInside(Named<Slider>(editor, "StutterCountSlider"), (FrameworkElement)editor.Content,
                "The stutter threshold can be brought into view at the smallest editor size");
            SaveEditor(editor, "hud-editor-small-scrolled.png");

            AlertSettings alertDraft = editor.DraftAppearance.Alerts;
            ComponentSettings componentDraft = editor.DraftComponents;
            Click(Named<Button>(editor, "ResetLayoutButton"));
            Check(editor.DraftAppearance.Layout == HudLayoutSettings.Default && editor.DraftAppearance.Alerts == alertDraft &&
                  editor.DraftComponents == componentDraft && editor.DraftAppearance.ClickThrough && editor.DraftAppearance.Compact,
                "Reset changes only layout and keeps warnings, components and global input choice");
            Check(editor.CommitDraft(), "The default layout can be previewed again after custom arrangement");
            Check(editor.CommitDraft() && PreviewCalls == 3, "An unchanged commit reuses the successfully rendered preview");
        }
        finally { editor.Close(); }

        await CheckAllComponentsAsync();
        await CheckCancellationAsync();
        await CheckAutomaticPreviewAsync();
        await CheckPreviewFailureAsync();
        await CheckAcceptedDialogAsync();
        CheckPreviewScale();
        CheckUnchangedPrecision();
        Check(Preferences().OrderBy(item => item.Key).SequenceEqual(preferenceHashes.OrderBy(item => item.Key)),
            "All real user preference bytes and missing-file states remain unchanged");
        Check(ModalWindowsStayedInactive,
            "Native test windows do not take foreground focus, including when the user changes apps during the checks");
    }

    private static async Task CheckAllComponentsAsync()
    {
        var empty = new ComponentSettings(false, false, false, false, false);
        var editor = Create(AppearanceSettings.Default, empty, "Общий HUD");
        try
        {
            foreach (string name in new[] { "Music", "Volume", "Time", "Date", "Battery", "Cpu", "Gpu", "Memory", "Fps", "Charts",
                "GpuTemperature", "CpuTemperature", "MemoryTemperature", "Low", "FrameTime" })
                Named<CheckBox>(editor, name + "CheckBox").IsChecked = true;
            Check(editor.CommitDraft(), "An initially empty HUD can enable all components together");
            Check(editor.DraftComponents == ComponentSettings.Default with { Cpu = true, Gpu = true, Memory = true, Fps = true,
                Charts = true, GpuTemperature = true, CpuTemperature = true, MemoryTemperature = true, OnePercentLow = true, FrameTime = true },
                "Every one of the fifteen component checkboxes maps to the corresponding stored field");
            foreach (string name in new[] { "Music", "Volume", "Time", "Date", "Battery", "Cpu", "Gpu", "Memory", "Fps", "Charts",
                "GpuTemperature", "CpuTemperature", "MemoryTemperature", "Low", "FrameTime" })
                Named<CheckBox>(editor, name + "CheckBox").IsChecked = false;
            Check(editor.CommitDraft() && editor.DraftComponents == empty,
                "All components can be disabled without breaking the production preview");
        }
        finally { editor.Close(); }
        await Settle();
    }

    private static async Task CheckCancellationAsync()
    {
        int calls = 0;
        var editor = Create(AppearanceSettings.Default, ComponentSettings.Default, "Общий HUD", (appearance, components) =>
        { calls++; return MainWindow.RenderHudPreview(appearance, components); });
        Named<Slider>(editor, "SpacingSlider").Value = 12;
        Check(editor.HasPendingPreview && calls == 1, "Draft changes schedule a deferred preview before cancellation");
        editor.Close();
        await Task.Delay(180);
        Check(!editor.HasPendingPreview && calls == 1 && !editor.CommitDraft(),
            "Closing cancels the pending timer and prevents rendering or committing a closed editor");
    }

    private static async Task CheckPreviewFailureAsync()
    {
        bool fail = true;
        var editor = Create(AppearanceSettings.Default, ComponentSettings.Default, "Общий HUD", (appearance, components) =>
        {
            if (fail) throw new InvalidOperationException("Synthetic preview failure");
            return MainWindow.RenderHudPreview(appearance, components);
        });
        try
        {
            Check(!editor.PreviewAvailable && !editor.CommitDraft() && editor.DialogResult is null,
                "A preview failure stays in the editor and blocks acceptance");
            Check(Named<TextBlock>(editor, "StatusText").Text.Contains("не сохранены", StringComparison.Ordinal),
                "Preview errors explain that settings were not saved");
            fail = false;
            Check(editor.CommitDraft() && editor.PreviewAvailable,
                "A failed preview can recover and validate the same draft");
        }
        finally { editor.Close(); }
        await Settle();
    }

    private static async Task CheckAutomaticPreviewAsync()
    {
        int calls = 0;
        var editor = Create(AppearanceSettings.Default, ComponentSettings.Default, "Общий HUD", (appearance, components) =>
        { calls++; return MainWindow.RenderHudPreview(appearance, components); });
        try
        {
            Named<Slider>(editor, "SpacingSlider").Value = 7;
            Named<Slider>(editor, "SpacingSlider").Value = 8;
            Named<Slider>(editor, "SpacingSlider").Value = 9;
            await Task.Delay(180);
            Check(calls == 2 && !editor.HasPendingPreview && editor.PreviewAvailable,
                "The editor updates one coalesced live preview automatically without Save");
            await Task.Delay(150);
            Check(calls == 2 && !editor.HasPendingPreview,
                "The preview timer stops after rendering and does not poll while idle");
        }
        finally { editor.Close(); }
    }

    private static void CheckUnchangedPrecision()
    {
        foreach (double opacity in new[] { .05, .612345, .4 })
        {
            var appearance = new AppearanceSettings(IslandAppearance.Glass, opacity, Scale: 1.12345)
                { Layout = new HudLayoutSettings(TextScale: 1.11234, Spacing: 7.25) };
            var editor = Create(appearance, ComponentSettings.Default, "Общий HUD");
            try
            {
                Check(editor.CommitDraft() && editor.DraftAppearance == appearance,
                    "An unchanged editor keeps existing fractional values without quantizing saved preferences: " + opacity);
            }
            finally { editor.Close(); }
        }
    }

    private static void CheckPreviewScale()
    {
        var editor = Create(AppearanceSettings.Default, ComponentSettings.Default, "Общий HUD");
        try
        {
            Named<Slider>(editor, "ScaleSlider").Value = 75;
            if (!editor.CommitDraft()) throw new InvalidOperationException("Small-scale preview failed");
            double smallWidth = Named<Image>(editor, "HudPreviewImage").Width;
            Named<Slider>(editor, "ScaleSlider").Value = 150;
            if (!editor.CommitDraft()) throw new InvalidOperationException("Large-scale preview failed");
            double largeWidth = Named<Image>(editor, "HudPreviewImage").Width;
            Check(largeWidth > smallWidth * 1.8 && double.IsFinite(largeWidth),
                "Global HUD scale visibly enlarges the preview against a fixed reference without clipping");
        }
        finally { editor.Close(); }
    }

    private static async Task CheckAcceptedDialogAsync()
    {
        var editor = Create(AppearanceSettings.Default, ComponentSettings.Default, "Общий HUD");
        PrepareInvisible(editor);
        _ = editor.Dispatcher.BeginInvoke(new Action(() =>
        {
            ModalWindowsStayedInactive &= GetForegroundWindow() != new WindowInteropHelper(editor).Handle;
            Named<Slider>(editor, "SpacingSlider").Value = 8;
            Click(Named<Button>(editor, "SaveDraftButton"));
        }), DispatcherPriority.ApplicationIdle);
        Check(editor.ShowDialog() == true && editor.DraftAppearance.Layout.Spacing == 8,
            "Save validates the pending draft and accepts the actual modal dialog");
        await Settle();
        var cancelled = Create(AppearanceSettings.Default, ComponentSettings.Default, "Общий HUD");
        PrepareInvisible(cancelled);
        _ = cancelled.Dispatcher.BeginInvoke(new Action(() =>
        {
            ModalWindowsStayedInactive &= GetForegroundWindow() != new WindowInteropHelper(cancelled).Handle;
            Named<Slider>(cancelled, "SpacingSlider").Value = 11;
            cancelled.Close();
        }), DispatcherPriority.ApplicationIdle);
        Check(cancelled.ShowDialog() != true && cancelled.DraftAppearance.Layout.Spacing == 11,
            "Closing a modified modal draft does not signal acceptance");
    }

    private static HudEditorWindow Create(AppearanceSettings appearance, ComponentSettings components, string target,
        Func<AppearanceSettings, ComponentSettings, BitmapSource>? preview = null) =>
        new(appearance, components, target, preview ?? ((settings, widgets) =>
        { PreviewCalls++; return MainWindow.RenderHudPreview(settings, widgets); }));

    private static void PrepareInvisible(Window window)
    {
        window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Topmost = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000; window.Top = -30000;
    }
    private static void ShowInvisible(Window window) { PrepareInvisible(window); window.Show(); }
    private static void CheckPixels(BitmapSource source, string description)
    {
        Check(source.IsFrozen && source.PixelWidth > 0 && source.PixelHeight > 0, description + " (frozen bitmap)");
        var bytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(bytes, source.PixelWidth * 4, 0);
        Check(Enumerable.Range(0, bytes.Length / 4).Any(index => bytes[index * 4 + 3] > 0), description);
    }
    private static void CheckInside(FrameworkElement element, FrameworkElement root, string description)
    {
        Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Check(element.IsVisible && bounds.Top >= -1 && bounds.Left >= -1 && bounds.Bottom <= root.ActualHeight + 1 &&
            bounds.Right <= root.ActualWidth + 1, description);
    }
    private static void SaveEditor(HudEditorWindow editor, string fileName)
    {
        var root = (FrameworkElement)editor.Content;
        root.UpdateLayout();
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(editor.Background, null, new Rect(0, 0, root.ActualWidth + root.Margin.Left + root.Margin.Right,
                root.ActualHeight + root.Margin.Top + root.Margin.Bottom));
            var brush = new VisualBrush(root) { Stretch = Stretch.Fill };
            context.DrawRectangle(brush, null, new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(root.ActualWidth + root.Margin.Left + root.Margin.Right)),
            Math.Max(1, (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, fileName)); encoder.Save(stream);
    }
    private static Dictionary<string, string?> Preferences()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }.ToDictionary(name => name,
            name => File.Exists(Path.Combine(directory, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))) : null);
    }
    private static T Named<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException("Missing editor element: " + name);
    private static T Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(child); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        throw new InvalidOperationException("Missing ancestor " + typeof(T).Name);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    private static async Task Settle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Check(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException(description);
        Checks.Add(description);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
