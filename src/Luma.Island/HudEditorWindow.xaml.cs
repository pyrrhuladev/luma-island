using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island.Services;
using Luma.Island.Settings;

namespace Luma.Island;

/// <summary>A local editor draft. Only its caller can persist accepted settings.</summary>
public partial class HudEditorWindow : Window
{
    private readonly Func<AppearanceSettings, ComponentSettings, BitmapSource> _preview;
    private readonly DispatcherTimer _previewTimer;
    private readonly ObservableCollection<OrderEntry> _blocks = new();
    private readonly ObservableCollection<OrderEntry> _metrics = new();
    private AppearanceSettings? _renderedAppearance;
    private ComponentSettings? _renderedComponents;
    private bool _ready, _closed;
    private readonly SettingsSectionRegistry _sections = new();
    private readonly ObservableCollection<SettingsSectionDefinition> _filteredSections = new();
    private readonly Dictionary<string, TabItem> _sectionTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ScrollViewer> _sectionScrollHosts = new();
    private readonly SettingsApplicationPages? _applicationPages;
    private readonly SettingsEditorContext? _context;
    private readonly AppearanceSettings _initialAppearance;
    private readonly ComponentSettings _initialComponents;
    private readonly SettingsApplicationDraft? _initialApplication;
    private readonly string _initialTargetName;
    private bool _navigationReady, _selectingNavigation, _showWidePreview = true, _focusedPreview, _extensionDirty;

    public AppearanceSettings DraftAppearance { get; private set; }
    public ComponentSettings DraftComponents { get; private set; }
    public bool PreviewAvailable { get; private set; }
    public bool HasPendingPreview => _previewTimer.IsEnabled;
    public ReadOnlyObservableCollection<SettingsSectionDefinition> Sections => _sections.Sections;
    public IReadOnlyList<SettingsSectionDefinition> FilteredSections => _filteredSections;
    public string? SelectedSectionId { get; private set; }
    public SettingsApplicationDraft? DraftApplication => _applicationPages?.Capture();
    public bool HasUnsavedChanges => _extensionDirty || DraftAppearance != _initialAppearance || DraftComponents != _initialComponents
        || !SameApplicationDraft(_initialApplication, DraftApplication);

    public HudEditorWindow(AppearanceSettings appearance, ComponentSettings components, string targetName,
        Func<AppearanceSettings, ComponentSettings, BitmapSource> preview, SettingsEditorContext? context = null)
    {
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        DraftAppearance = AppearanceStore.Normalize(appearance);
        DraftComponents = components ?? ComponentSettings.Default;
        _initialAppearance = DraftAppearance;
        _initialComponents = DraftComponents;
        _context = context;
        _initialTargetName = string.IsNullOrWhiteSpace(targetName) ? "Общий HUD" : targetName;
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(100) };
        _previewTimer.Tick += OnPreviewTick;
        InitializeComponent();
        if (context is not null)
        {
            _applicationPages = new SettingsApplicationPages(context, CurrentHudDraft, OnApplicationDraftChanged, () => this);
            _initialApplication = _applicationPages.Capture();
        }
        TargetNameText.Text = _initialTargetName;
        TargetNameText.ToolTip = "Изменения сохранятся здесь: " + TargetNameText.Text;
        BlockOrderList.ItemsSource = _blocks;
        MetricOrderList.ItemsSource = _metrics;
        LoadControls();
        _ready = true;
        InitializeSections();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => SettingsSearchBox.Focus()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, OnSave));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, Key.S, ModifierKeys.Control));
        RefreshMoveButtons();
        RenderPreview();
        UpdateResponsiveLayout();
        Closed += OnEditorClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemParameters.HighContrast) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        // COLORREF uses BGR. Keep the native caption and its window controls.
        SetCaptionAttribute(hwnd, 20, 1);
        SetCaptionAttribute(hwnd, 34, 0x44392D);
        SetCaptionAttribute(hwnd, 35, 0x211A14);
        SetCaptionAttribute(hwnd, 36, 0xF7F1EC);
    }

    private static void SetCaptionAttribute(IntPtr hwnd, int attribute, int value)
        => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void LoadControls()
    {
        AppearanceSettings appearance = DraftAppearance;
        HudLayoutSettings layout = appearance.Layout;
        AlertSettings alerts = appearance.Alerts;
        CompactCheckBox.IsChecked = appearance.Compact;
        GlassRadio.IsChecked = appearance.Mode == IslandAppearance.Glass;
        MatteRadio.IsChecked = appearance.Mode == IslandAppearance.Matte;
        TransparencySlider.Value = (1 - appearance.GlassOpacity) * 100;
        ScaleSlider.Value = appearance.Scale * 100;
        LoadLayout(layout);
        MusicCheckBox.IsChecked = DraftComponents.Music;
        VolumeCheckBox.IsChecked = DraftComponents.Volume;
        TimeCheckBox.IsChecked = DraftComponents.Time;
        DateCheckBox.IsChecked = DraftComponents.Date;
        BatteryCheckBox.IsChecked = DraftComponents.Battery;
        CpuCheckBox.IsChecked = DraftComponents.Cpu;
        GpuCheckBox.IsChecked = DraftComponents.Gpu;
        MemoryCheckBox.IsChecked = DraftComponents.Memory;
        FpsCheckBox.IsChecked = DraftComponents.Fps;
        ChartsCheckBox.IsChecked = DraftComponents.Charts;
        GpuTemperatureCheckBox.IsChecked = DraftComponents.GpuTemperature;
        CpuTemperatureCheckBox.IsChecked = DraftComponents.CpuTemperature;
        MemoryTemperatureCheckBox.IsChecked = DraftComponents.MemoryTemperature;
        LowCheckBox.IsChecked = DraftComponents.OnePercentLow;
        FrameTimeCheckBox.IsChecked = DraftComponents.FrameTime;
        AlertsEnabledCheckBox.IsChecked = alerts.Enabled;
        AlertCpuCheckBox.IsChecked = alerts.CpuTemperature;
        AlertGpuCheckBox.IsChecked = alerts.GpuTemperature;
        AlertMemoryCheckBox.IsChecked = alerts.MemoryTemperature;
        AlertStuttersCheckBox.IsChecked = alerts.Stutters;
        CpuThresholdSlider.Value = alerts.CpuThreshold;
        GpuThresholdSlider.Value = alerts.GpuThreshold;
        MemoryThresholdSlider.Value = alerts.MemoryThreshold;
        FrameThresholdSlider.Value = alerts.FrameTimeThreshold;
        StutterCountSlider.Value = alerts.StutterCount;
    }

    private void LoadLayout(HudLayoutSettings layout)
    {
        TextScaleSlider.Value = layout.TextScale * 100;
        SpacingSlider.Value = layout.Spacing;
        ColumnsOneRadio.IsChecked = layout.MetricColumns == 1;
        ColumnsTwoRadio.IsChecked = layout.MetricColumns == 2;
        ColumnsThreeRadio.IsChecked = layout.MetricColumns == 3;
        DetachedMusicCheckBox.IsChecked = layout.DetachedMusic;
        _blocks.Clear();
        foreach (string id in layout.BlockOrder.Split(','))
            _blocks.Add(new OrderEntry(id, id switch
            {
                "header" => "FPS, время и батарея",
                "metrics" => "CPU, GPU и RAM",
                _ => "Музыка"
            }));
        _metrics.Clear();
        foreach (string id in layout.MetricOrder.Split(','))
            _metrics.Add(new OrderEntry(id, id switch { "cpu" => "CPU", "gpu" => "GPU", _ => "RAM" }));
        BlockOrderList.SelectedIndex = 0;
        MetricOrderList.SelectedIndex = 0;
    }

    private void OnDraftChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        UpdateDraft();
        UpdateDirtyStatus();
        SchedulePreview();
    }

    private void UpdateDraft()
    {
        bool Checked(CheckBox checkBox) => checkBox.IsChecked == true;
        HudLayoutSettings layout = HudLayoutSettings.Normalize(DraftAppearance.Layout with
        {
            BlockOrder = string.Join(',', _blocks.Select(entry => entry.Id)),
            MetricOrder = string.Join(',', _metrics.Select(entry => entry.Id)),
            MetricColumns = ColumnsOneRadio.IsChecked == true ? 1 : ColumnsTwoRadio.IsChecked == true ? 2 : 3,
            TextScale = KeepEquivalent(DraftAppearance.Layout.TextScale, TextScaleSlider.Value / 100),
            Spacing = SpacingSlider.Value,
            DetachedMusic = Checked(DetachedMusicCheckBox)
        });
        AlertSettings alerts = AlertSettings.Normalize(DraftAppearance.Alerts with
        {
            Enabled = Checked(AlertsEnabledCheckBox),
            CpuTemperature = Checked(AlertCpuCheckBox), GpuTemperature = Checked(AlertGpuCheckBox),
            MemoryTemperature = Checked(AlertMemoryCheckBox), Stutters = Checked(AlertStuttersCheckBox),
            CpuThreshold = CpuThresholdSlider.Value, GpuThreshold = GpuThresholdSlider.Value,
            MemoryThreshold = MemoryThresholdSlider.Value, FrameTimeThreshold = FrameThresholdSlider.Value,
            StutterCount = (int)Math.Round(StutterCountSlider.Value)
        });
        DraftAppearance = AppearanceStore.Normalize(DraftAppearance with
        {
            Mode = GlassRadio.IsChecked == true ? IslandAppearance.Glass : IslandAppearance.Matte,
            GlassOpacity = KeepEquivalent(DraftAppearance.GlassOpacity, 1 - TransparencySlider.Value / 100),
            Scale = KeepEquivalent(DraftAppearance.Scale, ScaleSlider.Value / 100),
            Compact = Checked(CompactCheckBox), Layout = layout, Alerts = alerts
        });
        DraftComponents = DraftComponents with
        {
            Music = Checked(MusicCheckBox), Volume = Checked(VolumeCheckBox),
            Time = Checked(TimeCheckBox), Date = Checked(DateCheckBox), Battery = Checked(BatteryCheckBox),
            Cpu = Checked(CpuCheckBox), Gpu = Checked(GpuCheckBox), Memory = Checked(MemoryCheckBox),
            Fps = Checked(FpsCheckBox), Charts = Checked(ChartsCheckBox),
            GpuTemperature = Checked(GpuTemperatureCheckBox), CpuTemperature = Checked(CpuTemperatureCheckBox),
            MemoryTemperature = Checked(MemoryTemperatureCheckBox),
            OnePercentLow = Checked(LowCheckBox), FrameTime = Checked(FrameTimeCheckBox)
        };
    }

    private static double KeepEquivalent(double previous, double value) => Math.Abs(previous - value) < 1e-10 ? previous : value;

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        if (!_closed) RenderPreview();
    }

    private bool RenderPreview()
    {
        if (PreviewAvailable && _renderedAppearance == DraftAppearance && _renderedComponents == DraftComponents) return true;
        try
        {
            BitmapSource source = _preview(DraftAppearance, DraftComponents)
                ?? throw new InvalidOperationException("Preview callback returned no image.");
            HudPreviewImage.Source = source;
            AlertSettings alerts = DraftAppearance.Alerts;
            AlertPreviewCaption.Visibility = alerts.Enabled && (alerts.CpuTemperature || alerts.GpuTemperature || alerts.MemoryTemperature || alerts.Stutters)
                ? Visibility.Visible : Visibility.Collapsed;
            _renderedAppearance = DraftAppearance;
            _renderedComponents = DraftComponents;
            UpdateResponsiveLayout();
            PreviewAvailable = true;
            UpdateDirtyStatus();
            return true;
        }
        catch (Exception exception)
        {
            PreviewAvailable = false;
            HudPreviewImage.Source = null;
            AlertPreviewCaption.Visibility = Visibility.Collapsed;
            PreviewSizeText.Text = string.Empty;
            ShowStatusError("Не удалось обновить предпросмотр. Изменения ещё не сохранены.", exception.Message);
            return false;
        }
    }

    private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_closed) UpdatePreviewSize();
    }

    private void UpdatePreviewSize()
    {
        if (HudPreviewImage?.Source is not BitmapSource source || PreviewSurface is null) return;
        double scale = _renderedAppearance?.Scale ?? DraftAppearance.Scale;
        double baseWidth = source.PixelWidth / scale, baseHeight = source.PixelHeight / scale;
        double areaWidth = PreviewSurface.ActualWidth > 16 ? PreviewSurface.ActualWidth - 16 : 300;
        double areaHeight = PreviewSurface.ActualHeight > 16 ? PreviewSurface.ActualHeight - 16 : 264;
        double zoom = _focusedPreview
            ? Math.Min(1, Math.Min(areaWidth / source.PixelWidth, areaHeight / source.PixelHeight))
            : Math.Min(areaWidth / (baseWidth * AppearanceStore.MaxScale), areaHeight / (baseHeight * AppearanceStore.MaxScale));
        // A fixed scale reference keeps the size change visible without feeding Image size back into its host.
        HudPreviewImage.Width = Math.Max(1, source.PixelWidth * zoom);
        HudPreviewImage.Height = Math.Max(1, source.PixelHeight * zoom);
        PreviewSizeText.Text = $"{source.PixelWidth} × {source.PixelHeight} · при 100% Windows";
    }

    /// <summary>Flushes pending edits and validates preview without writing or closing the window.</summary>
    public bool CommitDraft()
    {
        VerifyAccess();
        if (_closed) return false;
        UpdateDraft();
        _previewTimer.Stop();
        try
        {
            foreach (var section in _sections.Sections) section.CaptureDraft?.Invoke();
            string? applicationError = _applicationPages?.Validate();
            if (applicationError is not null) { SelectSection("profiles"); ShowStatusError(applicationError); return false; }
            foreach (var section in _sections.Sections)
            {
                string? error = section.Validate?.Invoke();
                if (error is null) continue;
                SelectSection(section.Id);
                ShowStatusError(error);
                return false;
            }
            return RenderPreview();
        }
        catch (Exception error) { ShowStatusError("Не удалось проверить изменения. Они ещё не сохранены.", error.Message); return false; }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (CommitDraft()) DialogResult = true;
    }

    private void OnTransparencyPreset(object sender, RoutedEventArgs e)
    {
        if (GlassRadio.IsChecked == true && sender is Button { Tag: string value } && int.TryParse(value, out int percent))
            TransparencySlider.Value = percent;
    }

    private void OnOrderSelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshMoveButtons();

    private void RefreshMoveButtons()
    {
        if (BlockUpButton is null || MetricUpButton is null) return;
        BlockUpButton.IsEnabled = BlockOrderList.SelectedIndex > 0;
        BlockDownButton.IsEnabled = BlockOrderList.SelectedIndex >= 0 && BlockOrderList.SelectedIndex < _blocks.Count - 1;
        MetricUpButton.IsEnabled = MetricOrderList.SelectedIndex > 0;
        MetricDownButton.IsEnabled = MetricOrderList.SelectedIndex >= 0 && MetricOrderList.SelectedIndex < _metrics.Count - 1;
    }

    private void Move(ListBox list, ObservableCollection<OrderEntry> entries, int direction)
    {
        int index = list.SelectedIndex, next = index + direction;
        if (index < 0 || next < 0 || next >= entries.Count) return;
        entries.Move(index, next);
        list.SelectedIndex = next;
        RefreshMoveButtons();
        if (!_ready || _closed) return;
        UpdateDraft();
        SchedulePreview();
    }

    private void OnBlockUp(object sender, RoutedEventArgs e) => Move(BlockOrderList, _blocks, -1);
    private void OnBlockDown(object sender, RoutedEventArgs e) => Move(BlockOrderList, _blocks, 1);
    private void OnMetricUp(object sender, RoutedEventArgs e) => Move(MetricOrderList, _metrics, -1);
    private void OnMetricDown(object sender, RoutedEventArgs e) => Move(MetricOrderList, _metrics, 1);

    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        _ready = false;
        DraftAppearance = DraftAppearance with { Layout = HudLayoutSettings.Default };
        LoadLayout(HudLayoutSettings.Default);
        _ready = true;
        RefreshMoveButtons();
        UpdateDraft();
        UpdateDirtyStatus();
        SchedulePreview();
    }

    private void OnResetMusicPosition(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        UpdateDraft();
        DraftAppearance = DraftAppearance with { Layout = DraftAppearance.Layout with { MusicPosition = null } };
        UpdateDirtyStatus();
        SchedulePreview();
    }

    private void OnEditorClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTick;
        foreach (var section in _sections.Sections)
            try { section.ReleaseContent(); } catch (Exception error) { App.Log(error); }
    }

    private HudProfileSettings CurrentHudDraft() => new(DraftAppearance, DraftComponents,
        _applicationPages?.Capture().Placement ?? _context?.Placement ?? PlacementSettings.Default);

    private void OnApplicationDraftChanged()
    {
        if (!_ready || _closed) return;
        if (_context?.ActiveProfileId is { } activeId && DraftApplication is { } draft)
        {
            var active = draft.Profiles.Profiles.FirstOrDefault(profile => profile.Id == activeId);
            TargetNameText.Text = active is null ? "Активный профиль удалён" : !draft.Profiles.Enabled ? "Общий HUD после сохранения" : active.Name;
            TargetNameText.ToolTip = active is null
                ? "После сохранения вернутся общие настройки. Изменения вида удалённого профиля не сохранятся."
                : "Изменения HUD сохранятся здесь: " + TargetNameText.Text;
        }
        UpdateDirtyStatus();
    }

    private void InitializeSections()
    {
        RegisterBuiltIn("appearance", "Внешний вид", "Фон, масштаб, текст и расстояния между блоками.",
            "стекло матовое прозрачность размер шрифт компактная капсула", AppearanceTab);
        RegisterBuiltIn("layout", "Раскладка", "Отдельные островки, порядок блоков и число показателей в строке.",
            "порядок музыка заголовок cpu gpu ram строки колонки расположение островок островки раздельные разделить отдельно перетаскивать", LayoutTab);
        RegisterBuiltIn("components", "Показатели", "Выберите, какие данные отображать в HUD.",
            "музыка громкость время дата часы батарея fps фпс low кадр фреймтайм cpu gpu ram процессор видеокарта оперативка озу нагрузка температура память графики", ComponentsTab);
        RegisterBuiltIn("alerts", "Предупреждения", "Тихий индикатор температуры и серий просадок.",
            "порог температура перегрев просадки задержки кадры фреймтайм фриз статтер уведомления", AlertsTab);
        RegisterBuiltIn("placement", "Положение", "Монитор, точка привязки и защита от случайного перемещения.",
            "монитор экран позиция угол центр закрепить перетаскивание", PlacementTab,
            () => _applicationPages?.CreatePositionPage() ?? UnavailableApplicationPage());
        RegisterBuiltIn("profiles", "Профили игр", "Свой HUD для каждой игры и автоматическое переключение.",
            "игра exe процесс профиль автопереключение добавить удалить имя переименовать путь", ProfilesTab,
            () => _applicationPages?.CreateProfilesPage() ?? UnavailableApplicationPage());
        RegisterBuiltIn("application", "Приложение", "Автозапуск Windows, датчики и управление HUD.",
            "автозагрузка запуск windows датчики установка cpu ram горячие клавиши трей версия", ApplicationTab,
            () => _applicationPages?.CreateApplicationPage() ?? UnavailableApplicationPage());
        SettingsNavigation.ItemsSource = _filteredSections;
        _navigationReady = true;
        ApplySearch();
    }

    private static FrameworkElement UnavailableApplicationPage() => new Border
    {
        Padding = new Thickness(18), CornerRadius = new CornerRadius(10),
        Background = new SolidColorBrush(Color.FromRgb(25, 36, 43)),
        Child = new TextBlock { Text = "Параметры приложения доступны при открытии настроек из Luma.", TextWrapping = TextWrapping.Wrap }
    };

    private void RegisterBuiltIn(string id, string title, string description, string keywords, TabItem tab,
        Func<FrameworkElement>? factory = null)
    {
        _sections.Register(new SettingsSectionDefinition(id, title, description, keywords,
            factory ?? (() => (FrameworkElement)tab.Content)));
        _sectionTabs.Add(id, tab);
    }

    /// <summary>Adds an internal settings page without changing navigation or preview infrastructure.</summary>
    public void RegisterSection(SettingsSectionDefinition section)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_closed, this);
        _sections.Register(section);
        var tab = new TabItem { Header = section.Title };
        _sectionTabs.Add(section.Id, tab);
        EditorTabs.Items.Add(tab);
        ApplySearch();
    }

    public void NotifyDraftChanged(bool updatePreview = false)
    {
        VerifyAccess();
        if (_closed) return;
        _extensionDirty = true;
        UpdateDirtyStatus();
        if (updatePreview) SchedulePreview();
    }

    public bool SelectSection(string id)
    {
        VerifyAccess();
        if (_closed || _sections.Find(id) is not { } section) return false;
        bool wasSelecting = _selectingNavigation;
        try
        {
            var content = section.GetContent();
            TabItem tab = _sectionTabs[section.Id];
            if (tab.Content is null)
                tab.Content = content is ScrollViewer ? content : new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    CanContentScroll = false,
                    PanningMode = PanningMode.VerticalOnly,
                    Padding = new Thickness(0, 0, 10, 0)
                };
            if (tab.Content is ScrollViewer scroll && _sectionScrollHosts.Add(scroll))
                scroll.PreviewMouseWheel += OnSectionMouseWheel;
            _selectingNavigation = true;
            if (!_filteredSections.Contains(section)) { SettingsSearchBox.Text = string.Empty; ApplySearch(); }
            EditorTabs.SelectedItem = tab;
            SettingsNavigation.SelectedItem = section;
            SettingsNavigation.ScrollIntoView(section);
            SelectedSectionId = section.Id;
            ActiveSectionTitle.Text = section.Title;
            ActiveSectionDescription.Text = section.Description;
            _focusedPreview = false;
            UpdateResponsiveLayout();
            return true;
        }
        catch (Exception error)
        {
            _selectingNavigation = true;
            SettingsNavigation.SelectedItem = _sections.Find(SelectedSectionId ?? string.Empty);
            ShowStatusError("Не удалось открыть раздел настроек.", error.Message);
            return false;
        }
        finally { _selectingNavigation = wasSelecting; }
    }

    private void OnSectionMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer outer || outer.ScrollableHeight <= 0 || e.Delta == 0) return;
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null && node != outer; node = ScrollParent(node))
        {
            if (node is ComboBox { IsDropDownOpen: true }) return;
            if (node is ScrollViewer inner && inner != outer && inner.ScrollableHeight > 0 &&
                (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight)) return;
        }
        int lines = SystemParameters.WheelScrollLines;
        if (lines < 0)
        {
            if (e.Delta < 0) outer.PageDown(); else outer.PageUp();
        }
        else if (lines > 0) outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta / 120d * lines * 16);
        e.Handled = true;
    }

    private static DependencyObject? ScrollParent(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    public IReadOnlyList<SettingsSectionDefinition> SearchSections(string? query)
    {
        VerifyAccess();
        string text = query ?? string.Empty;
        if (SettingsSearchBox.Text == text) ApplySearch(); else SettingsSearchBox.Text = text;
        return _filteredSections.ToArray();
    }

    public object? FindSettingsControl(string name)
    {
        if (FindName(name) is { } existing) return existing;
        if (_applicationPages?.FindControl(name) is { } applicationControl) return applicationControl;
        foreach (var section in _sections.Sections)
            if (section.IsContentCreated && FindElement(section.GetContent(), name) is { } extensionControl) return extensionControl;
        return null;
    }

    private static FrameworkElement? FindElement(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node && FindElement(node, name) is { } found) return found;
        return null;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (_navigationReady && !_closed) ApplySearch(); }
    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SettingsSearchBox.Clear();
        SettingsSearchBox.Focus();
    }

    private void ApplySearch()
    {
        if (!_navigationReady || _closed) return;
        var matches = _sections.Search(SettingsSearchBox.Text);
        bool hasText = SettingsSearchBox.Text.Length > 0;
        SearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        SearchResultLabel.Text = string.IsNullOrWhiteSpace(SettingsSearchBox.Text) ? "РАЗДЕЛЫ" : $"НАЙДЕНО · {matches.Count}";
        var previous = _sections.Find(SelectedSectionId ?? string.Empty);
        bool wasSelecting = _selectingNavigation;
        _selectingNavigation = true;
        _filteredSections.Clear();
        foreach (var section in matches) _filteredSections.Add(section);
        NoSearchResults.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsNavigation.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _selectingNavigation = wasSelecting;
        if (matches.Count == 0) return;
        SelectSection(previous is not null && matches.Contains(previous) ? previous.Id : matches[0].Id);
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigationReady && !_selectingNavigation && SettingsNavigation.SelectedItem is SettingsSectionDefinition section)
            SelectSection(section.Id);
    }

    private void OnEditorTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_navigationReady || _selectingNavigation || !ReferenceEquals(e.Source, EditorTabs)) return;
        var entry = _sectionTabs.FirstOrDefault(pair => pair.Value == EditorTabs.SelectedItem);
        if (entry.Key is not null) SelectSection(entry.Key);
    }

    private void OnSettingsKeyDown(object sender, KeyEventArgs e)
    {
        if (!SettingsSearchBox.IsKeyboardFocusWithin && !ReferenceEquals(e.OriginalSource, SettingsSearchBox)) return;
        if (e.Key == Key.Escape && SettingsSearchBox.Text.Length > 0)
        { SettingsSearchBox.Clear(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (SettingsNavigation.SelectedItem is not SettingsSectionDefinition section) return;
            SelectSection(section.Id);
            SettingsNavigation.ScrollIntoView(section);
            if (SettingsNavigation.ItemContainerGenerator.ContainerFromItem(section) is ListBoxItem item) item.Focus();
            else SettingsNavigation.Focus();
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) { if (_ready) UpdateResponsiveLayout(); }
    private void OnSettingsBodySizeChanged(object sender, SizeChangedEventArgs e) { if (_ready) UpdateResponsiveLayout(); }
    private void OnTogglePreview(object sender, RoutedEventArgs e)
    {
        if (_focusedPreview) _focusedPreview = false;
        else if (!CanShowSidePreview()) _focusedPreview = true;
        else _showWidePreview = !_showWidePreview;
        UpdateResponsiveLayout();
    }
    private void OnExpandPreview(object sender, RoutedEventArgs e)
    {
        _focusedPreview = !_focusedPreview;
        UpdateResponsiveLayout();
    }
    private bool CanShowSidePreview() => (ActualWidth > 0 ? ActualWidth : Width) >= 1000
        && (SettingsBody.ActualHeight > 0 ? SettingsBody.ActualHeight : Height - 180) >= 310;

    private void UpdateResponsiveLayout()
    {
        if (PreviewPane is null || SettingsContentPanel is null) return;
        bool sidePreview = !_focusedPreview && CanShowSidePreview() && _showWidePreview;
        bool visible = _focusedPreview || sidePreview;
        PreviewColumn.Width = new GridLength(sidePreview ? 272 : 0);
        PreviewGapColumn.Width = new GridLength(sidePreview ? 16 : 0);
        Grid.SetColumn(PreviewPane, sidePreview ? 4 : 2);
        PreviewPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SettingsContentPanel.Visibility = _focusedPreview ? Visibility.Collapsed : Visibility.Visible;
        PreviewPane.VerticalAlignment = _focusedPreview ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        PreviewDetails.Visibility = _focusedPreview ? Visibility.Collapsed : Visibility.Visible;
        double availableHeight = SettingsBody.ActualHeight > 0 ? SettingsBody.ActualHeight : Math.Max(120, Height - 180);
        double detailsHeight = AlertPreviewCaption.Visibility == Visibility.Visible ? 128 : 106;
        PreviewSurface.Height = _focusedPreview ? double.NaN
            : Math.Clamp(availableHeight - detailsHeight - 26, 100, 225);
        TogglePreviewButton.Content = _focusedPreview ? "К настройкам" : visible ? "Скрыть предпросмотр" : "Предпросмотр";
        ExpandPreviewButton.Content = _focusedPreview ? "Свернуть" : "Крупнее";
        UpdatePreviewSize();
    }

    private static bool SameApplicationDraft(SettingsApplicationDraft? a, SettingsApplicationDraft? b)
    {
        if (a is null || b is null) return a == b;
        return a.Placement == b.Placement && a.AutostartRegistered == b.AutostartRegistered
            && a.Profiles.Enabled == b.Profiles.Enabled && a.Profiles.Profiles.SequenceEqual(b.Profiles.Profiles);
    }
    private void UpdateDirtyStatus()
    {
        if (StatusText is null) return;
        bool changed = HasUnsavedChanges;
        StatusText.Text = changed ? "Есть несохранённые изменения" : "Настройки сохранены";
        StatusText.Foreground = (Brush)Resources[changed ? "EditorMint" : "EditorMuted"];
        StatusDot.Fill = StatusText.Foreground;
        StatusText.ToolTip = null;
    }
    private void ShowStatusError(string message, string? details = null)
    {
        StatusText.Text = message;
        StatusText.Foreground = StatusDot.Fill = (Brush)Resources["EditorWarning"];
        StatusText.ToolTip = details;
    }

    private sealed record OrderEntry(string Id, string Label);
}
