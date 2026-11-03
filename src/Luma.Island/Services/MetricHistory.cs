using System;
using System.Collections.Generic;

namespace Luma.Island.Services;

/// <summary>Two-minute history, capped at sixty two-second buckets. Missing readings remain gaps.</summary>
public sealed class MetricHistory
{
    public const int Capacity = 60;
    public const double WindowSeconds = 120;
    private const double BucketSeconds = 2;
    private const double MaximumConnectedGap = 4.5;
    private readonly List<Sample> _samples = new(Capacity);
    public readonly record struct Sample(double Time, double? Value);
    public int Count => _samples.Count;

    public void Add(double now, double? value)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (!value.HasValue || !double.IsFinite(value.Value) || value.Value < 0) value = null;
        if (_samples.Count > 0 && now < _samples[^1].Time) _samples.Clear();
        _samples.RemoveAll(sample => sample.Time < now - WindowSeconds);
        if (_samples.Count > 0 && Math.Floor(_samples[^1].Time / BucketSeconds) == Math.Floor(now / BucketSeconds))
        {
            // A missing reading must not disappear when a later value arrives in the same bucket.
            _samples[^1] = new Sample(now, _samples[^1].Value.HasValue ? value : null);
            return;
        }
        if (_samples.Count == Capacity) _samples.RemoveAt(0);
        _samples.Add(new Sample(now, value));
    }

    public IReadOnlyList<Sample[]> Segments(double now)
    {
        var result = new List<Sample[]>();
        var segment = new List<Sample>();
        double previous = double.NegativeInfinity;
        foreach (Sample sample in _samples)
        {
            if (sample.Time < now - WindowSeconds || sample.Time > now) continue;
            if (!sample.Value.HasValue || sample.Time - previous > MaximumConnectedGap)
            {
                if (segment.Count > 0) { result.Add(segment.ToArray()); segment.Clear(); }
            }
            if (sample.Value.HasValue) segment.Add(sample);
            previous = sample.Time;
        }
        if (segment.Count > 0) result.Add(segment.ToArray());
        return result;
    }

    public double Maximum(double now)
    {
        double maximum = 0;
        foreach (Sample sample in _samples)
            if (sample.Time >= now - WindowSeconds && sample.Time <= now && sample.Value.HasValue)
                maximum = Math.Max(maximum, sample.Value.Value);
        return maximum;
    }

    public void Clear() => _samples.Clear();

    public static double NiceFpsMaximum(double maximum)
    {
        if (!double.IsFinite(maximum) || maximum <= 0) return 60;
        double desired = Math.Max(30, maximum * 1.15);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(desired)));
        double normalized = desired / magnitude;
        return (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
    }
}
