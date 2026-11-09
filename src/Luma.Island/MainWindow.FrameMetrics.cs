using System;
using System.Windows;
using System.Windows.Media;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly FrameTimeHistory _frameTimeHistory;
    private FpsSnapshot _lastFrameMetrics = new(null, null, "Ожидание кадров");
    private string? _frameTimeStream;

    private void ApplyFrameMetrics(FpsSnapshot snapshot)
    {
        _lastFrameMetrics = snapshot with { Frames = null };
        UpdateFrameMetricLabels();
        if (_islandOwner is null && Components.FrameTime) RecordFrameTime(snapshot);
        DrawFrameTimeChart();
    }

    private void RecordFrameTime(FpsSnapshot snapshot)
    {
        string? stream = snapshot.StreamId ?? snapshot.ProcessName;
        if (!string.Equals(_frameTimeStream, stream, StringComparison.Ordinal))
        {
            _frameTimeHistory.Clear();
            _frameTimeStream = stream;
        }
        if (snapshot.HasFrameGap)
        {
            double gapTime = snapshot.Frames is { Length: > 0 } frames ? frames[0].TimeSeconds - .000001 : ChartTime();
            _frameTimeHistory.MarkGap(gapTime);
        }
        if (snapshot.Frames is not null)
            foreach (var frame in snapshot.Frames) _frameTimeHistory.Add(frame.TimeSeconds, frame.Milliseconds);
    }

    private void UpdateFrameMetricLabels()
    {
        double? low = _lastFrameMetrics.OnePercentLow;
        string lowText = low is > 0 and <= 20000 && double.IsFinite(low.Value) ? low.Value.ToString("0", _culture) : "—";
        CompactOnePercentLow.Text = lowText;
        string lowTip = _lastFrameMetrics.OnePercentLowStatus ?? "1% Low · NVIDIA FrameView. Ожидание данных наиболее медленных кадров.";
        CompactLowPanel.ToolTip = CompactOnePercentLow.ToolTip = lowTip;
        double? milliseconds = _lastFrameMetrics.FrameTimeMilliseconds;
        string timeText = milliseconds is > 0 and <= 60000 && double.IsFinite(milliseconds.Value)
            ? milliseconds.Value.ToString("0.0", _culture) + " мс" : "—";
        FrameTimeValue.Text = timeText;
        string frameTip = "Интервалы между Present игры · NVIDIA FrameView.\n" +
            (_lastFrameMetrics.FrameTimeStatus ?? "Ожидание отдельных кадров.");
        FrameTimeCard.ToolTip = frameTip;
    }

    private void RefreshFrameMetricVisibility()
    {
        if (!Components.OnePercentLow) _lastFrameMetrics = _lastFrameMetrics with { OnePercentLow = null };
        if (!Components.FrameTime)
        {
            _lastFrameMetrics = _lastFrameMetrics with { FrameTimeMilliseconds = null, Frames = null };
            ClearFrameTimeHistory();
        }
        UpdateFrameMetricLabels();
    }

    private void ClearFrameTimeHistory()
    {
        if (_islandOwner is null)
        {
            _frameTimeHistory.Clear();
            _frameTimeStream = null;
        }
        FrameTimeLine.Data = null;
        FrameTimePeak.Text = "до 10 с";
        FrameTimeScale.Text = "0–16 мс";
    }

    private void OnFrameTimeSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_closed && PresentedComponents.FrameTime && args.NewSize.Width > 0) DrawFrameTimeChart();
    }

    private void OnFrameTimeVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!_closed && PresentedComponents.FrameTime && args.NewValue is true) DrawFrameTimeChart();
    }

    private void DrawFrameTimeChart()
    {
        if (!PresentedComponents.FrameTime || !FrameTimeCanvas.IsVisible || FrameTimeCanvas.ActualWidth <= 4 || FrameTimeCanvas.ActualHeight <= 4) return;
        double now = ChartTime(), width = FrameTimeCanvas.ActualWidth - 4, height = FrameTimeCanvas.ActualHeight - 4;
        double peak = _frameTimeHistory.Maximum(now);
        double maximum = FrameTimeHistory.NiceMaximum(peak);
        FrameTimeScale.Text = $"0–{maximum:0} мс";
        FrameTimePeak.Text = peak > 0 ? $"Пик {peak.ToString("0.0", _culture)} мс · 10 с" : "до 10 с";
        var grid = new StreamGeometry();
        using (var drawing = grid.Open())
        {
            foreach (double fraction in new[] { 0d, .5, 1d })
            {
                double y = 2 + height * fraction;
                drawing.BeginFigure(new Point(2, y), false, false);
                drawing.LineTo(new Point(2 + width, y), true, false);
            }
        }
        grid.Freeze();
        FrameTimeGrid.Data = grid;
        var geometry = new GeometryGroup();
        var lines = new StreamGeometry();
        using (var drawing = lines.Open())
        {
            foreach (var segment in _frameTimeHistory.Segments(now, Math.Max(1, (int)Math.Ceiling(width))))
            {
                Point Position(FrameTimeHistory.Sample sample) => new(
                    2 + Math.Clamp((sample.Time - now + FrameTimeHistory.WindowSeconds) / FrameTimeHistory.WindowSeconds, 0, 1) * width,
                    2 + (1 - Math.Clamp(sample.Value.GetValueOrDefault() / maximum, 0, 1)) * height);
                if (segment.Length == 1) geometry.Children.Add(new EllipseGeometry(Position(segment[0]), .8, .8));
                else if (segment.Length > 1)
                {
                    drawing.BeginFigure(Position(segment[0]), false, false);
                    for (int index = 1; index < segment.Length; index++) drawing.LineTo(Position(segment[index]), true, false);
                }
            }
        }
        lines.Freeze(); geometry.Children.Add(lines); geometry.Freeze();
        FrameTimeLine.Data = geometry;
    }
}
