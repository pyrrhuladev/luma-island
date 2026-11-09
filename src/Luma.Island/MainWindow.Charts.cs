using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Luma.Island.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly MetricHistory _cpuHistory, _gpuHistory, _memoryHistory, _fpsHistory;
    private string? _fpsHistoryProcess;
    private int EnabledChartCount => (PresentsChart("cpu") ? 1 : 0) + (PresentsChart("gpu") ? 1 : 0) +
        (PresentsChart("memory") ? 1 : 0) + (PresentsChart("fps") ? 1 : 0);
    private double ChartCardHeight => 64 + (LayoutGrowth - 1) * 32;
    public double ChartsHeight => EnabledChartCount == 0 ? 0
        : EnabledChartCount <= 2 ? ChartCardHeight + 6 : 2 * ChartCardHeight + 12;

    internal void RecordPerformanceHistory(PerformanceSnapshot snapshot)
    {
        VerifyAccess();
        if (_closed || !(Components.ShowCpuChart || Components.ShowGpuChart || Components.ShowMemoryChart)) return;
        double now = ChartTime();
        if (_islandOwner is null)
        {
            if (Components.ShowCpuChart) _cpuHistory.Add(now, CpuUtilityValue(snapshot.CpuPercent));
            if (Components.ShowGpuChart) _gpuHistory.Add(now, PercentValue(snapshot.GpuPercent));
            if (Components.ShowMemoryChart) _memoryHistory.Add(now, PercentValue(snapshot.MemoryPercent));
        }
        UpdatePerformanceChartValues(snapshot);
        if (PresentsChart("cpu")) DrawCpuChart(now);
        if (PresentsChart("gpu")) DrawChart(_gpuHistory, GpuChartCanvas, GpuChartGrid, GpuChartLine, 100, now);
        if (PresentsChart("memory")) DrawChart(_memoryHistory, MemoryChartCanvas, MemoryChartGrid, MemoryChartLine, 100, now);
    }

    private void UpdatePerformanceChartValues(PerformanceSnapshot snapshot)
    {
        if (PresentsChart("cpu"))
        {
            CpuChartValue.Text = CpuUtilityValue(snapshot.CpuPercent) is double cpu ? cpu.ToString("0", _culture) + "%" : "—";
            CpuChartCard.ToolTip = PresentedComponents.Cpu ? CompactCpu.ToolTip : CpuUsageDescription(snapshot.CpuUsageSource);
        }
        if (PresentsChart("gpu"))
        {
            GpuChartValue.Text = ChartPercent(snapshot.GpuPercent);
            GpuChartCard.ToolTip = PresentedComponents.Gpu ? CompactGpu.ToolTip : "Загрузка графического движка";
        }
        if (PresentsChart("memory"))
        {
            MemoryChartValue.Text = ChartPercent(snapshot.MemoryPercent);
            MemoryChartCard.ToolTip = PresentedComponents.Memory ? CompactMemory.ToolTip : "Загрузка оперативной памяти";
        }
    }

    internal void RecordFpsHistory(FpsSnapshot snapshot)
    {
        VerifyAccess();
        if (_closed || !Components.ShowFpsChart) return;
        double now = ChartTime();
        double? value = snapshot.Fps is >= 0 && double.IsFinite(snapshot.Fps.Value) ? snapshot.Fps : null;
        if (_islandOwner is null)
        {
            string? stream = snapshot.StreamId ?? snapshot.ProcessName;
            if (!string.Equals(_fpsHistoryProcess, stream, StringComparison.OrdinalIgnoreCase))
            {
                _fpsHistory.Clear();
                _fpsHistoryProcess = stream;
            }
            _fpsHistory.Add(now, value);
        }
        if (!PresentsChart("fps")) return;
        FpsChartValue.Text = value.HasValue ? value.Value.ToString("0", _culture) : "—";
        FpsChartCard.ToolTip = PresentedComponents.Fps ? CompactFps.ToolTip : snapshot.Status;
        DrawFpsChart(now);
    }

    internal void RefreshChartsVisibility()
    {
        VerifyAccess();
        ChartsPanel.Visibility = ChartsHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        var cards = new (string Metric, Border Card, bool Enabled, MetricHistory History, ShapePath Line, TextBlock Value)[]
        {
            ("cpu", CpuChartCard, Components.ShowCpuChart, _cpuHistory, CpuChartLine, CpuChartValue),
            ("gpu", GpuChartCard, Components.ShowGpuChart, _gpuHistory, GpuChartLine, GpuChartValue),
            ("memory", MemoryChartCard, Components.ShowMemoryChart, _memoryHistory, MemoryChartLine, MemoryChartValue),
            ("fps", FpsChartCard, Components.ShowFpsChart, _fpsHistory, FpsChartLine, FpsChartValue)
        };
        int visible = 0;
        if (PresentationCapsuleItems is { } order)
            cards = cards.OrderBy(card => Array.IndexOf(order, ChartId(card.Metric))).ToArray();
        foreach (var card in cards)
        {
            bool enabled = PresentsChart(card.Metric);
            card.Card.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled)
            {
                if (_islandOwner is null && !card.Enabled) card.History.Clear();
                card.Line.Data = null;
                card.Value.Text = "—";
                continue;
            }
            Grid.SetRow(card.Card, visible / 2 * 2);
            Grid.SetColumn(card.Card, visible % 2 * 2);
            Grid.SetColumnSpan(card.Card, EnabledChartCount == 1 ? 3 : 1);
            visible++;
        }
        if (_islandOwner is null && !Components.ShowFpsChart) _fpsHistoryProcess = null;
        if (visible > 0)
        {
            if (IsCapsuleLayoutActive) UpdatePerformanceChartValues(_lastPerformance);
            DrawAllCharts();
        }
    }

    public void ClearMetricHistory()
    {
        VerifyAccess();
        if (_islandOwner is null)
        {
            _cpuHistory.Clear(); _gpuHistory.Clear(); _memoryHistory.Clear(); _fpsHistory.Clear();
            _fpsHistoryProcess = null;
        }
        CpuChartLine.Data = GpuChartLine.Data = MemoryChartLine.Data = FpsChartLine.Data = null;
        ClearFrameTimeHistory();
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_closed && e.NewSize.Width > 0 && e.NewSize.Height > 0 && EnabledChartCount > 0) DrawAllCharts();
    }

    private void OnChartsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && !_closed && EnabledChartCount > 0) DrawAllCharts();
    }

    private void DrawAllCharts()
    {
        double now = ChartTime();
        if (PresentsChart("cpu")) DrawCpuChart(now);
        if (PresentsChart("gpu")) DrawChart(_gpuHistory, GpuChartCanvas, GpuChartGrid, GpuChartLine, 100, now);
        if (PresentsChart("memory")) DrawChart(_memoryHistory, MemoryChartCanvas, MemoryChartGrid, MemoryChartLine, 100, now);
        if (PresentsChart("fps")) DrawFpsChart(now);
    }

    private void DrawFpsChart(double now)
    {
        double maximum = MetricHistory.NiceFpsMaximum(_fpsHistory.Maximum(now));
        FpsChartScale.Text = $"0–{maximum:0} FPS";
        DrawChart(_fpsHistory, FpsChartCanvas, FpsChartGrid, FpsChartLine, maximum, now);
    }

    private void DrawCpuChart(double now)
    {
        double maximum = MetricHistory.NiceUtilityMaximum(_cpuHistory.Maximum(now));
        CpuChartScale.Text = $"0–{maximum:0}%";
        DrawChart(_cpuHistory, CpuChartCanvas, CpuChartGrid, CpuChartLine, maximum, now);
    }

    private static void DrawChart(MetricHistory history, Canvas canvas, ShapePath grid, ShapePath line, double maximum, double now)
    {
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        if (!canvas.IsVisible || width <= 4 || height <= 4) return;
        double left = 2, top = 2, plotWidth = width - 4, plotHeight = height - 4;
        var gridGeometry = new StreamGeometry();
        using (StreamGeometryContext drawing = gridGeometry.Open())
        {
            foreach (double fraction in new[] { 0d, .5, 1d })
            {
                double y = top + plotHeight * fraction;
                drawing.BeginFigure(new Point(left, y), false, false);
                drawing.LineTo(new Point(left + plotWidth, y), true, false);
            }
        }
        gridGeometry.Freeze();
        grid.Data = gridGeometry;
        var geometry = new GeometryGroup();
        var lines = new StreamGeometry();
        var segments = history.Segments(now);
        double firstTime = segments.Count > 0 ? segments[0][0].Time : now;
        double visibleSeconds = Math.Min(MetricHistory.WindowSeconds, Math.Max(20, now - firstTime));
        using (StreamGeometryContext drawing = lines.Open())
        {
            foreach (MetricHistory.Sample[] segment in segments)
            {
                Point Position(MetricHistory.Sample sample) => new(
                    left + Math.Clamp((sample.Time - now + visibleSeconds) / visibleSeconds, 0, 1) * plotWidth,
                    top + (1 - Math.Clamp(sample.Value!.Value / maximum, 0, 1)) * plotHeight);
                if (segment.Length == 1)
                {
                    geometry.Children.Add(new EllipseGeometry(Position(segment[0]), .8, .8));
                    continue;
                }
                drawing.BeginFigure(Position(segment[0]), false, false);
                for (int index = 1; index < segment.Length; index++) drawing.LineTo(Position(segment[index]), true, false);
            }
        }
        lines.Freeze();
        geometry.Children.Add(lines);
        geometry.Freeze();
        line.Data = geometry;
    }

    private static double ChartTime() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private static double? PercentValue(double? value) => value is >= 0 && double.IsFinite(value.Value) ? Math.Clamp(value.Value, 0, 100) : null;
    private static double? CpuUtilityValue(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
    private string ChartPercent(double? value) => PercentValue(value) is double percent ? percent.ToString("0", _culture) + "%" : "—";
}
