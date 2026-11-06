using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;
using Luma.Island.Settings;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private static string Output = null!;
    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : "work/checks/SettingsNavigationUiChecks");
        Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(); }
            catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(error); }
            finally
            {
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, completedChecks = Checks, failure,
                    assemblySha256 = Hash(typeof(MainWindow).Assembly.Location), demonstrationData = true,
                    isolation = "Invisible in-process settings windows, synthetic application context and never-shown production preview. No App startup, SDK, sensor pipe, global input, registry/service/driver or real preference writes."
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        if (failure is null) Console.WriteLine($"PASS: {Checks.Count} settings navigation UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        var preferences = RealPreferences();
        var placement = PlacementSettings.Default with { Placement = IslandPlacement.Custom, XFraction = .43123456789, YFraction = .287654321 };
        string activeId = Guid.NewGuid().ToString("D");
        var profiles = new GameProfileSettings(true, new[] { new GameProfile(activeId, "Тестовая игра", @"D:\Games\Example\game.exe", HudProfileSettings.Default) });
        var context = new SettingsEditorContext(placement, HudProfileSettings.Default, profiles, activeId,
            new[] { new DisplayInfo("main", "Основной экран", new Rect(0, 0, 1920, 1080), true),
                new DisplayInfo("left", "Дополнительный экран", new Rect(-1280, 0, 1280, 1024), false) },
            new AutostartState(true, true));
        int previews = 0;
        var widgets = ComponentSettings.Default with { Cpu = true, Gpu = true, Memory = true, Fps = true, OnePercentLow = true,
            CpuTemperature = true, GpuTemperature = true, MemoryTemperature = true };
        var editor = new HudEditorWindow(AppearanceSettings.Default, widgets, "Тестовая игра",
            (appearance, components) => { previews++; return MainWindow.RenderHudPreview(appearance, components); }, context);
        try
        {
            Check(editor.Sections.Select(section => section.Id).SequenceEqual(new[] { "appearance", "layout", "components", "alerts", "placement", "profiles", "application" }), "Seven clear settings sections share one registry");
            Check(editor.SelectedSectionId == "appearance" && !editor.HasUnsavedChanges, "Opening the default page leaves every draft unchanged");
            Check(editor.DraftApplication?.Placement == placement && editor.DraftApplication.Profiles.Profiles.SequenceEqual(profiles.Profiles), "Application draft preserves exact position and profile records");
            Check(editor.FindSettingsControl("PlacementMonitorCombo") is null && editor.FindSettingsControl("AppAutostartCheckBox") is null, "Application page trees remain unconstructed until first navigation");
            Check(previews == 1 && !editor.HasPendingPreview, "Navigation starts with one production preview and no idle renderer");
            Prepare(editor); editor.Show(); await Settle();
            Check(editor.InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == Key.F && binding.Modifiers == ModifierKeys.Control && binding.Command == ApplicationCommands.Find), "Search has a window-scoped Ctrl+F command without a global hotkey");
            foreach (string id in editor.Sections.Select(section => section.Id).ToArray())
            {
                Check(editor.SelectSection(id) && editor.SelectedSectionId == id, "Navigation selects " + id);
                Check(Named<TextBlock>(editor, "ActiveSectionTitle").Text == editor.Sections.Single(section => section.Id == id).Title, "Page heading matches selected section " + id);
            }
            Check(!editor.HasUnsavedChanges && previews == 1 && !editor.HasPendingPreview, "Visiting every page changes neither preferences nor preview sampling");
            Check(editor.FindSettingsControl("PlacementMonitorCombo") is ComboBox && editor.FindSettingsControl("AppAutostartCheckBox") is CheckBox, "Lazy application controls are discoverable after their sections are visited");
            editor.SelectSection("placement"); await Settle();
            var monitorPicker = (ComboBox)editor.FindSettingsControl("PlacementMonitorCombo")!;
            var selectedLabels = VisualText(monitorPicker).ToArray();
            Check(selectedLabels.Any(text => text.Text.Contains("Основной монитор", StringComparison.Ordinal)) &&
                selectedLabels.All(text => !text.Text.Contains("MonitorChoice", StringComparison.Ordinal) && (text.Foreground as SolidColorBrush)?.Color != Colors.Black),
                "The themed monitor picker renders its display label in readable foreground colors");
            Check(((CheckBox)editor.FindSettingsControl("AppAutostartCheckBox")!).IsChecked == true && editor.DraftApplication?.AutostartChanged == false,
                "A Windows-disabled registration stays registered without an implicit startup change");
            Check(editor.SearchSections("ТЕМПЕРАТУРА").Select(section => section.Id).OrderBy(id => id).SequenceEqual(new[] { "alerts", "components" }), "Search matches Russian setting keywords regardless of case");
            Check(editor.SearchSections("горячие клавиши").Single().Id == "application" && editor.SelectedSectionId == "application", "Multi-word search navigates directly to the matching section");
            Check(editor.SearchSections("оперативка").Single().Id == "components", "Common Russian RAM wording finds component settings");
            Check(editor.SearchSections("фпс").Single().Id == "components", "Russian FPS wording finds the frame counter");
            Check(editor.SearchSections("импорт").Count == 0, "Search does not advertise an unavailable import feature");
            editor.SelectSection("application");
            editor.SearchSections("температура");
            var search = Named<TextBox>(editor, "SettingsSearchBox");
            var clearSearch = Named<Button>(editor, "ClearSearchButton");
            var resultLabel = Named<TextBlock>(editor, "SearchResultLabel");
            Check(clearSearch.IsVisible && resultLabel.Text.EndsWith("· 2", StringComparison.Ordinal),
                "A matching query offers a clear action and shows the number of matching sections");
            var enterSearch = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search), Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            search.RaiseEvent(enterSearch);
            Check(enterSearch.Handled && editor.IsVisible && editor.DialogResult is null &&
                  editor.SelectedSectionId == "components" && !editor.HasUnsavedChanges,
                "Enter in search accepts the matching section without invoking the default Save action");
            Click(clearSearch);
            Check(search.Text.Length == 0 && editor.FilteredSections.Count == 7 && !clearSearch.IsVisible &&
                  resultLabel.Text == "РАЗДЕЛЫ" && editor.SelectedSectionId == "components",
                "Clearing search restores all sections and keeps the current page selected");
            editor.SelectSection("application");
            string? previous = editor.SelectedSectionId;
            Check(editor.SearchSections("нет-такого-раздела-123").Count == 0 && Named<TextBlock>(editor, "NoSearchResults").Visibility == Visibility.Visible,
                "An unmatched query shows a clear empty search state");
            Check(editor.SelectedSectionId == previous && previews == 1 && !editor.HasUnsavedChanges, "Empty search preserves current content and drafts");
            Check(editor.SearchSections(null).Count == 7 && Named<TextBlock>(editor, "NoSearchResults").Visibility == Visibility.Collapsed, "Clearing search restores all sections");
            Check(!editor.SelectSection("missing"), "Unknown section IDs do not alter navigation");
            var profileName = (TextBox)editor.FindSettingsControl("SettingsProfileNameBox")!;
            editor.SelectSection("profiles"); await Settle();
            string originalName = profileName.Text;
            profileName.SetCurrentValue(TextBox.TextProperty, "Ошибка\u0001");
            profileName.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            editor.SearchSections("стекло");
            Check(!editor.CommitDraft() && editor.SelectedSectionId == "profiles" && Named<TextBox>(editor, "SettingsSearchBox").Text.Length == 0,
                "A profile validation error clears filtering and returns to the profile page");
            profileName.SetCurrentValue(TextBox.TextProperty, originalName);
            profileName.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Check(editor.CommitDraft() && !editor.HasUnsavedChanges, "Correcting a profile validation error restores the original local draft");

            var previewSurface = Named<Border>(editor, "PreviewSurface");
            Brush darkBackdrop = previewSurface.Background;
            var lightPreview = Named<RadioButton>(editor, "LightPreviewRadio");
            var darkPreview = Named<RadioButton>(editor, "DarkPreviewRadio");
            string beforeBackdrop = DraftSnapshot(editor);
            var previewImage = Named<Image>(editor, "HudPreviewImage");
            ImageSource beforePreview = previewImage.Source;
            lightPreview.IsChecked = true;
            await Task.Delay(180); await Settle();
            Check(darkPreview.IsChecked == false && !ReferenceEquals(previewSurface.Background, darkBackdrop),
                "The light backdrop switches the actual preview surface");
            darkPreview.IsChecked = true;
            await Settle();
            Check(lightPreview.IsChecked == false && ReferenceEquals(previewSurface.Background, darkBackdrop) &&
                  ReferenceEquals(previewImage.Source, beforePreview) && previews == 1 && !editor.HasPendingPreview &&
                  !editor.HasUnsavedChanges && DraftSnapshot(editor) == beforeBackdrop,
                "Backdrop changes remain local to the preview and neither alter the draft nor render the HUD again");

            editor.SelectSection("layout");
            Named<RadioButton>(editor, "ColumnsTwoRadio").IsChecked = true;
            editor.SelectSection("components"); editor.SelectSection("layout");
            Check(Named<RadioButton>(editor, "ColumnsTwoRadio").IsChecked == true && editor.HasUnsavedChanges, "Unsaved page values survive navigation");
            await Task.Delay(180); await Settle();
            Check(previews == 2 && !editor.HasPendingPreview, "A HUD edit renders one debounced preview after navigation");
            await Task.Delay(150);
            Check(previews == 2, "Idle settings do not keep rendering the HUD");
            editor.Width = 1120; editor.Height = 780; editor.SelectSection("appearance"); await Settle();
            Check(Named<Border>(editor, "PreviewPane").Visibility == Visibility.Visible, "Wide settings keep the live preview beside the page");
            Save(editor, "settings-appearance.png");
            double sidePreviewWidth = Named<Image>(editor, "HudPreviewImage").ActualWidth;
            Click(Named<Button>(editor, "ExpandPreviewButton")); await Settle();
            Check(Named<Grid>(editor, "SettingsContentPanel").Visibility == Visibility.Collapsed && Named<Image>(editor, "HudPreviewImage").ActualWidth > sidePreviewWidth,
                "The side preview can expand into a readable full-width view");
            Inside(Named<Image>(editor, "HudPreviewImage"), (FrameworkElement)editor.Content, "The enlarged wide preview stays inside the window");
            editor.SelectSection("layout"); await Settle(); Save(editor, "settings-layout.png");
            editor.SelectSection("components"); await Settle(); Save(editor, "settings-components.png");
            editor.SelectSection("placement"); await Settle(); Save(editor, "settings-placement.png");
            editor.SelectSection("profiles"); await Settle(); Save(editor, "settings-profiles.png");
            editor.SelectSection("application"); await Settle(); Save(editor, "settings-application.png");
            editor.Height = 420; await Settle();
            Check(Named<Border>(editor, "PreviewPane").Visibility == Visibility.Collapsed && Named<Grid>(editor, "SettingsContentPanel").IsVisible,
                "A wide but short settings window hides the side preview");
            Click(Named<Button>(editor, "TogglePreviewButton")); await Settle();
            var settingsBody = Named<Grid>(editor, "SettingsBody");
            Inside(previewImage, settingsBody, "The wide short focused preview image fits in the settings body");
            Inside(Named<TextBlock>(editor, "PreviewDataCaption"), settingsBody, "The wide short preview caption fits above the footer");
            Inside(Named<Button>(editor, "SaveDraftButton"), (FrameworkElement)editor.Content, "Save remains visible in the wide short focused preview");
            editor.Width = 760; editor.Height = 420; editor.SelectSection("alerts"); await Settle();
            Check(Named<Border>(editor, "PreviewPane").Visibility == Visibility.Collapsed && Named<Grid>(editor, "SettingsContentPanel").Visibility == Visibility.Visible,
                "Narrow windows preserve page width by hiding the side preview");
            Inside(Named<Button>(editor, "SaveDraftButton"), (FrameworkElement)editor.Content, "Save remains visible in the 760 by 420 footer");
            Inside(Named<Button>(editor, "CancelDraftButton"), (FrameworkElement)editor.Content, "Cancel remains visible in the 760 by 420 footer");
            Click(Named<Button>(editor, "TogglePreviewButton")); await Settle();
            Check(Named<Border>(editor, "PreviewPane").Visibility == Visibility.Visible && Named<Grid>(editor, "SettingsContentPanel").Visibility == Visibility.Collapsed,
                "The small window can switch to a full-width preview");
            Inside(Named<Image>(editor, "HudPreviewImage"), (FrameworkElement)editor.Content, "The small preview image fits above the footer");
            Inside(Named<TextBlock>(editor, "PreviewDataCaption"), (FrameworkElement)editor.Content, "The small preview keeps its demonstration-data caption visible");
            Save(editor, "settings-small-preview.png");
            Check(editor.SelectSection("components") && Named<Grid>(editor, "SettingsContentPanel").Visibility == Visibility.Visible, "Sidebar navigation returns from small preview to settings");
            Save(editor, "settings-small.png");
            Check(editor.CommitDraft() && editor.DraftApplication?.Placement == placement, "Saving the local draft preserves fractional placement without moving a real HUD");
        }
        finally { editor.Close(); }

        await VerifyExtensions();
        await VerifyScrollRouting(context, widgets);
        Check(SamePreferences(preferences, RealPreferences()), "Every actual user preference file stays byte-identical");
    }

    private static async Task VerifyScrollRouting(SettingsEditorContext context, ComponentSettings widgets)
    {
        var appearance = AppearanceSettings.Default with { Alerts = AlertSettings.Default with { Enabled = true } };
        var editor = new HudEditorWindow(appearance, widgets, "Тестовая игра", MainWindow.RenderHudPreview, context);
        try
        {
            Prepare(editor); editor.Width = 800; editor.Height = 420; editor.Show(); await Settle();
            string before = DraftSnapshot(editor);
            var cases = new[] { ("appearance", "SpacingSlider"), ("layout", "ResetLayoutButton"),
                ("components", "BatteryCheckBox"), ("alerts", "StutterCountSlider"),
                ("placement", "PlacementLockCheckBox"), ("profiles", "CopyProfileButton"), ("application", "SettingsVersionText") };
            foreach (var (section, bottomName) in cases)
            {
                editor.SelectSection(section); await Settle();
                var scroll = PageScroll(editor);
                Check(scroll.ScrollableHeight > 0, "Short-window page has a scrollable viewport: " + section);
                scroll.ScrollToTop(); await Settle();
                Wheel((UIElement)scroll.Content, -120); await Settle();
                Check(scroll.VerticalOffset > 0, "A routed wheel event moves the page: " + section);
                await WheelToEnd(scroll);
                var bottom = (FrameworkElement)(editor.FindSettingsControl(bottomName) ?? throw new InvalidOperationException("Missing bottom control " + bottomName));
                InViewport(bottom, scroll, "Wheel scrolling reaches the last control: " + section);
                if (section is "placement" or "profiles" or "application") Save(editor, "settings-" + section + "-short-bottom.png");
            }
            Check(DraftSnapshot(editor) == before, "Scrolling all seven sections does not alter any preference draft");

            editor.SelectSection("appearance"); await Settle();
            var appearanceScroll = PageScroll(editor);
            var scale = Named<Slider>(editor, "ScaleSlider"); scale.BringIntoView(); await Settle();
            double scaleBefore = scale.Value, offsetBefore = appearanceScroll.VerticalOffset;
            Wheel(scale, -120); await Settle();
            Check(scale.Value == scaleBefore && appearanceScroll.VerticalOffset > offsetBefore,
                "Wheel over a HUD scale slider scrolls the page without changing the scale");

            editor.SelectSection("placement"); await Settle();
            var positionScroll = PageScroll(editor); positionScroll.ScrollToTop(); await Settle();
            var monitor = (ComboBox)editor.FindSettingsControl("PlacementMonitorCombo")!;
            object? monitorBefore = monitor.SelectedItem;
            Wheel(monitor, -120); await Settle();
            Check(ReferenceEquals(monitor.SelectedItem, monitorBefore) && positionScroll.VerticalOffset > 0,
                "Wheel over a closed monitor picker scrolls the page without switching displays");

            editor.SelectSection("profiles"); await Settle();
            var profilesScroll = PageScroll(editor); profilesScroll.ScrollToTop(); await Settle();
            var profileList = (ListBox)editor.FindSettingsControl("SettingsProfileList")!;
            var inner = VisualScroll(profileList) ?? throw new InvalidOperationException("Profile list viewport is missing.");
            Check(inner.ScrollableHeight == 0, "The one-profile list has no inner scroll range");
            Wheel(WheelContent(inner), -120); await Settle();
            Check(profilesScroll.VerticalOffset > 0, "Wheel over a short profile list falls through to the outer page");

            FrameworkElement? last = null;
            editor.RegisterSection(new SettingsSectionDefinition("long-scroll-fixture", "Длинный раздел", "", "", () =>
            {
                var panel = new StackPanel();
                for (int index = 0; index < 40; index++) panel.Children.Add(last = new TextBlock { Text = "Строка " + index, Height = 28 });
                return panel;
            }));
            editor.SelectSection("long-scroll-fixture"); await Settle();
            var extensionScroll = PageScroll(editor);
            Check(extensionScroll.ScrollableHeight > 0, "A registered raw page receives the same scroll host automatically");
            await WheelToEnd(extensionScroll);
            InViewport(last!, extensionScroll, "Wheel scrolling reaches the bottom of a lazy extension page");
            Check(DraftSnapshot(editor) == before, "Wheel routing leaves slider, monitor, profile and startup settings unchanged");
        }
        finally { editor.Close(); }

        var manyProfiles = new GameProfileSettings(true, Enumerable.Range(0, 40).Select(index => new GameProfile(
            Guid.NewGuid().ToString("D"), "Игра " + index, @"D:\Games\Example\game" + index + ".exe", HudProfileSettings.Default)).ToArray());
        var longContext = context with { Profiles = manyProfiles, ActiveProfileId = null };
        var nestedEditor = new HudEditorWindow(appearance, widgets, "Общий HUD", MainWindow.RenderHudPreview, longContext);
        try
        {
            Prepare(nestedEditor); nestedEditor.Width = 800; nestedEditor.Height = 420; nestedEditor.Show();
            nestedEditor.SelectSection("profiles"); await Settle();
            string before = DraftSnapshot(nestedEditor);
            var outer = PageScroll(nestedEditor);
            var list = (ListBox)nestedEditor.FindSettingsControl("SettingsProfileList")!;
            var inner = VisualScroll(list) ?? throw new InvalidOperationException("Long profile list viewport is missing.");
            Check(inner.ScrollableHeight > 0, "A long profile list has its own scroll range");
            outer.ScrollToTop(); inner.ScrollToTop(); await Settle();
            double outerBefore = outer.VerticalOffset;
            Wheel(WheelContent(inner), -120); await Settle();
            Check(inner.VerticalOffset > 0 && outer.VerticalOffset == outerBefore,
                "An inner list consumes the wheel while it has room in that direction");
            inner.ScrollToEnd(); outer.ScrollToTop(); await Settle();
            Wheel(WheelContent(inner), -120); await Settle();
            Check(outer.VerticalOffset > 0 && Math.Abs(inner.VerticalOffset - inner.ScrollableHeight) < .1,
                "At the inner list bottom, downward wheel moves the outer page");
            inner.ScrollToTop(); outer.ScrollToEnd(); await Settle();
            outerBefore = outer.VerticalOffset;
            Wheel(WheelContent(inner), 120); await Settle();
            Check(inner.VerticalOffset == 0 && outer.VerticalOffset < outerBefore,
                "At the inner list top, upward wheel moves the outer page");
            Check(DraftSnapshot(nestedEditor) == before, "Nested wheel routing never changes the selected profile or its settings");
        }
        finally { nestedEditor.Close(); }
    }

    private static ScrollViewer PageScroll(HudEditorWindow editor)
        => ((TabItem)Named<TabControl>(editor, "EditorTabs").SelectedItem).Content as ScrollViewer
           ?? throw new InvalidOperationException("Selected page has no outer ScrollViewer: " + editor.SelectedSectionId);
    private static ScrollViewer? VisualScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (VisualScroll(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }
    private static UIElement WheelContent(ScrollViewer scroll)
        => VisualDescendant<System.Windows.Controls.ScrollContentPresenter>(scroll) ?? (UIElement)scroll.Content;
    private static T? VisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (VisualDescendant<T>(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }
    private static void Wheel(UIElement target, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.MouseWheelEvent });
    }
    private static async Task WheelToEnd(ScrollViewer scroll)
    {
        for (int batch = 0; batch < 30 && scroll.VerticalOffset < scroll.ScrollableHeight - .1; batch++)
        {
            for (int notch = 0; notch < 5; notch++) Wheel((UIElement)scroll.Content, -120);
            await Settle();
        }
        Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < .1, "Routed wheel reaches the page's actual bottom");
    }
    private static void InViewport(FrameworkElement element, ScrollViewer scroll, string message)
    {
        var bounds = element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
        Check(element.IsVisible && element.ActualHeight > 0 && bounds.Top >= -1 && bounds.Bottom <= scroll.ActualHeight + 1, message);
    }
    private static string DraftSnapshot(HudEditorWindow editor) => JsonSerializer.Serialize(new { editor.DraftAppearance, editor.DraftComponents, editor.DraftApplication });

    private static async Task VerifyExtensions()
    {
        var editor = new HudEditorWindow(AppearanceSettings.Default, ComponentSettings.Default, "Общий HUD", MainWindow.RenderHudPreview);
        int factories = 0, captures = 0, closed = 0;
        string captured = string.Empty;
        bool valid = false;
        TextBox? box = null;
        try
        {
            var extension = new SettingsSectionDefinition("example-extension", "Дополнительный раздел", "Параметры дополнения", "дополнение расширение", () =>
            {
                factories++;
                box = new TextBox { Name = "ExtensionTextBox", Text = "Исходное значение", Margin = new Thickness(18), VerticalAlignment = VerticalAlignment.Top };
                box.TextChanged += (_, _) => editor.NotifyDraftChanged();
                return box;
            }, () => { captures++; captured = box?.Text ?? "unopened"; }, () => valid ? null : "Проверь тестовое значение.", () => closed++);
            editor.RegisterSection(extension);
            Check(editor.Sections.Count == 8 && factories == 0 && !extension.IsContentCreated && !editor.HasUnsavedChanges,
                "Registering a new section adds navigation without creating controls or changing the draft");
            Check(editor.SearchSections("дополнение").Single().Id == extension.Id && factories == 1 && extension.IsContentCreated,
                "Search activates an extension factory only on first navigation");
            box!.Text = "Новое значение";
            Check(editor.HasUnsavedChanges && !editor.HasPendingPreview, "Extension edits can mark the draft without scheduling a HUD render");
            editor.SelectSection("appearance"); editor.SelectSection(extension.Id);
            Check(factories == 1 && box.Text == "Новое значение" && ReferenceEquals(editor.FindSettingsControl("ExtensionTextBox"), box),
                "Cached extension content and its values survive section changes");
            Check(!editor.CommitDraft() && captures == 1 && captured == "Новое значение" && editor.DialogResult is null,
                "A registered validation failure blocks acceptance and captures pending extension values");
            Check(Named<TextBlock>(editor, "StatusText").Text == "Проверь тестовое значение." && editor.SelectedSectionId == extension.Id,
                "Validation directs the user to its page with an inline explanation");
            valid = true;
            Check(editor.CommitDraft() && captures == 2, "Corrected extension values can be accepted without a separate persistence path");
            Expect<ArgumentException>(() => editor.RegisterSection(extension), "Duplicate page IDs are rejected");
            Expect<ArgumentException>(() => new SettingsSectionDefinition("Bad Id", "Example", "", "", () => new TextBlock()), "Unsafe or ambiguous page IDs are rejected");
            Expect<ArgumentException>(() => new SettingsSectionDefinition("empty-title", " ", "", "", () => new TextBlock()), "Every section requires a visible title");
            Expect<ArgumentNullException>(() => new SettingsSectionDefinition("empty-factory", "Example", "", "", null!), "Every extension requires a content factory");
            int attempts = 0;
            bool recover = false;
            var failing = new SettingsSectionDefinition("retry-example", "Восстановление", "", "", () =>
            { attempts++; if (!recover) throw new InvalidOperationException("Fixture failure"); return new TextBlock { Text = "Готово" }; });
            editor.RegisterSection(failing);
            string? previous = editor.SelectedSectionId;
            Check(!editor.SelectSection(failing.Id) && !failing.IsContentCreated && editor.SelectedSectionId == previous,
                "A failed page factory preserves the current page and can be retried");
            recover = true;
            Check(editor.SelectSection(failing.Id) && failing.IsContentCreated && attempts == 2, "Recovering a page factory creates and caches its content");
            Check(editor.SelectSection(failing.Id) && attempts == 2, "Revisiting a recovered page does not create another view");
            var unused = new SettingsSectionDefinition("never-opened", "Не открывался", "", "", () => throw new InvalidOperationException("Must stay lazy"), onClosed: () => closed += 100);
            editor.RegisterSection(unused);
            Check(!unused.IsContentCreated, "Unvisited pages stay lazy until the settings window closes");
            await Settle();
        }
        finally { editor.Close(); }
        Check(closed == 1, "Only constructed extension pages release their optional resources once on close");
    }

    private static void Prepare(Window window) { window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false; window.Topmost = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -16000; window.Top = -16000; }
    private static T Named<T>(Window window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    private static void Inside(FrameworkElement element, FrameworkElement root, string message)
    {
        var rect = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Check(element.IsVisible && rect.Left >= 0 && rect.Top >= 0 && rect.Right <= root.ActualWidth + 1 && rect.Bottom <= root.ActualHeight + 1, message);
    }
    private static void Save(HudEditorWindow editor, string name)
    {
        var root = (FrameworkElement)editor.Content; root.UpdateLayout();
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            drawing.DrawRectangle(editor.Background, null, rect);
            drawing.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect }, null, rect);
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(root.ActualWidth)), Math.Max(1, (int)Math.Ceiling(root.ActualHeight)), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(Output, name)); encoder.Save(stream);
    }
    private static string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    private static IEnumerable<TextBlock> VisualText(DependencyObject root)
    {
        if (root is TextBlock text) yield return text;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisualText(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static Dictionary<string, string> RealPreferences() => new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }.ToDictionary(name => name, name => Hash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland", name)));
    private static bool SamePreferences(Dictionary<string, string> a, Dictionary<string, string> b) => a.All(pair => b.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(25); }
    private static void Expect<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { Check(true, message); return; } throw new InvalidOperationException(message); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Checks.Add(message); }
}
