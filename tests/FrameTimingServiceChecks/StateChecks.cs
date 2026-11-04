using System;
using System.Linq;
using Luma.Island.Services;

internal static partial class Program
{
    private static void StateChecks()
    {
        var fake = new FakeSource(new Harness());
        var state = new FrameTimingState(1000, "session-a");
        Check(state.SetTarget(42, "Game A"), "First target establishes an isolated PID context");
        state.ObserveStatistics(new(new[] { Metric(10000, 42, 7, 240), Metric(10000, 99, 7, 800) },
            new[] { Metric(10000, 42, 7, 91), Metric(10000, 99, 7, 600) }), 10);
        FpsSnapshot snapshot = state.TakeSnapshot(true, true, true, fake, 10);
        Check(snapshot.Fps == 240 && snapshot.OnePercentLow == 91 && snapshot.FrameTimeMilliseconds is null,
            "Average and native Low remain independent, selected from the requested PID only");
        Check(snapshot.StreamId == "session-a/42/7" && snapshot.HasFrameGap, "Initial stream identity carries session, PID and swapchain");
        Check(snapshot.Frames is null && state.TakeSnapshot(true, true, true, fake, 10).HasFrameGap == false,
            "Taking a snapshot drains one-shot gap state without inventing frames");

        state.ObserveFrames(new(new[] { Frame(10003, 42, 7, 40), Frame(10001, 42, 7, 5), Frame(10002, 99, 7, 300),
            Frame(10002, 42, 7, 6), Frame(10002, 42, 7, 999) }, false), 10.01);
        snapshot = state.TakeSnapshot(true, true, true, fake, 10.01);
        Check(snapshot.Frames!.Select(frame => frame.TimeSeconds).SequenceEqual(new[] { 10.001, 10.002, 10.003 }) &&
            snapshot.Frames!.Select(frame => frame.Milliseconds).SequenceEqual(new[] { 5d, 6d, 40d }),
            "Raw frames are PID-filtered, timestamp-sorted and deduplicated without averaging");
        Check(snapshot.FrameTimeMilliseconds == 40 && snapshot.FrameTimeMilliseconds != 1000d / snapshot.Fps,
            "The latest real frame interval is independent of the average FPS statistic");
        Check(state.TakeSnapshot(true, true, true, fake, 10.01).Frames is null,
            "Draining snapshots never repeats already delivered raw frames");
        state.ObserveFrames(new(new[] { Frame(10002, 42, 7, 500) }, false), 10.02);
        Check(state.TakeSnapshot(true, true, true, fake, 10.02).FrameTimeMilliseconds == 40,
            "Late old frames cannot overwrite the newest interval");

        string firstStream = snapshot.StreamId!;
        Check(state.SetTarget(43, "Game B"), "Changing the target explicitly clears stream state");
        snapshot = state.TakeSnapshot(true, true, true, fake, 10.03);
        Check(snapshot.Fps is null && snapshot.OnePercentLow is null && snapshot.Frames is null && snapshot.FrameTimeMilliseconds is null &&
            snapshot.StreamId != firstStream && snapshot.HasFrameGap, "A new PID cannot inherit previous values or pending raw frames");
        state.ObserveFrames(new(new[] { Frame(10030, 43, 20, 12) }, false), 10.03);
        snapshot = state.TakeSnapshot(false, false, true, fake, 10.03);
        Check(snapshot.StreamId == "session-a/43/14" && snapshot.FrameTimeMilliseconds == 12 && snapshot.Fps is null && snapshot.OnePercentLow is null,
            "Frames-only capture establishes its own hexadecimal swapchain identity");
        Check(!state.SetTarget(43, "Renamed B") && state.TakeSnapshot(false, false, true, fake, 10.03).ProcessName == "Renamed B",
            "Updating a name does not reset a still-identical PID stream");

        state.ObserveFrames(new(new[] { Frame(10040, 43, 21, 400) }, false), 10.04);
        Check(state.TakeSnapshot(false, false, true, fake, 10.04).FrameTimeMilliseconds == 12,
            "A fresh preferred swapchain is not contaminated by a stray secondary chain");
        state.ObserveStatistics(new(new[] { Metric(12000, 43, 21, 150), Metric(11999, 43, 20, 999) }, new[] { Metric(12000, 43, 21, 80) }), 12);
        snapshot = state.TakeSnapshot(true, true, true, fake, 12);
        Check(snapshot.StreamId == "session-a/43/14" && snapshot.Fps == 999 && snapshot.OnePercentLow is null,
            "A newer secondary-chain statistic does not oscillate away from a still-fresh preferred chain");
        state.ObserveStatistics(new(new[] { Metric(16000, 43, 21, 150) }, new[] { Metric(16000, 43, 21, 80) }), 16);
        snapshot = state.TakeSnapshot(true, true, true, fake, 16);
        Check(snapshot.StreamId == "session-a/43/15" && snapshot.Fps == 150 && snapshot.OnePercentLow == 80 &&
            snapshot.FrameTimeMilliseconds is null && snapshot.HasFrameGap, "A newer chain replaces stale frames and resets frame history identity");
        state.ObserveStatistics(new(new[] { Metric(16001, 43, 21, 155) }, new[] { Metric(16001, 43, 20, 999) }), 16.001);
        Check(state.TakeSnapshot(true, true, false, fake, 16.001).OnePercentLow == 80,
            "A different nonzero swapchain cannot supply Low for the current stream");
        state.ObserveStatistics(new(Array.Empty<FrameViewMetric>(), new[] { Metric(16002, 43, 0, 77) }), 16.002);
        snapshot = state.TakeSnapshot(true, true, false, fake, 16.002);
        Check(snapshot.OnePercentLow == 77 && snapshot.Fps == 155 && snapshot.StreamId == "session-a/43/15",
            "An SDK aggregate Low is allowed without replacing a real nonzero swapchain or clearing current FPS");

        snapshot = state.TakeSnapshot(true, true, true, fake, 19.1);
        Check(snapshot.Fps is null && snapshot.OnePercentLow is null && snapshot.FrameTimeMilliseconds is null,
            "Stale cached metrics become unavailable rather than staying frozen forever");
        var handoff = new FrameTimingState(1000, "handoff"); handoff.SetTarget(42, "Game");
        handoff.ObserveStatistics(new(new[] { Metric(10000, 42, 7, 100) }, new[] { Metric(10000, 42, 0, 50) }), 10);
        handoff.TakeSnapshot(true, true, false, fake, 10);
        handoff.ObserveStatistics(new(new[] { Metric(10100, 42, 8, 150) }, new[] { Metric(10100, 42, 0, 60) }), 10.1);
        var replacement = handoff.TakeSnapshot(true, true, false, fake, 10.1);
        Check(replacement.StreamId == "handoff/42/8" && replacement.Fps == 150 && replacement.OnePercentLow == 60,
            "A newly supplied average chain takes over immediately when the former chain disappears from the current statistic set");
        handoff.ObserveStatistics(new(Array.Empty<FrameViewMetric>(), Array.Empty<FrameViewMetric>()), 10.2);
        Check(handoff.TakeSnapshot(true, true, false, fake, 10.2).Fps == 150,
            "A merely empty statistic poll retains a still-fresh cached stream");
        foreach (double invalid in new[] { -1d, 0, 20000.1, double.NaN, double.PositiveInfinity })
        {
            var rejected = new FrameTimingState(1000, "invalid"); rejected.SetTarget(42, "Game");
            rejected.ObserveStatistics(new(new[] { Metric(10000, 42, 7, invalid) }, new[] { Metric(10000, 42, 7, invalid) }), 10);
            var result = rejected.TakeSnapshot(true, true, false, fake, 10);
            Check(result.Fps is null && result.OnePercentLow is null, "Invalid FPS/Low is unavailable: " + invalid);
        }
        var times = new FrameTimingState(1000, "times"); times.SetTarget(42, "Game");
        times.ObserveStatistics(new(new[] { Metric(0, 42, 7, 100), Metric(6999, 42, 7, 100), Metric(10251, 42, 7, 100) },
            new[] { Metric(0, 42, 7, 50), Metric(6000, 42, 7, 50), Metric(10251, 42, 7, 50) }), 10);
        Check(times.TakeSnapshot(true, true, false, fake, 10).OnePercentLow is null, "Zero, stale and excessively future Low timestamps are rejected");
        // The zero/future values must not create a stream; a fresh ordinary value is accepted normally.
        times.ObserveStatistics(new(new[] { Metric(10000, 42, 7, 100) }, Array.Empty<FrameViewMetric>()), 10);
        Check(times.TakeSnapshot(true, true, false, fake, 10).Fps == 100 && times.TakeSnapshot(true, true, false, fake, 10).OnePercentLow is null,
            "Missing native Low is never synthesized from a valid average");

        foreach (double invalid in new[] { -1d, 0, 60000.1, double.NaN, double.PositiveInfinity })
        {
            var rejected = new FrameTimingState(1000, "frames"); rejected.SetTarget(42, "Game");
            rejected.ObserveFrames(new(new[] { Frame(10000, 42, 7, invalid) }, false), 10);
            var result = rejected.TakeSnapshot(false, false, true, fake, 10);
            Check(result.FrameTimeMilliseconds is null && result.Frames is null, "Invalid actual frame duration is rejected: " + invalid);
        }
        var frameTimes = new FrameTimingState(1000, "future"); frameTimes.SetTarget(42, "Game");
        frameTimes.ObserveFrames(new(new[] { Frame(0, 42, 7, 5), Frame(6000, 42, 7, 5), Frame(10251, 42, 7, 5) }, false), 10);
        Check(frameTimes.TakeSnapshot(false, false, true, fake, 10).Frames is null, "Zero, stale and excessively future raw-frame timestamps are rejected");
        frameTimes.ObserveFrames(new(new[] { Frame(10200, 42, 7, 60000) }, false), 10);
        Check(frameTimes.TakeSnapshot(false, false, true, fake, 10).FrameTimeMilliseconds == 60000,
            "A bounded read-time tolerance and real one-minute stall are accepted without altering their duration");
        var capability = new Harness();
        var capabilitySource = new FakeSource(capability);
        var capabilityState = new FrameTimingState(1000, "capability"); capabilityState.SetTarget(42, "Game");
        capabilityState.ObserveFrames(new(new[] { Frame(10000, 42, 7, 5) }, false), 10);
        capability.FramesSupported = false;
        var unsupported = capabilityState.TakeSnapshot(false, false, true, capabilitySource, 10);
        Check(unsupported.FrameTimeMilliseconds is null && unsupported.Frames is null &&
            unsupported.FrameTimeStatus!.Contains("unsupported", StringComparison.Ordinal),
            "A lost frame capability gates both the latest value and pending raw-frame payload");

        var gapState = new FrameTimingState(1000, "gap"); gapState.SetTarget(42, "Game");
        gapState.ObserveFrames(new(new[] { Frame(10000, 42, 7, 5), Frame(10001, 42, 7, 40) }, false), 10.01);
        gapState.TakeSnapshot(false, false, true, fake, 10.01);
        gapState.ObserveFrames(new(Array.Empty<FrameViewFrame>(), false), 10.02);
        Check(!gapState.TakeSnapshot(false, false, true, fake, 10.02).HasFrameGap,
            "A merely empty poll is not a fabricated capture discontinuity");
        gapState.ObserveFrames(new(new[] { Frame(10003, 42, 7, 7) }, true), 10.03);
        snapshot = gapState.TakeSnapshot(false, false, true, fake, 10.03);
        Check(snapshot.HasFrameGap && snapshot.Frames?.Length == 1 && snapshot.Frames[0].Milliseconds == 7,
            "An actual loss marker discards pending pre-loss data and survives in the batch");
        var capped = new FrameTimingState(1_000_000, "cap"); capped.SetTarget(42, "Game");
        var many = Enumerable.Range(0, 5000).Select(index => Frame((ulong)(99_000_000 + index), 42, 7, index + 1)).ToArray();
        capped.ObserveFrames(new(many, false), 100);
        snapshot = capped.TakeSnapshot(false, false, true, fake, 100);
        Check(snapshot.Frames is { Length: <= 4096 } && snapshot.HasFrameGap && snapshot.Frames[^1].Milliseconds == 5000,
            "A saturated collector batch is bounded and explicitly marks loss while retaining its newest frame");
        capped.SetTarget(0, null);
        snapshot = capped.TakeSnapshot(true, true, true, fake, 100);
        Check(snapshot.StreamId is null && snapshot.Fps is null && snapshot.OnePercentLow is null && snapshot.FrameTimeMilliseconds is null && snapshot.Frames is null,
            "A zero/excluded target clears every metric and stream payload");
    }

    private static FrameViewMetric Metric(ulong ticks, ulong pid, ulong chain, double value) => new(ticks, pid, chain, value);
    private static FrameViewFrame Frame(ulong ticks, ulong pid, ulong chain, double value) => new(ticks, pid, chain, value, value + 12);
}
