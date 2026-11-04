using System;
using System.Collections.Generic;
using System.IO;
using Luma.Island.Services;

internal static class Program
{
    private static int _checks;
    private static int Main()
    {
        try
        {
            Equal(80, PerformanceService.CalculateCpu(0, 0, 0, 20, 60, 40), "CPU kernel already includes idle");
            Equal(0, PerformanceService.CalculateCpu(10, 20, 10, 110, 120, 10), "CPU fully idle delta");
            Equal(100, PerformanceService.CalculateCpu(10, 20, 10, 10, 120, 110), "CPU fully busy delta");
            Missing(PerformanceService.CalculateCpu(10, 20, 30, 10, 20, 30), "CPU zero interval");
            Missing(PerformanceService.CalculateCpu(10, 20, 30, 9, 30, 40), "CPU idle rollback");
            Missing(PerformanceService.CalculateCpu(10, 20, 30, 11, 19, 40), "CPU kernel rollback");
            Missing(PerformanceService.CalculateCpu(10, 20, 30, 11, 30, 29), "CPU user rollback");
            Missing(PerformanceService.CalculateCpu(0, 0, 0, 101, 60, 40), "CPU idle exceeds total");
            Equal(50, PerformanceService.CalculateCpu(0, 0, 0, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue), "CPU sum exceeds ulong safely");
            Equal(50, PerformanceService.CalculateCpu(ulong.MaxValue - 10, ulong.MaxValue - 10, ulong.MaxValue - 10, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue), "CPU near-max counters retain deltas");

            var memory = PerformanceService.CalculateMemory(1000, 250);
            Equal(75, memory.Percent, "RAM physical use percentage");
            Check(memory.Used == 750 && memory.Total == 1000, "RAM used and total bytes");
            Equal(0, PerformanceService.CalculateMemory(1000, 1000).Percent, "RAM unused");
            Equal(100, PerformanceService.CalculateMemory(1000, 0).Percent, "RAM fully used");
            Missing(PerformanceService.CalculateMemory(0, 0).Percent, "RAM missing total");
            Missing(PerformanceService.CalculateMemory(100, 101).Percent, "RAM available exceeds total");
            Equal(100, PerformanceService.CalculateMemory(ulong.MaxValue, 0).Percent, "RAM near-max byte count");

            var firstAdapter = new List<(string Instance, double Utilization)>
            {
                (Engine(1, "00000001", 0, 0), 30),
                (Engine(2, "00000001", 0, 0), 40),
                (Engine(1, "00000001", 0, 1), 60)
            };
            Equal(70, PerformanceService.AggregateGpu(firstAdapter), "GPU sums PIDs per engine and takes busiest engine");
            firstAdapter.Add((Engine(3, "00000002", 0, 0), 99));
            Equal(99, PerformanceService.AggregateGpu(firstAdapter), "GPU takes busiest adapter instead of summing adapters");
            Equal(90, PerformanceService.AggregateGpu(new[]
            {
                (Engine(1, "00000001", 0, 0), 70d),
                (Engine(2, "00000001", 1, 0), 90d)
            }), "GPU separates physical adapters with same LUID and engine index");
            Missing(PerformanceService.AggregateGpu(Array.Empty<(string, double)>()), "GPU no counters");
            var invalid = new[]
            {
                (Engine(1, "00000001", 0, 0), double.NaN),
                (Engine(2, "00000001", 0, 0), double.PositiveInfinity),
                (Engine(3, "00000001", 0, 0), -1d),
                ("_Total", 95d), ("", 70d), ("pid_1_luid_broken_phys_0_eng_0", 100d)
            };
            Missing(PerformanceService.AggregateGpu(invalid), "GPU invalid data remains unavailable");
            var mixed = new List<(string, double)>(invalid) { (Engine(4, "00000001", 0, 1), 12) };
            Equal(12, PerformanceService.AggregateGpu(mixed), "GPU ignores invalid samples alongside valid data");
            Equal(100, PerformanceService.AggregateGpu(new[]
            {
                (Engine(1, "00000001", 0, 0), 70d), (Engine(2, "00000001", 0, 0), 60d)
            }), "GPU engine percentage is capped at 100");
            Equal(100, PerformanceService.AggregateGpu(new[]
            {
                (Engine(1, "00000001", 0, 0), double.MaxValue), (Engine(2, "00000001", 0, 0), double.MaxValue)
            }), "GPU finite extreme inputs cannot overflow aggregate");
            VerifyFreshEqualTemperatures();
            Console.WriteLine($"PASS: {_checks} performance calculation and freshness checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static string Engine(int pid, string adapter, int physical, int engine)
        => $"pid_{pid}_luid_0x00000000_0x{adapter}_phys_{physical}_eng_{engine}_engtype_3D";

    private static void VerifyFreshEqualTemperatures()
    {
        using var service = new PerformanceService();
        var publish = typeof(PerformanceService).GetMethod("Publish", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Performance publication entry point is missing.");
        var alerts = new HudAlertEngine();
        alerts.Configure(new AlertSettings(Enabled: true, GpuTemperature: false, Stutters: false), 0);
        int sampled = 0, changed = 0;
        double now = 0;
        service.Sampled += snapshot => { sampled++; alerts.ObserveTemperatures(snapshot.CpuTemperatureCelsius, null, null, now); };
        service.Changed += _ => changed++;
        var hot = new PerformanceSnapshot(null, null, null, 0, 0, CpuTemperatureCelsius: 96);
        for (int second = 0; second <= 4; second += 2) { now = second; publish.Invoke(service, new object[] { hot }); }
        Check(sampled == 3 && changed == 1, "Equal fresh measurements notify alert observers without redrawing unchanged labels");
        Check(alerts.View(4).Active, "Constant hot temperature reaches sustained warning after four seconds");
        now = 6; publish.Invoke(service, new object[] { hot });
        Check(alerts.View(9).Active, "Equal fresh hot readings extend warning freshness");
        Check(changed == 1 && service.Current == hot, "Equal readings preserve UI deduplication and current snapshot");
        Check(!alerts.View(13).Active, "Cached value cannot keep warning alive after measurement stops");
        now = 14; publish.Invoke(service, new object[] { PerformanceSnapshot.Empty });
        Check(!alerts.View(14).Active && changed == 2, "Missing measurement resets warning and updates display once");
        now = 16; publish.Invoke(service, new object[] { PerformanceSnapshot.Empty });
        Check(sampled == 6 && changed == 2, "Repeated missing readings remain fresh events without redundant display updates");
        Check(!service.IsRunning, "Freshness regression uses no real system collector or native sampling");
    }
    private static void Equal(double expected, double? actual, string description)
        => Check(actual.HasValue && double.IsFinite(actual.Value) && Math.Abs(expected - actual.Value) < 0.000001,
            description + $" (expected {expected}, got {actual?.ToString() ?? "null"})");
    private static void Missing(double? actual, string description) => Check(actual is null, description + " (expected null)");
    private static void Check(bool passed, string description)
    {
        _checks++;
        if (!passed) throw new InvalidDataException("FAIL: " + description);
    }
}

namespace Luma.Island
{
    internal static class App
    {
        internal static void Log(Exception exception) => Console.Error.WriteLine(exception);
    }
}
