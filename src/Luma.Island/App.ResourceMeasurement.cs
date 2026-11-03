using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Luma.Island.Services;

namespace Luma.Island;

public partial class App
{
    private sealed record ResourceProcess(int Pid, string Name, bool IsApp, string Source, DateTimeOffset? StartUtc,
        bool ExistingAtStart, bool BaselineOnly, double IntervalSeconds, double? CpuMilliseconds, double? CpuPercentOfMachine,
        long? WorkingSetBytes, long? PrivateBytes);
    private sealed record ResourceSample(DateTimeOffset Utc, string Scenario, string Phase, bool WindowVisible,
        IReadOnlyList<ResourceProcess> Processes, IReadOnlyList<string> Errors, FpsSnapshot? Fps);
    private sealed record ResourcePrevious(double CpuMilliseconds, double Seconds);
    private static readonly string[] ResourceHelperNames = { "PresentMon_x64", "FvContainer", "FvContainer.System", "NVFVSdkSvc_x64" };

    private async Task MeasureResourcesAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var samples = new List<ResourceSample>();
        var previous = new Dictionary<string, ResourcePrevious>();
        var initialHelperPids = new HashSet<int>();
        var watch = Stopwatch.StartNew();
        var notes = new List<string>();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        var initial = await ReadAsync("initial", "baseline");
        initialHelperPids.UnionWith(initial.Processes.Where(p => !p.IsApp).Select(p => p.Pid));
        samples[0] = initial with { Processes = initial.Processes.Select(p => p with { ExistingAtStart = !p.IsApp }).ToArray() };
        _island!.WindowState = WindowState.Normal;
        _island.Show();
        _island.SetAppearance(IslandAppearance.Glass, .76);
        if (_island.Height > _island.CollapsedWindowHeight + 1) _island.ToggleExpanded();
        await _island.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));

        try
        {
            foreach (var scenario in new (string Name, bool On, bool Charts, int Warmup)[]
                { ("off-before", false, false, 4), ("counters-on", true, false, 6), ("charts-expanded", true, true, 4), ("off-after", false, false, 4) })
            {
                _island.SetComponents(ComponentSettings.Default with
                    { Cpu = scenario.On, Gpu = scenario.On, Memory = scenario.On, Fps = scenario.On, Charts = scenario.Charts });
                bool expanded = (_island.FindName("ExpandedView") as FrameworkElement)?.Visibility == Visibility.Visible;
                if (expanded != scenario.Charts) _island.ToggleExpanded();
                await _island.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));
                if (!scenario.On)
                {
                    try { await _fps!.Completion.WaitAsync(TimeSpan.FromSeconds(15)); }
                    catch (Exception ex) { notes.Add(scenario.Name + ": FPS cleanup: " + ex.Message); }
                    if (scenario.Name is "off-before" or "off-after")
                    {
                        for (int grace = 0; grace <= 10; grace++)
                        {
                            var cleanup = await ReadAsync(scenario.Name, "helper-grace");
                            if (!cleanup.Processes.Any(p => !p.IsApp && !initialHelperPids.Contains(p.Pid))) break;
                            if (grace == 10) { notes.Add("Newly observed NVIDIA companions remained after the 10-second grace; no vendor services were stopped."); break; }
                            await Task.Delay(1000);
                        }
                    }
                }
                await ReadAsync(scenario.Name, "warmup-baseline");
                for (int second = 0; second < scenario.Warmup; second++) { await Task.Delay(1000); await ReadAsync(scenario.Name, "warmup"); }
                for (int second = 0; second < 20; second++) { await Task.Delay(1000); await ReadAsync(scenario.Name, "measure"); }
                SavePreview(_island.CapturePreview(), Path.Combine(directory, scenario.Name + ".png"));
                await WriteAsync(); // Preserve completed scenarios if a later phase is interrupted.
            }
        }
        finally
        {
            _island.SetComponents(ComponentSettings.Default);
            try { await _fps!.Completion.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception ex) { notes.Add("Final FPS cleanup: " + ex.Message); }
            await WriteAsync();
        }

        async Task<ResourceSample> ReadAsync(string scenario, string phase)
        {
            var read = await Task.Run(() =>
            {
                var rows = new List<ResourceProcess>();
                var errors = new List<string>();
                var processes = new List<Process> { Process.GetCurrentProcess() };
                foreach (string name in ResourceHelperNames)
                {
                    try { processes.AddRange(Process.GetProcessesByName(name)); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { errors.Add(name + ": " + ex.Message); }
                }
                foreach (var process in processes)
                {
                    using (process)
                    {
                        int pid = 0;
                        string name = "unavailable";
                        try
                        {
                            pid = process.Id;
                            name = process.ProcessName;
                            process.Refresh();
                            DateTimeOffset? start = null;
                            try { start = process.StartTime.ToUniversalTime(); }
                            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { errors.Add($"{name}/{pid} start time: {ex.Message}"); }
                            string identity = pid + ":" + (start?.UtcTicks.ToString() ?? "unknown");
                            double now = watch.Elapsed.TotalSeconds;
                            double? cpu = ReadValue(() => process.TotalProcessorTime.TotalMilliseconds, "CPU");
                            bool baseline = cpu.HasValue && !previous.ContainsKey(identity);
                            double interval = 0;
                            double? delta = cpu.HasValue ? 0 : null, percent = cpu.HasValue ? 0 : null;
                            if (cpu.HasValue)
                            {
                                if (previous.TryGetValue(identity, out var last))
                                {
                                    interval = now - last.Seconds;
                                    delta = cpu.Value >= last.CpuMilliseconds ? cpu.Value - last.CpuMilliseconds : null;
                                    percent = interval > 0 && delta.HasValue ? delta.Value / (interval * 1000 * Environment.ProcessorCount) * 100 : null;
                                }
                                previous[identity] = new ResourcePrevious(cpu.Value, now);
                            }
                            bool own = pid == Environment.ProcessId;
                            rows.Add(new ResourceProcess(pid, name, own, own ? "Luma Island" : "Observed NVIDIA FrameView/PresentMon companion name",
                                start, initialHelperPids.Contains(pid), baseline, interval, delta, percent,
                                ReadValue(() => process.WorkingSet64, "working set"), ReadValue(() => process.PrivateMemorySize64, "private bytes")));
                            T? ReadValue<T>(Func<T> read, string metric) where T : struct
                            {
                                try { return read(); }
                                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                                { errors.Add($"{name}/{pid} {metric}: {ex.GetType().Name}: {ex.Message}"); return null; }
                            }
                        }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                        {
                            errors.Add($"Process {pid}: {ex.GetType().Name}: {ex.Message}");
                            rows.Add(new ResourceProcess(pid, name, pid == Environment.ProcessId, "Process read unavailable", null,
                                initialHelperPids.Contains(pid), false, 0, null, null, null, null));
                        }
                    }
                }
                return (rows, errors);
            });
            var sample = new ResourceSample(DateTimeOffset.UtcNow, scenario, phase, _island?.IsVisible == true, read.rows, read.errors, _fps?.Current);
            samples.Add(sample);
            return sample;
        }

        async Task WriteAsync()
        {
            var measured = samples.Where(s => s.Phase == "measure").ToArray();
            var summaries = measured.GroupBy(s => s.Scenario).Select(group =>
            {
                var points = group.ToArray();
                var complete = points.Where(s => s.Errors.Count == 0 && s.Processes.Any(p => p.IsApp)).ToArray();
                var validFps = points.Select(s => s.Fps?.Fps).Where(v => v.HasValue && double.IsFinite(v.Value) && v.Value >= 0).Select(v => v!.Value).ToArray();
                return new
                {
                    scenario = group.Key, samples = points.Length, completeSamples = complete.Length, missedReadSamples = points.Count(p => p.Errors.Count != 0),
                    app = ResourceStats(points, p => p.IsApp, true), helpers = ResourceStats(points, p => !p.IsApp, false), combined = ResourceStats(points, _ => true, true),
                    fps = new { validSamples = validFps.Length, minimum = validFps.Length == 0 ? (double?)null : validFps.Min(), maximum = validFps.Length == 0 ? (double?)null : validFps.Max(),
                        processes = points.Select(s => s.Fps?.ProcessName).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToArray(), statuses = points.Select(s => s.Fps?.Status).Distinct().ToArray() }
                };
            }).ToArray();
            var ledger = samples.SelectMany(s => s.Processes.Select(p => new { sample = s, process = p }))
                .GroupBy(p => p.process.Pid + ":" + p.process.StartUtc?.UtcTicks).Select(g => new
                {
                    g.First().process.Pid, g.First().process.Name, g.First().process.Source, g.First().process.StartUtc,
                    existingAtStart = initialHelperPids.Contains(g.First().process.Pid), firstObservedUtc = g.First().sample.Utc, lastObservedUtc = g.Last().sample.Utc,
                    scenarios = g.Select(p => p.sample.Scenario).Distinct().ToArray()
                }).ToArray();
            var report = new
            {
                schemaVersion = 1, startedUtc, finishedUtc = DateTimeOffset.UtcNow, logicalProcessors = Environment.ProcessorCount,
                sampleIntervalSeconds = 1, measurementSecondsPerScenario = 20, initialHelperPids = initialHelperPids.Order().ToArray(),
                configuration = new { appearance = "Glass", opacity = .76, window = "collapsed except charts-expanded", baseline = ComponentSettings.Default,
                    enabled = ComponentSettings.Default with { Cpu = true, Gpu = true, Memory = true, Fps = true } },
                baselineAttributionCondition = initialHelperPids.Count == 0 ? "No matching companions were observed at entry." : "Matching companions pre-existed. Their baseline is measured too; exclusive ownership and causation are not established.",
                unknownCpuProcesses = samples.SelectMany(s => s.Processes).Where(p => !p.CpuPercentOfMachine.HasValue).Select(p => new { p.Pid, p.Name, p.StartUtc }).Distinct().ToArray(),
                notes = notes.Concat(new[] { "CPU is processor-time delta divided by observed wall time and total logical processors.",
                    "A newly observed process gets a zero-CPU baseline; CPU before first observation is not attributed to this interval.",
                    "Companion names are observed, not proof of exclusive ownership. Existing companions are included in every phase; no vendor process is killed.",
                    "Unknown process metrics are null, not zero. Known-only summary values are lower bounds when reads are unavailable; errors and all warmup/grace samples are retained.",
                    "Working-set sums include shared pages in each process and are not unique physical memory.",
                    "CPU and RAM only: no per-process GPU-overhead attribution. Results include the same diagnostic sampler in every phase." }).ToArray(),
                processes = ledger, summaries, samples
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "resource-performance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
    }

    private static object ResourceStats(ResourceSample[] points, Func<ResourceProcess, bool> include, bool requireApp)
    {
        var values = points.Select(s => { var rows = s.Processes.Where(include).ToArray(); return new
        {
            cpu = rows.Sum(p => p.CpuPercentOfMachine ?? 0), working = rows.Sum(p => p.WorkingSetBytes ?? 0) / 1048576d,
            committed = rows.Sum(p => p.PrivateBytes ?? 0) / 1048576d, missingApp = requireApp && !rows.Any(p => p.IsApp),
            cpuUnknown = rows.Any(p => !p.CpuPercentOfMachine.HasValue), ramUnknown = rows.Any(p => !p.WorkingSetBytes.HasValue || !p.PrivateBytes.HasValue), readErrors = s.Errors.Count != 0
        }; }).ToArray();
        bool cpuComplete = values.Length != 0 && values.All(v => !v.missingApp && !v.cpuUnknown);
        bool ramComplete = values.Length != 0 && values.All(v => !v.missingApp && !v.ramUnknown);
        return new { cpuComplete, ramComplete, hasProcessReadErrors = values.Any(v => v.readErrors),
            knownCpuIsLowerBound = !cpuComplete, knownRamIsLowerBound = !ramComplete,
            cpuPercentAverage = cpuComplete ? values.Average(v => v.cpu) : (double?)null, cpuPercentPeak = cpuComplete ? values.Max(v => v.cpu) : (double?)null,
            knownCpuPercentAverage = values.Length == 0 ? (double?)null : values.Average(v => v.cpu), knownCpuPercentPeak = values.Length == 0 ? (double?)null : values.Max(v => v.cpu),
            workingSetMiBAverage = ramComplete ? values.Average(v => v.working) : (double?)null, workingSetMiBPeak = ramComplete ? values.Max(v => v.working) : (double?)null,
            privateMiBAverage = ramComplete ? values.Average(v => v.committed) : (double?)null, privateMiBPeak = ramComplete ? values.Max(v => v.committed) : (double?)null,
            knownWorkingSetMiBAverage = values.Length == 0 ? (double?)null : values.Average(v => v.working), knownPrivateMiBAverage = values.Length == 0 ? (double?)null : values.Average(v => v.committed) };
    }
}
