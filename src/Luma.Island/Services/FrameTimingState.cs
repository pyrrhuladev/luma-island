using System;
using System.Collections.Generic;
using System.Linq;

namespace Luma.Island.Services;

/// <summary>PID/swapchain isolation and bounded batches; all times use the machine QPC clock.</summary>
internal sealed class FrameTimingState
{
    private const int MaximumPendingFrames = 4096;
    private readonly double _frequency;
    private readonly string _sessionId;
    private readonly List<FrameTimeSample> _pending = new(256);
    private uint _target;
    private string? _name;
    private ulong? _swapChain;
    private FrameViewMetric? _average, _low;
    private FrameViewFrame? _lastFrame;
    private bool _gap;
    private FrameViewStatistics _statistics = new(Array.Empty<FrameViewMetric>(), Array.Empty<FrameViewMetric>());

    public FrameTimingState(double frequency, string sessionId)
    {
        if (!double.IsFinite(frequency) || frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _frequency = frequency; _sessionId = sessionId;
    }

    public bool SetTarget(uint target, string? name)
    {
        _name = name;
        if (_target == target) return false;
        _target = target; _swapChain = null; _average = _low = null; _lastFrame = null;
        _statistics = new(Array.Empty<FrameViewMetric>(), Array.Empty<FrameViewMetric>());
        _pending.Clear(); _gap = true;
        return true;
    }

    public void ObserveStatistics(FrameViewStatistics statistics, double now)
    {
        _statistics = statistics;
        if (_target == 0) return;
        // A fresh actual frame pins the stream; otherwise a newer metric can establish it.
        if (!_lastFrame.HasValue || !Fresh(_lastFrame.Value.Timestamp, now, 1))
        {
            var anchor = Choose(statistics.Average, now, _swapChain);
            if (!anchor.HasValue || anchor.Value.SwapChain == 0)
                anchor = Choose(statistics.Average, now, null, nonzeroOnly: true) ?? anchor;
            anchor ??= Choose(statistics.Low, now, _swapChain) ?? Choose(statistics.Low, now, null);
            if (anchor.HasValue && (anchor.Value.SwapChain != 0 || !_swapChain.HasValue)) SetSwapChain(anchor.Value.SwapChain);
        }
        RefreshMetrics(now);
    }

    public void ObserveFrames(FrameViewFrameBatch batch, double now)
    {
        if (batch.HasGap) { _pending.Clear(); _lastFrame = null; _gap = true; }
        if (_target == 0) return;
        var frames = batch.Frames.Where(frame => frame.ProcessId == _target && Fresh(frame.Timestamp, now) &&
            frame.Milliseconds is > 0 and <= 60000 && double.IsFinite(frame.Milliseconds)).ToArray();
        if (frames.Length == 0) return;
        bool hasPreferred = _swapChain.HasValue && frames.Any(frame => frame.SwapChain == _swapChain.Value);
        if (!hasPreferred && (!_lastFrame.HasValue || !Fresh(_lastFrame.Value.Timestamp, now, 1)))
        {
            var selected = frames.GroupBy(frame => frame.SwapChain).OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Max(frame => frame.Timestamp)).First();
            SetSwapChain(selected.Key);
            RefreshMetrics(now);
        }
        foreach (var frame in frames.Where(frame => frame.SwapChain == _swapChain).OrderBy(frame => frame.Timestamp))
        {
            if (_lastFrame.HasValue && frame.Timestamp <= _lastFrame.Value.Timestamp) continue;
            if (_pending.Count == MaximumPendingFrames) { _pending.Clear(); _gap = true; }
            _pending.Add(new(frame.Timestamp / _frequency, frame.Milliseconds));
            _lastFrame = frame;
        }
    }

    public FpsSnapshot TakeSnapshot(bool fps, bool low, bool frameTime, IFrameViewSource source, double now)
    {
        double? averageValue = ValidCached(_average, now), lowValue = source.LowSupported ? ValidCached(_low, now) : null;
        double? frameValue = source.FramesSupported && _lastFrame.HasValue && Fresh(_lastFrame.Value.Timestamp, now)
            ? _lastFrame.Value.Milliseconds : null;
        string waiting = _target == 0 ? "Откройте игру или приложение" : "Ожидание кадров приложения";
        string status = averageValue.HasValue ? "FPS приложения · NVIDIA FrameView" : waiting;
        string lowStatus = !low ? "1% Low выключен" : !source.LowSupported ? source.LowStatus
            : lowValue.HasValue ? source.LowStatus : waiting + " · 1% Low";
        string frameStatus = !frameTime ? "График времени кадра выключен" : !source.FramesSupported ? source.FrameTimeStatus
            : frameValue.HasValue ? source.FrameTimeStatus : waiting + " · время кадра";
        var snapshot = new FpsSnapshot(fps ? averageValue : null, _name, status, low ? lowValue : null,
            frameTime ? frameValue : null, frameTime && source.FramesSupported && _pending.Count > 0 ? _pending.ToArray() : null,
            _target == 0 ? null : $"{_sessionId}/{_target}/{_swapChain?.ToString("X") ?? "waiting"}",
            lowStatus, frameStatus, frameTime && _gap);
        _pending.Clear(); _gap = false;
        return snapshot;
    }

    private void SetSwapChain(ulong chain)
    {
        if (_swapChain == chain) return;
        _swapChain = chain; _average = _low = null; _lastFrame = null; _pending.Clear(); _gap = true;
    }

    private void RefreshMetrics(double now)
    {
        _average = Choose(_statistics.Average, now, _swapChain) ?? (ValidCached(_average, now).HasValue ? _average : null);
        _low = Choose(_statistics.Low, now, _swapChain) ?? (ValidCached(_low, now).HasValue ? _low : null);
    }

    private FrameViewMetric? Choose(FrameViewMetric[] samples, double now, ulong? chain, bool nonzeroOnly = false)
    {
        FrameViewMetric? selected = null;
        foreach (var sample in samples)
        {
            if (sample.ProcessId != _target || !Fresh(sample.Timestamp, now) || sample.Value is <= 0 or > 20000 || !double.IsFinite(sample.Value)) continue;
            if (nonzeroOnly && sample.SwapChain == 0) continue;
            // The SDK can omit a swapchain (zero). Match that by PID, never by another nonzero chain.
            if (chain.HasValue && sample.SwapChain != chain.Value && sample.SwapChain != 0) continue;
            bool exact = chain.HasValue && sample.SwapChain == chain.Value;
            bool previousExact = chain.HasValue && selected.HasValue && selected.Value.SwapChain == chain.Value;
            if (!selected.HasValue || exact && !previousExact || exact == previousExact &&
                (sample.Timestamp > selected.Value.Timestamp || sample.Timestamp == selected.Value.Timestamp && sample.Value > selected.Value.Value)) selected = sample;
        }
        return selected;
    }

    private double? ValidCached(FrameViewMetric? sample, double now) => sample.HasValue && sample.Value.ProcessId == _target &&
        (!_swapChain.HasValue || sample.Value.SwapChain == _swapChain || sample.Value.SwapChain == 0) && Fresh(sample.Value.Timestamp, now)
        ? sample.Value.Value : null;

    private bool Fresh(ulong timestamp, double now, double maximumAge = 3)
    {
        double age = now - timestamp / _frequency;
        return timestamp != 0 && age >= -.25 && age <= maximumAge;
    }
}
