using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

internal static class Program
{
    private sealed record Phase(string Scenario, bool Measure, int TargetPid, bool Done);
    private sealed record Identity(int Pid, string Name);
    private sealed record ProcessSample(int Pid, string Name, string? PdhInstance, bool IsApp,
        bool ExistingAtStart, bool FirstObserved, double? CpuPercentOfMachine, double? WorkingSetMiB,
        double? PrivateWorkingSetMiB, double? PrivateCommitMiB, string? MetadataError);
    private sealed record Sample(DateTimeOffset Utc, double ElapsedSeconds, string Scenario, bool Measure, bool StablePhase,
        int TargetPid, bool ProcessEnumerationComplete, ProcessSample[] Processes, string[] Errors);
    private static readonly string[] HelperNames = { "PresentMon_x64", "FvContainer", "FvContainer.System", "NVFVSdkSvc_x64" };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: ResourceSampler <phase.json> <output.json>"); return 1; }
        string phasePath = Path.GetFullPath(args[0]), outputPath = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var samples = new List<Sample>();
        var enumerationErrors = new List<string>();
        var before = FindProcesses(0, enumerationErrors);
        var beforeIds = before.Select(p => p.Pid).ToHashSet();
        var previousIdentities = new HashSet<string>();
        uint activeLogical = GetActiveProcessorCount(0xFFFF);
        int logicalProcessors = activeLogical > 0 ? checked((int)activeLogical) : Environment.ProcessorCount;
        var watch = Stopwatch.StartNew();
        var startedUtc = DateTimeOffset.UtcNow;
        Phase? previousPhase = null;
        bool done = false;
        string reason = "180-second timeout";
        Console.WriteLine($"External sampler PID {Environment.ProcessId}; existing companions: {before.Length}.");
        try
        {
            using var counters = new PdhCounters();
            while (watch.Elapsed < TimeSpan.FromSeconds(180))
            {
                Phase? phase = ReadPhase(phasePath);
                if (phase is null) { await Task.Delay(1000); continue; }
                if (phase.Done) { done = true; reason = "scenario harness completed"; break; }
                if (previousPhase?.Scenario != phase.Scenario)
                {
                    if (samples.Count != 0) Save();
                    Console.WriteLine(phase.Scenario + (phase.Measure ? " measuring" : " warmup"));
                }
                var errors = new List<string>();
                var data = counters.Read(errors);
                var processErrors = new List<string>();
                var identities = FindProcesses(phase.TargetPid, processErrors);
                errors.AddRange(processErrors);
                var after = ReadPhase(phasePath);
                bool stable = previousPhase == phase && after == phase;
                var currentIdentities = new HashSet<string>();
                var rows = new List<ProcessSample>();
                foreach (var raw in identities)
                {
                    Identity identity = raw;
                    string? instance = null;
                    if (data.TryGetValue("ID Process", out var ids)) instance = ids.FirstOrDefault(p => p.Value == identity.Pid).Key;
                    string key = identity.Pid + ":" + identity.Name + ":" + instance;
                    bool first = !previousIdentities.Contains(key);
                    currentIdentities.Add(key);
                    double? Value(string counter)
                        => instance is not null && data.TryGetValue(counter, out var values) && values.TryGetValue(instance, out var value) ? value : null;
                    double? cpu = Value("% Processor Time");
                    if (first || !stable) cpu = null;
                    else if (cpu.HasValue) cpu = Math.Clamp(cpu.Value / logicalProcessors, 0, 100);
                    rows.Add(new ProcessSample(identity.Pid, identity.Name, instance, identity.Pid == phase.TargetPid,
                        beforeIds.Contains(identity.Pid), first, cpu, Value("Working Set") / 1048576d,
                        Value("Working Set - Private") / 1048576d, Value("Private Bytes") / 1048576d, null));
                }
                if (phase.TargetPid > 0 && !rows.Any(p => p.IsApp)) rows.Add(new ProcessSample(phase.TargetPid, "Luma.Island", null, true,
                    false, true, null, null, null, null, "Target PID was not available"));
                samples.Add(new Sample(DateTimeOffset.UtcNow, watch.Elapsed.TotalSeconds, phase.Scenario, phase.Measure, stable, phase.TargetPid,
                    processErrors.Count == 0, rows.ToArray(), errors.ToArray()));
                previousIdentities = currentIdentities;
                // Keep the phase observed before collection: replacing it with an after-read phase
                // could incorrectly classify the next CPU interval as entirely inside that new phase.
                previousPhase = phase;
                await Task.Delay(1000);
            }
        }
        catch (Exception exception) { reason = exception.ToString(); Console.Error.WriteLine(exception.Message); }
        finally { Save(); }
        Console.WriteLine($"Saved {samples.Count} external samples: {outputPath}");
        return done ? 0 : 2;

        void Save()
        {
            var summaries = samples.Where(s => s.Measure && s.StablePhase).GroupBy(s => s.Scenario).Select(g => new
            {
                scenario = g.Key, samples = g.Count(), firstSampleUtc = g.First().Utc, lastSampleUtc = g.Last().Utc,
                app = Summarize(g, p => p.IsApp), helpers = Summarize(g, p => !p.IsApp), combined = Summarize(g, _ => true)
            }).ToArray();
            var ledger = samples.SelectMany(s => s.Processes.Select(p => new { Sample = s, Process = p }))
                .GroupBy(v => v.Process.Pid + ":" + v.Process.Name).Select(g => new
                {
                    g.First().Process.Pid, g.First().Process.Name, g.First().Process.IsApp,
                    g.First().Process.ExistingAtStart, firstObservedUtc = g.First().Sample.Utc, lastObservedUtc = g.Last().Sample.Utc,
                    pdhInstances = g.Select(v => v.Process.PdhInstance).Where(v => v is not null).Distinct().ToArray(),
                    metadataErrors = g.Select(v => v.Process.MetadataError).Where(v => v is not null).Distinct().ToArray()
                }).ToArray();
            var report = new
            {
                schemaVersion = 1, samplerPid = Environment.ProcessId, startedUtc, updatedUtc = DateTimeOffset.UtcNow, completed = done, reason,
                logicalProcessors, source = "PDH English Process wildcard counters; external observer", helpersBefore = before,
                initialEnumerationErrors = enumerationErrors, helperNames = HelperNames, processes = ledger, summaries, samples,
                notes = new[] { "CPU is PDH Process % Processor Time divided by the machine logical processor count.",
                    "First-observed processes and phase-boundary CPU samples are null, never a whole-process-lifetime estimate. Only stable measured samples enter summaries.",
                    "Working Set is resident physical memory including shared pages; summing it can double-count shared pages.",
                    "Working Set - Private is resident private physical memory. Private Bytes is committed private virtual memory, not physical RAM.",
                    "Averages use available observations and report unknown counts. Known-only totals are lower bounds when counters are missing.",
                    "Companion names establish observation, not exclusive ownership. Existing companions are included; no process or vendor service is modified.",
                    "Sampler CPU/RAM is outside the target process and excluded from app/helper totals. No GPU-overhead attribution is made." }
            };
            File.WriteAllText(outputPath + ".tmp", JsonSerializer.Serialize(report, Json));
            File.Move(outputPath + ".tmp", outputPath, true);
        }
    }

    private static object Summarize(IEnumerable<Sample> source, Func<ProcessSample, bool> include)
    {
        var samples = source.ToArray();
        object Metric(Func<ProcessSample, double?> read)
        {
            var sums = samples.Select(s => { var values = s.Processes.Where(include).Select(read).ToArray(); return new
                { complete = values.All(v => v.HasValue) && s.ProcessEnumerationComplete, known = values.Sum(v => v ?? 0) }; }).ToArray();
            var complete = sums.Where(s => s.complete).Select(s => s.known).ToArray();
            return new { average = complete.Length == 0 ? (double?)null : complete.Average(), peak = complete.Length == 0 ? (double?)null : complete.Max(),
                validSamples = complete.Length, unknownSamples = sums.Length - complete.Length, knownOnlyAverage = sums.Length == 0 ? (double?)null : sums.Average(s => s.known),
                knownOnlyPeak = sums.Length == 0 ? (double?)null : sums.Max(s => s.known), knownOnlyIsLowerBound = complete.Length != sums.Length };
        }
        return new { cpuPercentOfMachine = Metric(p => p.CpuPercentOfMachine), workingSetMiB = Metric(p => p.WorkingSetMiB),
            privateWorkingSetMiB = Metric(p => p.PrivateWorkingSetMiB), privateCommitMiB = Metric(p => p.PrivateCommitMiB) };
    }
    private static Phase? ReadPhase(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return JsonSerializer.Deserialize<Phase>(stream, Json); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    private static Identity[] FindProcesses(int targetPid, List<string> errors)
    {
        var found = new List<Identity>();
        foreach (string name in HelperNames)
        {
            try { foreach (var process in Process.GetProcessesByName(name)) using (process) Add(process); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { errors.Add(name + ": " + ex.Message); }
        }
        if (targetPid > 0)
        {
            try { using var process = Process.GetProcessById(targetPid); Add(process); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { errors.Add("Target: " + ex.Message); }
        }
        return found.DistinctBy(p => p.Pid).ToArray();
        void Add(Process process)
        {
            int pid = process.Id;
            string name = process.ProcessName;
            found.Add(new Identity(pid, name));
        }
    }

    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort groupNumber);
}
