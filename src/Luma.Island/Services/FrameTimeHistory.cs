using System;
using System.Collections.Generic;

namespace Luma.Island.Services;

/// <summary>
/// Ten seconds of real frame intervals, compressed into 25 ms buckets without averaging away spikes.
/// Times use monotonic QPC seconds. The caller clears history when the process or capture stream changes.
/// </summary>
public sealed class FrameTimeHistory
{
    public const int Capacity = 512;
    public const double WindowSeconds = 10;
    public const double BucketSeconds = .025;
    public const double MaximumFrameMilliseconds = 60000;
    private const double MaximumUnexplainedGapSeconds = .25;
    private readonly Bucket[] _buckets = new Bucket[Capacity];
    private int _head, _count;
    private long _segment;
    private double _lastInputTime = double.NegativeInfinity;
    private double _lastFrameTime;
    private bool _hasLastFrame;

    public readonly record struct Sample(double Time, double? Value);
    public int Count => _count;

    /// <summary>Returns false for duplicate, late or invalid samples. A new invalid reading creates a gap.</summary>
    public bool Add(double timeSeconds, double? milliseconds)
    {
        if (!ValidTime(timeSeconds) || timeSeconds <= _lastInputTime) return false;
        if (milliseconds is not > 0 || !double.IsFinite(milliseconds.Value) || milliseconds > MaximumFrameMilliseconds)
        {
            MarkGap(timeSeconds);
            return false;
        }
        if (_hasLastFrame && timeSeconds - _lastFrameTime >
            Math.Max(MaximumUnexplainedGapSeconds, milliseconds.Value / 1000 * 1.5 + .01)) _segment++;
        _lastInputTime = _lastFrameTime = timeSeconds;
        _hasLastFrame = true;
        Trim(timeSeconds);

        var sample = new Sample(timeSeconds, milliseconds);
        double key = Math.Floor(timeSeconds / BucketSeconds);
        if (_count > 0)
        {
            int last = Index(_count - 1);
            if (_buckets[last].Key == key && _buckets[last].Segment == _segment)
            {
                _buckets[last].Add(sample);
                return true;
            }
        }
        if (_count == Capacity) { _head = (_head + 1) % Capacity; _count--; }
        _buckets[Index(_count++)] = new Bucket(key, _segment, sample);
        return true;
    }

    /// <summary>Record an explicit capture discontinuity. Later data in the same bucket cannot bridge it.</summary>
    public bool MarkGap(double timeSeconds)
    {
        if (!ValidTime(timeSeconds) || timeSeconds < _lastInputTime) return false;
        _lastInputTime = timeSeconds;
        _hasLastFrame = false;
        _segment++;
        Trim(timeSeconds);
        return true;
    }

    /// <summary>Reduce each visible segment by screen columns, retaining actual first/min/max/last points.</summary>
    public IReadOnlyList<Sample[]> Segments(double now, int pixelColumns = 300)
    {
        if (pixelColumns <= 0) throw new ArgumentOutOfRangeException(nameof(pixelColumns));
        if (!ValidTime(now) || _count == 0) return Array.Empty<Sample[]>();
        var result = new List<Sample[]>();
        var points = new List<Sample>();
        long previousSegment = _buckets[_head].Segment;
        double start = now - WindowSeconds;
        for (int offset = 0; offset < _count; offset++)
        {
            Bucket bucket = _buckets[Index(offset)];
            if (bucket.Segment != previousSegment)
            {
                if (points.Count > 0) { result.Add(Reduce(points, start, pixelColumns)); points.Clear(); }
                previousSegment = bucket.Segment;
            }
            AppendRepresentatives(points, bucket.First, bucket.Minimum, bucket.Maximum, bucket.Last, start, now);
        }
        if (points.Count > 0) result.Add(Reduce(points, start, pixelColumns));
        return result;
    }

    public double Maximum(double now)
    {
        if (!ValidTime(now)) return 0;
        double maximum = 0, start = now - WindowSeconds;
        for (int offset = 0; offset < _count; offset++)
        {
            Bucket bucket = _buckets[Index(offset)];
            Include(bucket.First); Include(bucket.Minimum); Include(bucket.Maximum); Include(bucket.Last);
        }
        return maximum;
        void Include(Sample sample)
        {
            if (sample.Time >= start && sample.Time <= now && sample.Value > maximum) maximum = sample.Value.Value;
        }
    }

    public void Clear()
    {
        Array.Clear(_buckets);
        _head = _count = 0;
        _segment = 0;
        _lastInputTime = double.NegativeInfinity;
        _hasLastFrame = false;
    }

    public static double NiceMaximum(double maximum)
    {
        if (!double.IsFinite(maximum) || maximum <= 0) return 20;
        double desired = Math.Max(10, Math.Min(maximum, MaximumFrameMilliseconds) * 1.1);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(desired)));
        double normalized = desired / magnitude;
        return (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
    }

    private int Index(int offset) => (_head + offset) % Capacity;
    private static bool ValidTime(double time) => double.IsFinite(time) && double.IsFinite(time / BucketSeconds);
    private void Trim(double now)
    {
        while (_count > 0 && _buckets[_head].Last.Time < now - WindowSeconds)
        { _buckets[_head] = default; _head = (_head + 1) % Capacity; _count--; }
    }

    private static Sample[] Reduce(List<Sample> samples, double start, int columns)
    {
        var result = new List<Sample>(Math.Min(samples.Count, 4 * Math.Min(columns, Capacity)));
        int previousColumn = -1;
        Sample first = default, minimum = default, maximum = default, last = default;
        foreach (Sample sample in samples)
        {
            int column = (int)Math.Clamp(Math.Floor((sample.Time - start) / WindowSeconds * columns), 0, columns - 1);
            if (column != previousColumn)
            {
                if (previousColumn >= 0) AppendRepresentatives(result, first, minimum, maximum, last);
                previousColumn = column;
                first = minimum = maximum = last = sample;
                continue;
            }
            last = sample;
            if (sample.Value < minimum.Value) minimum = sample;
            if (sample.Value > maximum.Value) maximum = sample;
        }
        if (previousColumn >= 0) AppendRepresentatives(result, first, minimum, maximum, last);
        return result.ToArray();
    }

    private static void AppendRepresentatives(List<Sample> output, Sample first, Sample minimum, Sample maximum,
        Sample last, double start = double.NegativeInfinity, double end = double.PositiveInfinity)
    {
        Span<Sample> ordered = stackalloc Sample[] { first, minimum, maximum, last };
        for (int index = 1; index < ordered.Length; index++)
        {
            Sample sample = ordered[index];
            int destination = index;
            while (destination > 0 && ordered[destination - 1].Time > sample.Time)
            { ordered[destination] = ordered[destination - 1]; destination--; }
            ordered[destination] = sample;
        }
        foreach (Sample sample in ordered)
            if (sample.Time >= start && sample.Time <= end && (output.Count == 0 || output[^1].Time != sample.Time)) output.Add(sample);
    }

    private struct Bucket(double key, long segment, Sample sample)
    {
        public double Key = key;
        public long Segment = segment;
        public Sample First = sample, Minimum = sample, Maximum = sample, Last = sample;
        public void Add(Sample next)
        {
            Last = next;
            if (next.Value < Minimum.Value) Minimum = next;
            if (next.Value > Maximum.Value) Maximum = next;
        }
    }
}
