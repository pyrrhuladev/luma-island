using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly Dictionary<TextBlock, double> _layoutBaseFonts = new();
    private bool _layoutFontsCaptured;
    private string? _appliedBlockOrder, _appliedMetricOrder;
    private string? _appliedHeaderOrder;
    private (int Fps, int Low, bool Clock, bool Battery, double TextScale)? _headerFitKey;
    private HudLayoutSettings LayoutSettings => Appearance.Layout ?? HudLayoutSettings.Default;
    private double LayoutTextScale => LayoutSettings.TextScale;
    private double LayoutGrowth => Math.Max(1, LayoutTextScale);
    private double LayoutSpacing => LayoutSettings.Spacing;
    private int VisibleHardwareCount => (PresentedComponents.Cpu || PresentedComponents.CpuTemperature ? 1 : 0)
        + (PresentedComponents.Gpu || PresentedComponents.GpuTemperature ? 1 : 0)
        + (PresentedComponents.Memory || PresentedComponents.MemoryTemperature ? 1 : 0);
    private int LayoutMetricColumns => Math.Max(1, Math.Min(VisibleHardwareCount, LayoutSettings.MetricColumns));
    private int LayoutMetricRows => VisibleHardwareCount == 0 ? 0 : (VisibleHardwareCount + LayoutMetricColumns - 1) / LayoutMetricColumns;
    private double CompactMetricCellHeight => (28 + TemperatureRowHeight) * LayoutGrowth;
    private double CompactMetricGridHeight => LayoutMetricRows == 0 ? 0
        : CompactMetricCellHeight * LayoutMetricRows + LayoutSpacing * (LayoutMetricRows - 1);
    private double LayoutFrameTimeHeight => PresentedComponents.FrameTime ? 72 + (LayoutGrowth - 1) * 32 : 0;

    private double LayoutBlockGap(string block)
    {
        bool previousVisible = false;
        foreach (string key in PresentationLayout.BlockOrder.Split(','))
        {
            bool visible = key switch { "header" => HudHeaderContentHeight > 0, "metrics" => HudMetricsContentHeight > 0, _ => HudMusicContentHeight > 0 };
            if (key == block) return previousVisible && visible ? LayoutSpacing - 6 : 0;
            previousVisible |= visible;
        }
        return 0;
    }

    public void SetHudLayout(HudLayoutSettings layout)
    {
        VerifyAccess();
        if (_closed) return;
        var normalized = HudLayoutSettings.Normalize(layout);
        if (Appearance.Layout == normalized) return;
        var previous = Components;
        Appearance = Appearance with { Layout = normalized };
        ApplyComponentVisibility();
        OnMediaChanged(_lastMedia ?? _media.Current);
        SaveAppearance();
        NotifyCapsuleComponentsChanged(previous);
    }

    private void ApplyHudLayout()
    {
        if (CompactContentGrid is null) return;
        var layout = _presentationLayout ?? HudLayoutSettings.Normalize(Appearance.Layout);
        if (!_layoutFontsCaptured && Content is not null) { CaptureLayoutFonts(Content); _layoutFontsCaptured = true; }
        foreach (var item in _layoutBaseFonts) item.Key.FontSize = item.Value * layout.TextScale;
        CompactCpu.LineHeight = CompactGpu.LineHeight = CompactMemory.LineHeight = 16 * layout.TextScale;
        if (ApplyContainerRows())
        {
            CompactLowPanel.Margin = new Thickness(0);
            CompactMusicRow.Margin = new Thickness(0);
            CompactCpu.Margin = CompactGpu.Margin = CompactMemory.Margin = new Thickness(0);
            ResizeChartTextRows(layout.TextScale);
            FitHeaderText(force: true);
            return;
        }
        CompactContentGrid.Width = CollapsedSurfaceWidth - 28;

        string rowKey = _presentationItemsKey ?? layout.BlockOrder;
        if (_appliedBlockOrder != rowKey)
        {
            string[] tail = ["charts", "frame-time", "volume", "date"];
            IEnumerable<string> groups = PresentationCapsuleItems is { } items
                ? items.Select(HudCapsuleLayout.Group).Concat(["header", "metrics", "music"]).Concat(tail).Distinct()
                : layout.BlockOrder.Split(',').Concat(tail);
            CompactContentGrid.RowDefinitions.Clear();
            foreach (string group in groups)
            {
                Grid.SetRow(PresentedWidgetGroup(group), CompactContentGrid.RowDefinitions.Count);
                CompactContentGrid.RowDefinitions.Add(group switch
                {
                    "header" => HudHeaderRow, "metrics" => HudMetricsRow, "music" => HudMusicRow,
                    "charts" => ChartsRow, "frame-time" => FrameTimeRow, "volume" => VolumeRow, _ => FooterRow
                });
            }
            _appliedBlockOrder = rowKey;
        }
        ApplyHeaderOrder();
        if (_appliedMetricOrder != layout.MetricOrder)
        {
            CompactPerformance.Children.Clear();
            foreach (string key in layout.MetricOrder.Split(',')) CompactPerformance.Children.Add(CompactMetric(key));
            _appliedMetricOrder = layout.MetricOrder;
        }
        CompactPerformance.Columns = LayoutMetricColumns;
        CompactPerformance.Rows = 0;
        CompactPerformance.Height = CompactMetricGridHeight;
        double bandMargin = layout.Spacing;
        HudHeader.Margin = new Thickness(0, LayoutBlockGap("header"), 0, 0);
        bool lowFirst = PresentationCapsuleItems is { } header && Array.IndexOf(header, "low") < Array.IndexOf(header, "fps");
        CompactLowPanel.Margin = !PresentedComponents.Fps ? new Thickness(0)
            : lowFirst ? new Thickness(0, 0, 12, 0) : new Thickness(12, 0, 0, 0);
        CompactMusicRow.Margin = new Thickness(0, LayoutBlockGap("music"), 0, 0);
        CompactPerformance.Margin = new Thickness(0, bandMargin + LayoutBlockGap("metrics"), 0, bandMargin);
        int visibleIndex = 0;
        foreach (TextBlock metric in CompactPerformance.Children)
        {
            if (metric.Visibility != Visibility.Visible) { metric.Margin = new Thickness(0); continue; }
            int row = visibleIndex++ / LayoutMetricColumns;
            metric.Margin = new Thickness(Math.Max(0, layout.Spacing - 6) / 2,
                row > 0 ? layout.Spacing / 2 : 0, Math.Max(0, layout.Spacing - 6) / 2,
                row < LayoutMetricRows - 1 ? layout.Spacing / 2 : 0);
        }
        ChartsRow.Height = new GridLength(ChartsHeight);
        FrameTimeRow.Height = new GridLength(LayoutFrameTimeHeight);
        VolumeRow.Height = new GridLength(HudVolumeHeight);
        FooterRow.Height = new GridLength(HudDateHeight);
        ResizeChartTextRows(layout.TextScale);
        FitHeaderText(force: true);
    }

    private TextBlock CompactMetric(string key) => key switch { "cpu" => CompactCpu, "gpu" => CompactGpu, _ => CompactMemory };

    private FrameworkElement? PresentedWidget(string id) => PresentsComponentInstances
        ? _instanceWidgets.GetValueOrDefault(id) : ClassicPresentedWidget(id);

    private FrameworkElement? ClassicPresentedWidget(string kind) => kind switch
    {
        "fps" => CompactFpsPanel, "low" => CompactLowPanel, "time" => CompactClock, "battery" => CompactBattery,
        "cpu" => CompactCpu, "gpu" => CompactGpu, "memory" => CompactMemory, "music" => CompactMusicRow,
        "cpu-chart" => CpuChartCard, "gpu-chart" => GpuChartCard, "memory-chart" => MemoryChartCard, "fps-chart" => FpsChartCard,
        "frame-time" => FrameTimeCard, "volume" => CompactVolume, "date" => DateLabel, _ => null
    };

    private FrameworkElement PresentedWidgetGroup(string group) => group switch
    {
        "header" => HudHeader, "metrics" => CompactPerformance, "charts" => ChartsPanel, _ => ClassicPresentedWidget(group)!
    };

    private void ApplyHeaderOrder()
    {
        string[]? items = PresentationCapsuleItems?.Where(HudCapsuleLayout.IsHeader).ToArray();
        string? order = items is null ? null : string.Join(',', items);
        if (_appliedHeaderOrder == order) return;
        _appliedHeaderOrder = order;

        CompactFrameStats.Children.Clear();
        HudHeader.Children.Clear();
        HudHeader.ColumnDefinitions.Clear();
        if (items is null)
        {
            HudHeader.ColumnDefinitions.Add(CompactFrameColumn);
            HudHeader.ColumnDefinitions.Add(CompactClockColumn);
            HudHeader.ColumnDefinitions.Add(CompactBatteryColumn);
            CompactFrameStats.Children.Add(CompactFpsPanel);
            CompactFrameStats.Children.Add(CompactLowPanel);
            HudHeader.Children.Add(CompactFrameStats);
            Grid.SetColumn(CompactClock, 1);
            Grid.SetColumn(CompactBattery, 2);
            HudHeader.Children.Add(CompactClock);
            HudHeader.Children.Add(CompactBattery);
            return;
        }

        // Keep the original left statistics/right clock alignment when their groups
        // retain that order. Custom interleaving uses the requested left-to-right order.
        int firstClock = Array.FindIndex(items, item => item is "time" or "battery");
        int lastFrame = Array.FindLastIndex(items, item => item is "fps" or "low");
        for (int index = 0; index < items.Length; index++)
        {
            if (index == firstClock && lastFrame < firstClock && _capsuleId is null)
                HudHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FrameworkElement widget = ClassicPresentedWidget(items[index])!;
            Grid.SetColumn(widget, HudHeader.ColumnDefinitions.Count);
            HudHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            HudHeader.Children.Add(widget);
        }
    }

    // Tabular FPS numerals only change width when their digit count changes. Avoid new
    // measurements on every frame update, while keeping large user fonts inside the capsule.
    private void FitHeaderText(bool force = false)
    {
        var key = (PresentedComponents.Fps ? CompactFps.Text.Length : 0,
            PresentedComponents.OnePercentLow ? CompactOnePercentLow.Text.Length : 0,
            PresentedComponents.Time, _batteryVisible, LayoutTextScale);
        if (!force && _headerFitKey == key) return;
        _headerFitKey = key;
        double factor = LayoutTextScale;
        CompactFps.FontSize = 34 * factor;
        CompactOnePercentLow.FontSize = (PresentedComponents.Fps ? 18 : 24) * factor;
        CompactFpsUnit.FontSize = 11 * factor;
        if (CompactLowPanel.Children[0] is TextBlock lowLabel) lowLabel.FontSize = 11 * factor;
        if (!PresentedComponents.Fps && !PresentedComponents.OnePercentLow) return;
        static double Width(FrameworkElement element)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return element.DesiredSize.Width;
        }
        static double CellWidth(FrameworkElement element) => double.IsFinite(element.Width)
            ? element.Width : Math.Max(element.MinWidth, element.DesiredSize.Width);
        if (PresentationRows is not null)
        {
            if (PrimaryContainerCell("fps") is { } fpsCell)
            {
                double fpsFit = Math.Min(1, Math.Max(1, CellWidth(fpsCell)) / Math.Max(1, Width(CompactFpsPanel)));
                CompactFps.FontSize *= fpsFit;
                CompactFpsUnit.FontSize *= fpsFit;
            }
            if (PrimaryContainerCell("low") is { } lowCell)
            {
                double lowFit = Math.Min(1, Math.Max(1, CellWidth(lowCell)) / Math.Max(1, Width(CompactLowPanel)));
                CompactOnePercentLow.FontSize *= lowFit;
                if (CompactLowPanel.Children[0] is TextBlock lowTitle) lowTitle.FontSize *= lowFit;
            }
            return;
        }
        double right = 0;
        if (PresentedComponents.Time) right += Width(CompactClock);
        if (_batteryVisible) right += Width(CompactBattery);
        double needed = (PresentedComponents.Fps ? Width(CompactFpsPanel) : 0) + (PresentedComponents.OnePercentLow ? Width(CompactLowPanel) : 0);
        double fit = needed > 0 ? Math.Min(1, Math.Max(1, CompactContentGrid.Width - right - 2) / needed) : 1;
        if (fit >= 1) return;
        CompactFps.FontSize *= fit;
        CompactOnePercentLow.FontSize *= fit;
        CompactFpsUnit.FontSize *= fit;
        if (CompactLowPanel.Children[0] is TextBlock label) label.FontSize *= fit;
    }

    private void CaptureLayoutFonts(object? root)
    {
        if (root is not DependencyObject node) return;
        if (node is TextBlock text && !_layoutBaseFonts.ContainsKey(text)
            && !text.FontFamily.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase)) _layoutBaseFonts[text] = text.FontSize;
        foreach (object child in LogicalTreeHelper.GetChildren(node)) CaptureLayoutFonts(child);
    }

    private void ResizeChartTextRows(double scale)
    {
        foreach (var card in new[] { CpuChartCard, GpuChartCard, MemoryChartCard, FpsChartCard, FrameTimeCard })
        {
            if (card.Child is not Grid grid || grid.RowDefinitions.Count < 3) continue;
            grid.RowDefinitions[0].Height = new GridLength(19 * Math.Max(1, scale));
            grid.RowDefinitions[2].Height = new GridLength(13 * Math.Max(1, scale));
        }
        ChartGapRow.Height = new GridLength(EnabledChartCount <= 2 ? 0 : 6);
        ChartSecondRow.Height = new GridLength(EnabledChartCount <= 2 ? 0 : ChartCardHeight);
        ChartsPanel.RowDefinitions[0].Height = new GridLength(ChartCardHeight);
    }
}
