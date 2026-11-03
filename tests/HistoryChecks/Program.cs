using System;
using Luma.Island.Services;

internal static class Program
{
    private static int _checks;
    private static int Main()
    {
        try
        {
            var history = new MetricHistory();
            for (int second = 0; second < 1000; second++) history.Add(second, second);
            Check(history.Count == 60, "Capacity remains bounded at sixty with one-second events");
            Check(history.Segments(999).Count == 1, "Regular samples form one continuous segment");
            Check(history.Segments(999)[0].Length == 60, "One-second events are coalesced into two-second buckets");
            Check(history.Segments(1120).Count == 0, "History outside the two-minute window disappears");
            history.Clear();
            history.Add(0, 20); history.Add(2, null); history.Add(4, 30);
            Check(history.Segments(4).Count == 2, "Missing data creates a gap rather than a false zero");
            history.Clear();
            history.Add(0, 20); history.Add(1, null); history.Add(1.5, 30); history.Add(2, 40);
            Check(history.Segments(2).Count == 1 && history.Segments(2)[0].Length == 1,
                "An unknown reading survives a later value in the same bucket");
            history.Clear();
            history.Add(0, 20); history.Add(10, 30);
            Check(history.Segments(10).Count == 2, "Long collection pauses cannot be bridged");
            history.Clear();
            history.Add(0, double.NaN); history.Add(2, double.PositiveInfinity); history.Add(4, -1);
            Check(history.Segments(4).Count == 0, "Invalid measurements stay unavailable");
            history.Add(6, 0);
            Check(history.Segments(6).Count == 1 && history.Maximum(6) == 0, "A genuine zero is retained");
            history.Add(2, 50);
            Check(history.Count == 1 && history.Maximum(2) == 50, "A backwards time reset starts a new history");
            Check(MetricHistory.NiceFpsMaximum(340) == 500, "High FPS receives a readable ceiling");
            Check(MetricHistory.NiceFpsMaximum(60) == 100, "FPS ceiling retains headroom");
            Check(MetricHistory.NiceFpsMaximum(double.NaN) == 60, "Missing FPS has a safe scale");
            history.Clear();
            Check(history.Count == 0 && history.Segments(6).Count == 0, "Clear discards all stored history");
            Console.WriteLine($"PASS: {_checks} history checks.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }

    private static void Check(bool passed, string description)
    {
        _checks++;
        if (!passed) throw new InvalidOperationException("FAIL: " + description);
    }
}
