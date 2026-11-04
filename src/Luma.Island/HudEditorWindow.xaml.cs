using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island.Services;

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

    public AppearanceSettings DraftAppearance { get; private set; }
    public ComponentSettings DraftComponents { get; private set; }
    public bool PreviewAvailable { get; private set; }
    public bool HasPendingPreview => _previewTimer.IsEnabled;

    public HudEditorWindow(AppearanceSettings appearance, ComponentSettings components, string targetName,
        Func<AppearanceSettings, ComponentSettings, BitmapSource> preview)
    {
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        DraftAppearance = AppearanceStore.Normalize(appearance);
        DraftComponents = components ?? ComponentSettings.Default;
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(100) };
        _previewTimer.Tick += OnPreviewTick;
        InitializeComponent();
        TargetNameText.Text = string.IsNullOrWhiteSpace(targetName) ? "Общий HUD" : targetName;
        TargetNameText.ToolTip = "Изменения сохранятся здесь: " + TargetNameText.Text;
        BlockOrderList.ItemsSource = _blocks;
        MetricOrderList.ItemsSource = _metrics;
        LoadControls();
        _ready = true;
        RefreshMoveButtons();
        RenderPreview();
        Closed += OnEditorClosed;
    }

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
            Spacing = SpacingSlider.Value
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
            UpdatePreviewSize();
            PreviewAvailable = true;
            StatusText.Text = "Изменения применятся после сохранения.";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(173, 191, 199));
            StatusText.ToolTip = null;
            return true;
        }
        catch (Exception exception)
        {
            PreviewAvailable = false;
            HudPreviewImage.Source = null;
            AlertPreviewCaption.Visibility = Visibility.Collapsed;
            PreviewSizeText.Text = string.Empty;
            StatusText.Text = "Не удалось обновить предпросмотр. Изменения ещё не сохранены.";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(242, 194, 150));
            StatusText.ToolTip = exception.Message;
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
        double zoom = Math.Min(areaWidth / (baseWidth * AppearanceStore.MaxScale),
            areaHeight / (baseHeight * AppearanceStore.MaxScale));
        // A fixed scale reference keeps the size change visible without feeding Image size back into its host.
        HudPreviewImage.Width = Math.Max(1, source.PixelWidth * zoom);
        HudPreviewImage.Height = Math.Max(1, source.PixelHeight * zoom);
        PreviewSizeText.Text = $"Размер HUD: {source.PixelWidth} × {source.PixelHeight} при 100% Windows";
    }

    /// <summary>Flushes pending edits and validates preview without writing or closing the window.</summary>
    public bool CommitDraft()
    {
        VerifyAccess();
        if (_closed) return false;
        UpdateDraft();
        _previewTimer.Stop();
        return RenderPreview();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (CommitDraft()) DialogResult = true;
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
        LoadLayout(HudLayoutSettings.Default);
        _ready = true;
        RefreshMoveButtons();
        UpdateDraft();
        SchedulePreview();
    }

    private void OnEditorClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTick;
    }

    private sealed record OrderEntry(string Id, string Label);
}
