using System;
using System.Collections.Generic;
using System.Linq;
using Luma.Island.Services;

internal static class Program
{
    private static int _checks;
    private static int Main()
    {
        try
        {
            var history = new FrameTimeHistory();
            Check(history.Count == 0 && history.Segments(1).Count == 0 && history.Maximum(1) == 0, "Empty capture has no invented frame data");
            foreach (int fps in new[] { 60, 144, 1000, 20000 })
            {
                history.Clear();
                for (int index = 0; index <= 20 * fps; index++) history.Add(index / (double)fps, 1000d / fps);
                var output = history.Segments(20);
                Check(history.Count <= 402 && history.Count <= FrameTimeHistory.Capacity, "Twenty seconds remain bounded at " + fps + " FPS");
                Check(output.Count == 1 && output[0].All(sample => sample.Time >= 10 && sample.Time <= 20), "Only the ten-second visible window is returned at " + fps + " FPS");
                Check(output[0].Length <= 1200, "Three hundred display columns bound generated points at " + fps + " FPS");
                Check(Near(history.Maximum(20), 1000d / fps), "Compression retains real durations at " + fps + " FPS");
            }
            history.Clear();
            history.Add(1.001, 5); history.Add(1.005, 80); history.Add(1.009, 2); history.Add(1.020, 6);
            var bucket = history.Segments(2, 1).Single();
            Check(history.Count == 1 && bucket.Length == 4, "One bucket retains distinct first, peak, trough and last frames");
            Check(bucket.Select(sample => sample.Value).SequenceEqual(new double?[] { 5, 80, 2, 6 }), "Representatives stay in actual timestamp order");
            Check(Near(history.Maximum(2), 80) && bucket.Any(sample => Near(sample.Value, 80)), "One-frame spike survives one-column downsampling");
            Check(bucket.Any(sample => Near(sample.Value, 2)), "One-column downsampling retains the real trough as well as peak");
            Check(!history.Add(1.020, 300) && !history.Add(1.010, 500), "Duplicate and late frames are discarded");
            Check(history.Count == 1 && Near(history.Maximum(2), 80), "Late IPC batches cannot erase or alter current history");
            Check(!history.MarkGap(.5) && history.Segments(2).Count == 1, "A late discontinuity cannot split current data");

            history.Clear();
            history.Add(0, 5); history.Add(.5, 500);
            Check(history.Segments(.5).Count == 1 && Near(history.Maximum(.5), 500), "A genuine half-second frame remains a connected stutter spike");
            history.Clear();
            history.Add(0, 5); history.Add(.5, 5);
            Check(history.Segments(.5).Count == 2, "An unexplained capture pause creates a gap");
            history.Clear();
            history.Add(0, 5); history.Add(2, 500);
            Check(history.Segments(2).Count == 2, "A frame duration cannot explain an unrelated longer capture loss");
            history.Clear();
            history.Add(0, 5); history.Add(10, 10000);
            Check(history.Segments(10).Count == 1 && Near(history.Maximum(10), 10000), "A real ten-second stall is retained, not replaced by a collection gap");
            history.Clear();
            history.Add(0, 5); history.Add(20, 20000);
            Check(history.Segments(20).Single().Length == 1 && Near(history.Maximum(20), 20000),
                "A twenty-second freeze remains a real latest-frame spike even though its start precedes the history window");
            Check(history.Segments(19).Count == 0 && history.Segments(31).Count == 0,
                "A long freeze does not invent historical samples before its end or after normal expiry");
            history.Clear();
            history.Add(0, 5); history.Add(60, 60000);
            Check(Near(history.Maximum(60), 60000), "The supported one-minute frame limit retains a real freeze endpoint");

            foreach (double? value in new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 60000.1 })
            {
                history.Clear();
                history.Add(1.001, 5);
                Check(!history.Add(1.002, value), "Invalid frame value is rejected: " + value);
                history.Add(1.003, 6);
                var gaps = history.Segments(2);
                Check(gaps.Count == 2 && gaps.All(segment => segment.Length == 1), "Invalid data splits even within one 25 ms bucket: " + value);
                Check(gaps.SelectMany(segment => segment).All(sample => sample.Value is > 0), "Missing data is never plotted as a false zero: " + value);
            }
            history.Clear();
            history.Add(1, 5); history.MarkGap(1); history.Add(1.001, 6);
            Check(history.Segments(2).Count == 2, "An explicit discontinuity at the last frame still breaks the following line");
            history.Clear();
            for (int index = 0; index < 2000; index++)
            {
                history.Add(1 + index * .0001, 5 + index % 7);
                history.MarkGap(1 + index * .0001 + .00005);
            }
            Check(history.Count == FrameTimeHistory.Capacity, "Repeated capture-loss markers cannot grow the ring beyond capacity");
            Check(history.Segments(2).Count == FrameTimeHistory.Capacity, "Capacity pressure still keeps discontinuous captures separated");

            history.Clear();
            for (int index = 0; index < 10000; index++) history.Add(index * .001, index == 5000 ? 140 : 5 + index % 9);
            foreach (int columns in new[] { 1, 2, 11, 100, 300, 1000000 })
            {
                var output = history.Segments(10, columns).Single();
                Check(output.Any(sample => Near(sample.Value, 140)) && output.Any(sample => Near(sample.Value, 5)), "Peak/trough survive reduction to " + columns + " columns");
                Check(output.Zip(output.Skip(1), (first, next) => first.Time < next.Time).All(value => value), "Reduced timestamps stay strictly increasing at " + columns + " columns");
                Check(output.Length <= Math.Min(2048, (long)columns * 4), "Reduced points remain bounded at " + columns + " columns");
            }
            Check(history.Segments(20).Count == 0 && history.Maximum(20) == 0, "Expired frames disappear without a new incoming sample");
            Check(history.Segments(5).SelectMany(segment => segment).All(sample => sample.Time <= 5), "Future frames are excluded from an earlier query");
            int before = history.Count;
            foreach (double time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue })
            {
                Check(!history.Add(time, 30) && !history.MarkGap(time), "Invalid timestamp is ignored: " + time);
                Check(history.Segments(time).Count == 0 && history.Maximum(time) == 0 && history.Count == before, "Invalid clock queries cannot mutate valid capture history");
            }
            bool rejected = false;
            try { history.Segments(10, 0); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "A nonpositive display column count is rejected explicitly");
            history.Clear();
            Check(history.Count == 0 && history.Add(.01, 3) && Near(history.Maximum(.02), 3), "Clear starts a new stream, including a lower QPC-relative origin");
            Check(FrameTimeHistory.NiceMaximum(double.NaN) == 20 && FrameTimeHistory.NiceMaximum(0) == 20, "No-data chart has a finite readable millisecond ceiling");
            Check(FrameTimeHistory.NiceMaximum(5.4) == 10 && FrameTimeHistory.NiceMaximum(28.7) == 50, "Frametime ceilings preserve visible headroom for stutters");
            Check(FrameTimeHistory.NiceMaximum(60000) >= 60000 && double.IsFinite(FrameTimeHistory.NiceMaximum(double.MaxValue)), "Large supported spikes still have a finite nonclipping ceiling");
            Console.WriteLine($"PASS: {_checks} frame-time history checks.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static bool Near(double? first, double second) => first.HasValue && Math.Abs(first.Value - second) < .000001;
    private static void Check(bool passed, string description) { if (!passed) throw new InvalidOperationException("FAIL: " + description); _checks++; }
}
