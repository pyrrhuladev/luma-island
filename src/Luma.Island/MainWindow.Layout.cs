using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly Dictionary<TextBlock, double> _layoutBaseFonts = new();
    private bool _layoutFontsCaptured;
    private string? _appliedBlockOrder, _appliedMetricOrder;
    private (int Fps, int Low, bool Clock, bool Battery, double TextScale)? _headerFitKey;
    private HudLayoutSettings LayoutSettings => Appearance.Layout ?? HudLayoutSettings.Default;
    private double LayoutTextScale => LayoutSettings.TextScale;
    private double LayoutGrowth => Math.Max(1, LayoutTextScale);
    private double LayoutSpacing => LayoutSettings.Spacing;
    private int VisibleHardwareCount => (Components.Cpu || Components.CpuTemperature ? 1 : 0)
        + (Components.Gpu || Components.GpuTemperature ? 1 : 0) + (Components.Memory || Components.MemoryTemperature ? 1 : 0);
    private int LayoutMetricColumns => Math.Max(1, Math.Min(VisibleHardwareCount, LayoutSettings.MetricColumns));
    private int LayoutMetricRows => VisibleHardwareCount == 0 ? 0 : (VisibleHardwareCount + LayoutMetricColumns - 1) / LayoutMetricColumns;
    private double CompactMetricCellHeight => (28 + TemperatureRowHeight) * LayoutGrowth;
    private double CompactMetricGridHeight => LayoutMetricRows == 0 ? 0
        : CompactMetricCellHeight * LayoutMetricRows + LayoutSpacing * (LayoutMetricRows - 1);
    private double LayoutFrameTimeHeight => Components.FrameTime ? 72 + (LayoutGrowth - 1) * 32 : 0;

    private double LayoutBlockGap(string block)
    {
        bool previousVisible = false;
        foreach (string key in LayoutSettings.BlockOrder.Split(','))
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
        Appearance = Appearance with { Layout = normalized };
        ApplyComponentVisibility();
        OnMediaChanged(_lastMedia ?? _media.Current);
        SaveAppearance();
    }

    private void ApplyHudLayout()
    {
        if (CompactContentGrid is null) return;
        var layout = HudLayoutSettings.Normalize(Appearance.Layout);
        if (!_layoutFontsCaptured && Content is not null) { CaptureLayoutFonts(Content); _layoutFontsCaptured = true; }
        foreach (var item in _layoutBaseFonts) item.Key.FontSize = item.Value * layout.TextScale;
        CompactCpu.LineHeight = CompactGpu.LineHeight = CompactMemory.LineHeight = 16 * layout.TextScale;

        if (_appliedBlockOrder != layout.BlockOrder)
        {
            var rows = new Dictionary<string, (RowDefinition Row, FrameworkElement Block)>
            {
                ["header"] = (HudHeaderRow, HudHeader), ["metrics"] = (HudMetricsRow, CompactPerformance),
                ["music"] = (HudMusicRow, CompactMusicRow)
            };
            CompactContentGrid.RowDefinitions.Clear();
            foreach (string key in layout.BlockOrder.Split(','))
            {
                var entry = rows[key];
                Grid.SetRow(entry.Block, CompactContentGrid.RowDefinitions.Count);
                CompactContentGrid.RowDefinitions.Add(entry.Row);
            }
            CompactContentGrid.RowDefinitions.Add(ChartsRow);
            CompactContentGrid.RowDefinitions.Add(FrameTimeRow);
            CompactContentGrid.RowDefinitions.Add(VolumeRow);
            CompactContentGrid.RowDefinitions.Add(FooterRow);
            _appliedBlockOrder = layout.BlockOrder;
        }
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
        CompactLowPanel.Margin = new Thickness(Components.Fps ? 12 : 0, 0, 0, 0);
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

    // Tabular FPS numerals only change width when their digit count changes. Avoid new
    // measurements on every frame update, while keeping large user fonts inside the capsule.
    private void FitHeaderText(bool force = false)
    {
        var key = (Components.Fps ? CompactFps.Text.Length : 0, Components.OnePercentLow ? CompactOnePercentLow.Text.Length : 0,
            Components.Time, _batteryVisible, LayoutTextScale);
        if (!force && _headerFitKey == key) return;
        _headerFitKey = key;
        double factor = LayoutTextScale;
        CompactFps.FontSize = 34 * factor;
        CompactOnePercentLow.FontSize = (Components.Fps ? 18 : 24) * factor;
        CompactFpsUnit.FontSize = 11 * factor;
        if (CompactLowPanel.Children[0] is TextBlock lowLabel) lowLabel.FontSize = 11 * factor;
        if (!Components.Fps && !Components.OnePercentLow) return;
        static double Width(FrameworkElement element)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return element.DesiredSize.Width;
        }
        double right = 0;
        if (Components.Time) right += Width(CompactClock);
        if (_batteryVisible) right += Width(CompactBattery);
        double needed = (Components.Fps ? Width(CompactFpsPanel) : 0) + (Components.OnePercentLow ? Width(CompactLowPanel) : 0);
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
