using System;
using System.Collections.Generic;
using System.Globalization;

namespace Luma.Island.Services;

public enum HudAlertSeverity { None, Warning, High }

public sealed record HudAlertView(bool Active, HudAlertSeverity Severity, string Message,
    bool HasTemperatureAlert = false, bool HasStutterAlert = false)
{
    public static HudAlertView None { get; } = new(false, HudAlertSeverity.None, "Нет активных предупреждений");
}

/// <summary>Bounded warning state driven exclusively by existing measurements and a monotonic clock.</summary>
public sealed class HudAlertEngine
{
    public const double TemperatureSustainSeconds = 4;
    public const double TemperatureFreshSeconds = 6;
    public const double TemperatureHysteresis = 3;
    public const double StutterWindowSeconds = 2;
    public const double StutterHoldSeconds = 3;
    public const int MaximumQueuedSlowFrames = 64;
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly TemperatureState _cpu = new(), _gpu = new(), _memory = new();
    private readonly Queue<SlowFrame> _slowFrames = new();
    private AlertSettings _settings = AlertSettings.Default;
    private double _lastClock = double.NaN, _lastFrame = double.NegativeInfinity;
    private double _stutterUntil = double.NegativeInfinity, _stutterPeak;
    private string? _stream;
    private bool _frameBatchAvailable;
    public int RetainedSlowFrameCount => _slowFrames.Count;
    public AlertSettings Settings => _settings;

    public void Configure(AlertSettings? settings, double nowSeconds)
    {
        settings = AlertSettings.Normalize(settings);
        if (_settings != settings) { _settings = settings; Reset(); }
        if (!Clock(nowSeconds) || !_settings.Enabled) ResetMeasurements();
    }

    public void ObserveTemperatures(double? cpu, double? gpu, double? memory, double nowSeconds)
    {
        if (!Clock(nowSeconds) || !_settings.Enabled) { ResetMeasurements(); return; }
        _cpu.Observe(cpu, _settings.NeedsCpuTemperature, _settings.CpuThreshold, nowSeconds);
        _gpu.Observe(gpu, _settings.NeedsGpuTemperature, _settings.GpuThreshold, nowSeconds);
        _memory.Observe(memory, _settings.NeedsMemoryTemperature, _settings.MemoryThreshold, nowSeconds);
    }

    /// <summary>Starts one real-frame batch. Unavailable data and explicit capture gaps clear warning history.</summary>
    public void BeginFrameBatch(string? streamId, bool hasGap, bool sourceAvailable, double nowSeconds)
    {
        if (!Clock(nowSeconds) || !_settings.NeedsFrames)
        {
            ResetFrames();
            return;
        }
        if (!string.Equals(streamId, _stream, StringComparison.Ordinal))
        {
            ResetFrames();
            _stream = streamId;
        }
        _frameBatchAvailable = sourceAvailable && !string.IsNullOrEmpty(_stream);
        if (!_frameBatchAvailable || hasGap) ClearStutters();
        // Preserve the last consumed timestamp for a gap in the same stream: replaying an old
        // batch after a loss must not count those frames for a second time.
    }

    public void ObserveFrame(double timeSeconds, double milliseconds, double nowSeconds)
    {
        if (!Clock(nowSeconds) || !_frameBatchAvailable || !_settings.NeedsFrames ||
            !double.IsFinite(timeSeconds) || timeSeconds < 0 || !double.IsFinite(milliseconds) ||
            milliseconds <= 0 || milliseconds > 60000 || timeSeconds <= _lastFrame ||
            timeSeconds > nowSeconds + .25 || nowSeconds - timeSeconds > 3) return;
        _lastFrame = timeSeconds;
        while (_slowFrames.TryPeek(out var oldest) && timeSeconds - oldest.Time > StutterWindowSeconds)
            _slowFrames.Dequeue();
        if (milliseconds < _settings.FrameTimeThreshold) return;
        if (_slowFrames.Count == MaximumQueuedSlowFrames) _slowFrames.Dequeue();
        _slowFrames.Enqueue(new(timeSeconds, milliseconds));
        if (_slowFrames.Count < _settings.StutterCount) return;
        _stutterUntil = timeSeconds + StutterHoldSeconds;
        _stutterPeak = 0;
        foreach (var frame in _slowFrames) _stutterPeak = Math.Max(_stutterPeak, frame.Milliseconds);
    }

    public HudAlertView View(double nowSeconds)
    {
        if (!Clock(nowSeconds) || !_settings.Enabled) return HudAlertView.None;
        _cpu.Expire(nowSeconds); _gpu.Expire(nowSeconds); _memory.Expire(nowSeconds);
        bool temperatures = _cpu.Active || _gpu.Active || _memory.Active;
        bool stutters = _settings.NeedsFrames && _frameBatchAvailable && _stutterUntil > nowSeconds &&
            nowSeconds - _lastFrame <= StutterHoldSeconds;
        if (!temperatures && !stutters) return HudAlertView.None;
        var messages = new List<string>(4);
        bool high = false;
        AddTemperature(_cpu, "CPU", _settings.CpuThreshold);
        AddTemperature(_gpu, "GPU", _settings.GpuThreshold);
        AddTemperature(_memory, "RAM", _settings.MemoryThreshold);
        if (stutters) messages.Add("Серия просадок · кадр до " + _stutterPeak.ToString("0.0", Russian) + " мс");
        return new(true, high ? HudAlertSeverity.High : HudAlertSeverity.Warning,
            string.Join("\n", messages), temperatures, stutters);

        void AddTemperature(TemperatureState state, string name, double threshold)
        {
            if (!state.Active) return;
            messages.Add(name + " " + state.Value.ToString("0", Russian) + " °C · порог " + threshold.ToString("0", Russian) + " °C");
            high |= state.Value >= threshold + 5;
        }
    }

    /// <summary>The first active warning expiry, for one UI timeout rather than a sampling loop.</summary>
    public double? NextExpirySeconds(double nowSeconds)
    {
        if (!Clock(nowSeconds) || !_settings.Enabled) return null;
        _cpu.Expire(nowSeconds); _gpu.Expire(nowSeconds); _memory.Expire(nowSeconds);
        double next = double.PositiveInfinity;
        Include(_cpu.Expiry); Include(_gpu.Expiry); Include(_memory.Expiry);
        if (_settings.NeedsFrames && _frameBatchAvailable && _stutterUntil > nowSeconds &&
            nowSeconds - _lastFrame <= StutterHoldSeconds)
            Include(Math.Min(_stutterUntil, _lastFrame + StutterHoldSeconds));
        return double.IsFinite(next) ? next : null;
        void Include(double? deadline) { if (deadline.HasValue) next = Math.Min(next, deadline.Value); }
    }

    public void Reset()
    {
        _lastClock = double.NaN;
        ResetMeasurements();
    }

    private bool Clock(double now)
    {
        if (!double.IsFinite(now) || now < 0) { ResetMeasurements(); return false; }
        if (double.IsFinite(_lastClock) && now < _lastClock) ResetMeasurements();
        _lastClock = now;
        return true;
    }

    private void ResetMeasurements()
    {
        _cpu.Reset(); _gpu.Reset(); _memory.Reset(); ResetFrames();
    }
    private void ResetFrames()
    {
        ClearStutters();
        _stream = null;
        _lastFrame = double.NegativeInfinity;
        _frameBatchAvailable = false;
    }
    private void ClearStutters()
    {
        _slowFrames.Clear();
        _stutterUntil = double.NegativeInfinity;
        _stutterPeak = 0;
    }
    private readonly record struct SlowFrame(double Time, double Milliseconds);

    private sealed class TemperatureState
    {
        public bool Active { get; private set; }
        public double Value { get; private set; }
        public double? Expiry => Active ? _last + TemperatureFreshSeconds : null;
        private double? _hotSince;
        private double _last = double.NegativeInfinity;
        public void Observe(double? value, bool enabled, double threshold, double now)
        {
            if (!enabled || value is not > 0 or > 200 || !double.IsFinite(value.Value)) { Reset(); return; }
            if (now - _last > TemperatureFreshSeconds) Reset();
            _last = now;
            Value = value.Value;
            if (Active)
            {
                if (Value <= threshold - TemperatureHysteresis) Reset();
                return;
            }
            if (Value < threshold) { _hotSince = null; return; }
            _hotSince ??= now;
            Active = now - _hotSince.Value >= TemperatureSustainSeconds;
        }
        public void Expire(double now) { if (now - _last > TemperatureFreshSeconds) Reset(); }
        public void Reset() { Active = false; Value = 0; _hotSince = null; _last = double.NegativeInfinity; }
    }
}
