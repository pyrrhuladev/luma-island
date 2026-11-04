using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Luma.Island.Services;

internal static class Program
{
    private static readonly List<string> Checks = new();
    private const uint NoCounter = 0xC0000BB9, InvalidData = 0xC0000BC6, NoData = 0x800007D5, InvalidHandle = 0xC0000BBC;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            CheckUtility(); CheckInvalidUtility(); CheckFallback(); CheckLifetime(); CheckMath();
            Console.WriteLine($"PASS: {Checks.Count} CPU utility reader checks.");
            if (args.Length > 0 && args[0] == "--live") await LiveProbe(args.Length > 1 ? args[1] : "cpu-reader-live.json");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void CheckUtility()
    {
        var api = new FakeApi();
        using var reader = new CpuUsageReader(api);
        Check(api.TotalCalls == 0, "Construction does not load or query native counters");
        var first = reader.Read();
        Check(first.Percent is null && first.Source.Contains("% Processor Utility") && !first.Source.Contains("GetSystemTimes"), "First utility collection is a labelled warmup, not fallback or zero");
        Check(api.OpenCalls == 1 && api.AddedPaths.SequenceEqual(new[] { @"\Processor Information(_Total)\% Processor Utility" }), "Exactly one language-independent _Total utility counter is opened");
        Check(api.FormatCalls == 0 && api.TimesCalls == 0, "Warmup neither formats a one-point interval nor reads busy time");
        api.Values.Enqueue(new(0, 0, 56));
        Equal(56, reader.Read().Percent, "Utility preserves the aggregate 56% rather than legacy busy-time 29%");
        api.Values.Enqueue(new(0, 1, 146.75));
        Equal(146.75, reader.Read().Percent, "Valid boost utility above 100% is retained, including CSTATUS_NEW_DATA");
        api.Values.Enqueue(new(0, 0, 0));
        Equal(0, reader.Read().Percent, "A valid idle utility value is displayed as zero");
        Check(api.Formats.All(value => value == 0x8200), "PDH formatting uses DOUBLE | NOCAP100 without NOSCALE or per-core normalization");
        Check(api.OpenCalls == 1 && api.AddedPaths.Count == 1 && api.TimesCalls == 0, "Healthy utility reuses its one query and never invokes the busy fallback");
    }

    private static void CheckInvalidUtility()
    {
        foreach (var bad in new[] { new Value(0, 0, double.NaN), new(0, 0, double.PositiveInfinity), new(0, 0, -0.1),
                     new(InvalidData, 0, 99), new(0, 0xC0000BBA, 99), new(0, 0x800007D1, 99) })
        {
            var api = new FakeApi();
            using var reader = new CpuUsageReader(api);
            reader.Read(); api.Values.Enqueue(bad);
            var invalid = reader.Read();
            Check(invalid.Percent is null && invalid.Source.Contains("% Processor Utility") && api.TimesCalls == 0, "Invalid/transient utility data is missing, never silently replaced by busy time: " + bad);
            int formatted = api.FormatCalls;
            Check(reader.Read().Percent is null && api.FormatCalls == formatted, "A bad utility point establishes a fresh raw baseline before formatting: " + bad);
            api.Values.Enqueue(new(0, 0, 63.5));
            Equal(63.5, reader.Read().Percent, "Utility recovers after two fresh valid samples: " + bad);
            Check(api.OpenCalls == 1 && api.CloseCalls == 0, "Invalid values do not repeatedly recreate a working counter: " + bad);
        }
        {
            var api = new FakeApi(); using var reader = new CpuUsageReader(api);
            reader.Read(); api.CollectStatuses.Enqueue(NoData);
            Check(reader.Read().Percent is null && api.TimesCalls == 0, "PDH_NO_DATA from an existing query is a temporary gap, not metric fallback");
            Check(reader.Read().Percent is null, "After an empty collection a fresh baseline is required");
            api.Values.Enqueue(new(0, 0, 45)); Equal(45, reader.Read().Percent, "An empty utility interval recovers on the same query");
        }
        {
            var api = new FakeApi(); using var reader = new CpuUsageReader(api);
            reader.Read(); api.CollectStatuses.Enqueue(InvalidHandle);
            Check(reader.Read().Percent is null && api.CloseCalls == 1 && api.TimesCalls == 0, "An invalid utility handle is closed without publishing a different metric");
            Check(reader.Read().Percent is null && api.OpenCalls == 2, "The next read reopens a broken utility handle and warms up");
            api.Values.Enqueue(new(0, 0, 48)); Equal(48, reader.Read().Percent, "Reopened utility recovers with a complete interval");
        }
    }

    private static void CheckFallback()
    {
        long now = 0;
        var api = new FakeApi { AddStatus = NoCounter };
        using var reader = new CpuUsageReader(api, () => now);
        api.Times.Enqueue(new(true, new(10, 20, 10)));
        var first = reader.Read();
        Check(first.Percent is null && first.Source.Contains("GetSystemTimes") && first.Source.Contains("резервный") && first.Source.Contains("C0000BB9"), "A genuinely unavailable counter selects an explicit, diagnostic busy-time fallback");
        Check(api.CloseCalls == 1, "A query is closed if adding its utility counter fails");
        now += 2000; api.Times.Enqueue(new(true, new(20, 60, 70)));
        Equal(90, reader.Read().Percent, "Fallback uses kernel+user deltas with idle counted once");
        Check(api.OpenCalls == 1 && api.TimesCalls == 2, "Unavailable utility is not reopened on every two-second busy sample");
        now = CpuUsageReader.RetryMilliseconds - 1;
        reader.Read(); Check(api.OpenCalls == 1, "Utility retry waits for its bounded cooldown");
        now++;
        api.AddStatus = 0;
        var retry = reader.Read();
        Check(api.OpenCalls == 2 && retry.Percent is null && retry.Source.Contains("% Processor Utility") && !retry.Source.Contains("GetSystemTimes"), "Recovered counter returns to utility with a fresh warmup rather than mixing intervals");
        api.Values.Enqueue(new(0, 0, 67)); Equal(67, reader.Read().Percent, "Primary utility replaces the labelled fallback after recovery");

        var failed = new FakeApi { OpenStatus = NoCounter };
        using var busy = new CpuUsageReader(failed, () => 0);
        failed.Times.Enqueue(new(true, new(10, 20, 10))); busy.Read();
        failed.Times.Enqueue(new(false, default)); Check(busy.Read().Percent is null, "Failed GetSystemTimes is unavailable, not zero");
        failed.Times.Enqueue(new(true, new(100, 200, 100))); Check(busy.Read().Percent is null, "Busy sampling failure discards its stale baseline");
        failed.Times.Enqueue(new(true, new(120, 260, 140))); Equal(80, busy.Read().Percent, "Busy fallback recovers after two fresh system-time snapshots");
        Check(failed.CloseCalls == 0 && failed.AddedPaths.Count == 0, "A failed query open never adds a counter or closes a null handle");

        var missingDll = new FakeApi { OpenException = new DllNotFoundException() };
        using var noPdh = new CpuUsageReader(missingDll);
        Check(noPdh.Read().Source.Contains("GetSystemTimes") && missingDll.TimesCalls == 1, "A missing native PDH API degrades to the explicitly labelled OS fallback");
    }

    private static void CheckLifetime()
    {
        var api = new FakeApi();
        var reader = new CpuUsageReader(api);
        reader.Read(); api.Values.Enqueue(new(0, 0, 61)); reader.Read();
        reader.Reset(); Check(api.CloseCalls == 1, "Disable/reset closes the utility query");
        var reset = reader.Read(); Check(reset.Percent is null && api.OpenCalls == 2, "Re-enabling starts from a fresh utility baseline");
        reader.Dispose(); Check(api.CloseCalls == 2, "Disposal closes the remaining native query");
        int calls = api.TotalCalls;
        reader.Dispose(); reader.Reset();
        Check(reader.Read().Percent is null && api.TotalCalls == calls, "Disposed readers perform no native work; repeated cleanup is safe");

        var fallback = new FakeApi { AddStatus = NoCounter };
        using var busy = new CpuUsageReader(fallback, () => 0);
        fallback.Times.Enqueue(new(true, new(0, 10, 10))); busy.Read();
        fallback.Times.Enqueue(new(true, new(10, 20, 20))); busy.Read();
        busy.Reset(); fallback.Times.Enqueue(new(true, new(100, 200, 200)));
        Check(busy.Read().Percent is null && fallback.OpenCalls == 2, "Disable/reset clears fallback intervals and utility retry cooldown");
    }

    private static void CheckMath()
    {
        Equal(80, CpuUsageReader.CalculateBusy(0, 0, 0, 20, 60, 40), "Busy math includes idle in kernel exactly once");
        Equal(0, CpuUsageReader.CalculateBusy(10, 20, 10, 110, 120, 10), "Fully idle fallback interval");
        Equal(100, CpuUsageReader.CalculateBusy(10, 20, 10, 10, 120, 110), "Fully busy fallback interval");
        Check(CpuUsageReader.CalculateBusy(10, 20, 30, 10, 20, 30) is null, "A zero fallback interval is missing");
        Check(CpuUsageReader.CalculateBusy(10, 20, 30, 9, 30, 40) is null, "Idle rollback cannot become a CPU percentage");
        Check(CpuUsageReader.CalculateBusy(10, 20, 30, 11, 19, 40) is null, "Kernel rollback cannot become a CPU percentage");
        Check(CpuUsageReader.CalculateBusy(10, 20, 30, 11, 30, 29) is null, "User rollback cannot become a CPU percentage");
        Check(CpuUsageReader.CalculateBusy(0, 0, 0, 101, 60, 40) is null, "Idle exceeding total fallback time is rejected");
        Equal(50, CpuUsageReader.CalculateBusy(0, 0, 0, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue), "Fallback accumulation avoids unsigned overflow");
        Equal(50, CpuUsageReader.CalculateBusy(ulong.MaxValue - 10, ulong.MaxValue - 10, ulong.MaxValue - 10, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue), "Large absolute counters preserve exact small deltas");
    }

    private static async Task LiveProbe(string path)
    {
        var samples = new List<object>();
        using var reader = new CpuUsageReader();
        for (int index = 0; index < 5; index++)
        {
            var value = await Task.Run(reader.Read);
            samples.Add(new { index, value.Percent, value.Source });
            if (index < 4) await Task.Delay(2000);
        }
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(new { samples, checks = Checks.Count,
            isolation = "Read-only utility query on a worker, no synthetic CPU load or application/service/driver changes." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Read-only CPU utility probe completed: " + fullPath);
    }

    private readonly record struct Value(uint Status, uint DataStatus, double Percent);
    private readonly record struct Times(bool Success, CpuUsageReader.SystemTimes Value);
    private sealed class FakeApi : CpuUsageReader.ICpuUsageApi
    {
        public uint OpenStatus, AddStatus;
        public Exception? OpenException;
        public readonly Queue<uint> CollectStatuses = new();
        public readonly Queue<Value> Values = new();
        public readonly Queue<Times> Times = new();
        public readonly List<string> AddedPaths = new();
        public readonly List<uint> Formats = new();
        private readonly HashSet<IntPtr> _open = new();
        private int _next;
        public int OpenCalls, CollectCalls, FormatCalls, TimesCalls, CloseCalls;
        public int TotalCalls => OpenCalls + AddedPaths.Count + CollectCalls + FormatCalls + TimesCalls + CloseCalls;
        public uint OpenQuery(out IntPtr query)
        {
            OpenCalls++; query = IntPtr.Zero;
            if (OpenException is not null) throw OpenException;
            if (OpenStatus != 0) return OpenStatus;
            query = new IntPtr(++_next); _open.Add(query); return 0;
        }
        public uint AddEnglishCounter(IntPtr query, string path, out IntPtr counter)
        { RequireOpen(query); AddedPaths.Add(path); counter = AddStatus == 0 ? new IntPtr(query.ToInt64() + 100) : IntPtr.Zero; return AddStatus; }
        public uint Collect(IntPtr query) { RequireOpen(query); CollectCalls++; return CollectStatuses.TryDequeue(out uint value) ? value : 0; }
        public uint GetFormattedValue(IntPtr counter, uint format, out uint dataStatus, out double value)
        {
            RequireOpen(new IntPtr(counter.ToInt64() - 100)); FormatCalls++; Formats.Add(format);
            var next = Values.TryDequeue(out var item) ? item : new Value(0, 0, 50);
            dataStatus = next.DataStatus; value = next.Percent; return next.Status;
        }
        public uint CloseQuery(IntPtr query) { RequireOpen(query); _open.Remove(query); CloseCalls++; return 0; }
        public bool TryGetSystemTimes(out CpuUsageReader.SystemTimes times)
        { TimesCalls++; var sample = Times.TryDequeue(out var value) ? value : new Times(true, new(20, 40, 40)); times = sample.Value; return sample.Success; }
        private void RequireOpen(IntPtr query) { if (!_open.Contains(query)) throw new InvalidDataException("Used a closed or unknown query"); }
    }
    private static void Equal(double expected, double? actual, string description) => Check(actual.HasValue && Math.Abs(expected - actual.Value) < 0.000001, description);
    private static void Check(bool condition, string description) { if (!condition) throw new InvalidDataException("FAIL: " + description); Checks.Add(description); }
}
