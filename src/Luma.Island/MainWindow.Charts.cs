using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Luma.Island.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly MetricHistory _cpuHistory = new(), _gpuHistory = new(), _memoryHistory = new(), _fpsHistory = new();
    private string? _fpsHistoryProcess;
    private int EnabledChartCount => (Components.Cpu ? 1 : 0) + (Components.Gpu ? 1 : 0) +
        (Components.Memory ? 1 : 0) + (Components.Fps ? 1 : 0);
    private double ChartCardHeight => 64 + (LayoutGrowth - 1) * 32;
    public double ChartsHeight => !Components.Charts || EnabledChartCount == 0 ? 0
        : EnabledChartCount <= 2 ? ChartCardHeight + 6 : 2 * ChartCardHeight + 12;

    internal void RecordPerformanceHistory(PerformanceSnapshot snapshot)
    {
        VerifyAccess();
        if (!Components.Charts || _closed) return;
        double now = ChartTime();
        if (Components.Cpu)
        {
            _cpuHistory.Add(now, CpuUtilityValue(snapshot.CpuPercent));
            CpuChartValue.Text = CpuUtilityValue(snapshot.CpuPercent) is double cpu ? cpu.ToString("0", _culture) + "%" : "—";
            CpuChartCard.ToolTip = CompactCpu.ToolTip;
            DrawCpuChart(now);
        }
        if (Components.Gpu)
        {
            _gpuHistory.Add(now, PercentValue(snapshot.GpuPercent));
            GpuChartValue.Text = ChartPercent(snapshot.GpuPercent);
            GpuChartCard.ToolTip = CompactGpu.ToolTip;
            DrawChart(_gpuHistory, GpuChartCanvas, GpuChartGrid, GpuChartLine, 100, now);
        }
        if (Components.Memory)
        {
            _memoryHistory.Add(now, PercentValue(snapshot.MemoryPercent));
            MemoryChartValue.Text = ChartPercent(snapshot.MemoryPercent);
            MemoryChartCard.ToolTip = CompactMemory.ToolTip;
            DrawChart(_memoryHistory, MemoryChartCanvas, MemoryChartGrid, MemoryChartLine, 100, now);
        }
    }

    internal void RecordFpsHistory(FpsSnapshot snapshot)
    {
        VerifyAccess();
        if (!Components.Charts || !Components.Fps || _closed) return;
        string? stream = snapshot.StreamId ?? snapshot.ProcessName;
        if (!string.Equals(_fpsHistoryProcess, stream, StringComparison.OrdinalIgnoreCase))
        {
            _fpsHistory.Clear();
            _fpsHistoryProcess = stream;
        }
        double now = ChartTime();
        double? value = snapshot.Fps is >= 0 && double.IsFinite(snapshot.Fps.Value) ? snapshot.Fps : null;
        _fpsHistory.Add(now, value);
        FpsChartValue.Text = value.HasValue ? value.Value.ToString("0", _culture) : "—";
        FpsChartCard.ToolTip = CompactFps.ToolTip;
        DrawFpsChart(now);
    }

    internal void RefreshChartsVisibility()
    {
        VerifyAccess();
        ChartsPanel.Visibility = ChartsHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        var cards = new (Border Card, bool Enabled, MetricHistory History, ShapePath Line, TextBlock Value)[]
        {
            (CpuChartCard, Components.Cpu, _cpuHistory, CpuChartLine, CpuChartValue),
            (GpuChartCard, Components.Gpu, _gpuHistory, GpuChartLine, GpuChartValue),
            (MemoryChartCard, Components.Memory, _memoryHistory, MemoryChartLine, MemoryChartValue),
            (FpsChartCard, Components.Fps, _fpsHistory, FpsChartLine, FpsChartValue)
        };
        int visible = 0;
        foreach (var card in cards)
        {
            bool enabled = Components.Charts && card.Enabled;
            card.Card.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled)
            {
                card.History.Clear();
                card.Line.Data = null;
                card.Value.Text = "—";
                continue;
            }
            Grid.SetRow(card.Card, visible / 2 * 2);
            Grid.SetColumn(card.Card, visible % 2 * 2);
            Grid.SetColumnSpan(card.Card, EnabledChartCount == 1 ? 3 : 1);
            visible++;
        }
        if (!Components.Charts || !Components.Fps) _fpsHistoryProcess = null;
        if (Components.Charts) DrawAllCharts();
    }

    public void ClearMetricHistory()
    {
        VerifyAccess();
        _cpuHistory.Clear(); _gpuHistory.Clear(); _memoryHistory.Clear(); _fpsHistory.Clear();
        _fpsHistoryProcess = null;
        CpuChartLine.Data = GpuChartLine.Data = MemoryChartLine.Data = FpsChartLine.Data = null;
        ClearFrameTimeHistory();
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Components.Charts && !_closed && e.NewSize.Width > 0 && e.NewSize.Height > 0) DrawAllCharts();
    }

    private void OnChartsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && Components.Charts && !_closed) DrawAllCharts();
    }

    private void DrawAllCharts()
    {
        double now = ChartTime();
        if (Components.Cpu) DrawCpuChart(now);
        if (Components.Gpu) DrawChart(_gpuHistory, GpuChartCanvas, GpuChartGrid, GpuChartLine, 100, now);
        if (Components.Memory) DrawChart(_memoryHistory, MemoryChartCanvas, MemoryChartGrid, MemoryChartLine, 100, now);
        if (Components.Fps) DrawFpsChart(now);
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
