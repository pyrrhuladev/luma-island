using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Luma.Island.Services;

internal static class Program
{
    private static int _checks;
    private static readonly GameProfile Alpha = Profile("11111111-1111-1111-1111-111111111111", "Alpha", @"C:\Games\Alpha\alpha.exe");
    private static readonly GameProfile Beta = Profile("22222222-2222-2222-2222-222222222222", "Beta", @"C:\Games\Beta\beta.exe");
    private static readonly GameProfileSettings Config = new(true, new[] { Alpha, Beta });
    private static GameProcessSnapshot Snapshot(int foreground, params GameProcessInfo[] processes) => new(true, processes, foreground);
    private static GameProcessInfo ProcessFor(GameProfile profile, int pid) => new(pid, Path.GetFileName(profile.ExecutablePath), profile.ExecutablePath);
    private static GameProfile Profile(string id, string name, string path) => new(id, name, path, HudProfileSettings.Default);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--profile-target")
        {
            Console.WriteLine("profile-target-ready");
            Console.Out.Flush();
            await Console.In.ReadLineAsync();
            return 0;
        }
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : "work");
        string directory = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        StoreChecks(directory);
        SelectionChecks();
        await MonitorChecks();
        NativeChecks();
        NativeLifecycleResult nativeLifecycle = await NativeLifecycleCheck();
        File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
        {
            passed = true, checks = _checks,
            nativeLifecycle,
            isolation = "Only task-owned profile files, synthetic snapshots and providers. Native checks query the test executable and one unique alias of its own apphost, started hidden without a window and stopped through stdin. Only that configured fixture's process image is queried. No real preference files, user input, hooks, injected code or sensor hardware are used."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {_checks} game profile store, selection and monitor checks.");
        return 0;
    }

    private static void StoreChecks(string directory)
    {
        string path = Path.Combine(directory, "profiles.json");
        var store = new GameProfileStore(path);
        Check(!store.Load().Enabled && store.Load().Profiles.Length == 0, "Missing profile preferences are disabled and empty");
        Check(!File.Exists(path), "Loading missing profiles never writes settings");
        Check(GameProfileStore.Normalize(null) == GameProfileSettings.Default, "Null profiles normalize to opt-in defaults");
        foreach (string value in new[] { "", "null", "[]", "{broken", "{\"enabled\":\"true\"}", "{\"profiles\":1}" })
        {
            File.WriteAllText(path, value);
            Check(!store.Load().Enabled && store.Load().Profiles.Length == 0, "Malformed profiles never enable process scanning: " + value);
        }
        foreach (string value in new[] { @"alpha.exe", @"C:alpha.exe", @"C:\Games\alpha.dll", @"C:\Games\alpha*.exe", @"C:\Games\alpha.exe:other.exe", "C:\\Games\\a\n.exe" })
            Check(!GameProfileStore.TryNormalizeExecutablePath(value, out _), "Invalid executable binding is rejected: " + value);
        Check(GameProfileStore.TryNormalizeExecutablePath(@" C:\Games\Alpha\..\Alpha\alpha.EXE ", out string canonical) && canonical == @"C:\Games\Alpha\alpha.EXE",
            "Canonical absolute executable paths preserve filename casing and remove travel segments");
        Check(GameProfileStore.TryNormalizeExecutablePath(@"\\?\C:\Games\Alpha\alpha.exe", out string extended) && extended == Alpha.ExecutablePath,
            "Extended Win32 paths compare with ordinary file-picker paths");

        var snapshot = new HudProfileSettings(new AppearanceSettings(.05, ClickThrough: true, Scale: 1.25),
            ComponentSettings.Default with { Cpu = true, MemoryTemperature = true, Charts = true },
            new PlacementSettings(" Synthetic monitor ", IslandPlacement.Custom, .27, .71, true));
        var original = new GameProfileSettings(true, new[] { Alpha with { Name = " Alpha profile ", Settings = snapshot }, Beta });
        Check(store.Save(original), "Two game profiles save atomically");
        GameProfileSettings loaded = new GameProfileStore(path).Load();
        Check(loaded.Enabled && loaded.Profiles.Length == 2 && loaded.Profiles[0].Id == Alpha.Id && loaded.Profiles[1].Id == Beta.Id,
            "Fresh store preserves enabled state, profile IDs and configured order");
        Check(loaded.Profiles[0].Name == "Alpha profile" && loaded.Profiles[0].Settings.Appearance.GlassOpacity == .05 &&
            loaded.Profiles[0].Settings.Appearance.Scale == 1.25 && !loaded.Profiles[0].Settings.Appearance.ClickThrough,
            "Profile snapshots retain transparency and scale with the global click-through mode reserved");
        Check(loaded.Profiles[0].Settings.Components == snapshot.Components && loaded.Profiles[0].Settings.Placement == snapshot.Placement with { MonitorId = "Synthetic monitor" },
            "Every component and custom monitor-aware position survives recreation");
        var tree = HudCapsuleLayout.FromLegacy(HudLayoutSettings.Default).ToTree(_ => false);
        string treeParent = tree.Islands[0].Root!.Id;
        for (int depth = 1; depth < HudCapsuleLayout.MaxTreeDepth - 1; depth++)
        {
            var container = HudLayoutNode.Create("grid");
            tree = tree.InsertNode(container, "main", treeParent, 0);
            treeParent = container.Id;
        }
        tree = tree.AddNode("time", "main", treeParent, 0);
        var treeProfile = Alpha with { Settings = snapshot with { Appearance = snapshot.Appearance with
            { Layout = HudLayoutSettings.Default with { Capsules = tree } } } };
        var treeStore = new GameProfileStore(Path.Combine(directory, "tree-profiles.json"));
        Check(treeStore.Save(new(true, new[] { treeProfile })) && treeStore.Load().Profiles.Single().Settings.Appearance.Layout.Capsules == tree,
            "The deepest supported nested layout roundtrips through a game profile file");
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
        {
            JsonElement settings = json.RootElement.GetProperty("profiles")[0].GetProperty("settings");
            JsonElement appearance = settings.GetProperty("appearance");
            Check(!appearance.TryGetProperty("mode", out _) && !appearance.TryGetProperty("compact", out _),
                "Retired appearance fields are omitted in saved profiles");
            Check(settings.GetProperty("placement").GetProperty("placement").GetString() == "Custom",
                "Placement enums are still stored by name inside the nested snapshot");
        }

        const string legacyProfile = """
            {"enabled":true,"profiles":[{
              "id":"11111111-1111-1111-1111-111111111111","name":"Alpha profile","executablePath":"C:\\Games\\Alpha\\alpha.exe",
              "settings":{
                "appearance":{"mode":"Matte","compact":true,"glassOpacity":0.25,"clickThrough":true,"scale":0.8,
                  "layout":{"detachedMusic":true,"musicPosition":{"monitorId":"Other","xFraction":0.7,"yFraction":0.9}},
                  "alerts":{"enabled":true,"cpuThreshold":94}},
                "components":{"fps":true,"date":false,"volumeWheel":true},
                "placement":{"monitorId":"Synthetic monitor","placement":"Custom","xFraction":0.27,"yFraction":0.71,"locked":true}
              }
            }]}
            """;
        File.WriteAllText(path, legacyProfile);
        GameProfileSettings imported = store.Load();
        var expectedAppearance = new AppearanceSettings(.25, Scale: .8)
        {
            Layout = HudLayoutSettings.Default with { DetachedMusic = true, MusicPosition = new("Other", .7, .9) },
            Alerts = AlertSettings.Default with { Enabled = true, CpuThreshold = 94 }
        };
        var expectedSnapshot = new HudProfileSettings(expectedAppearance,
            ComponentSettings.Default with { Fps = true, Date = false, VolumeWheel = true },
            new PlacementSettings("Synthetic monitor", IslandPlacement.Custom, .27, .71, true));
        Check(imported.Enabled && imported.Profiles.SequenceEqual(new[] { Alpha with { Name = "Alpha profile", Settings = expectedSnapshot } }),
            "Legacy matte and dense profile loads with its transparency, scale, islands, alerts, components and placement");
        Check(File.ReadAllText(path) == legacyProfile, "Loading an old profile does not rewrite its file");

        var duplicatePath = Beta with { ExecutablePath = Alpha.ExecutablePath.ToUpperInvariant() };
        var duplicateId = Beta with { Id = Alpha.Id.ToUpperInvariant() };
        var normalized = GameProfileStore.Normalize(new(true, new[] { Alpha, duplicatePath, duplicateId, Beta, Alpha with { Id = "not a GUID" } }));
        Check(normalized.Profiles.Length == 2 && normalized.Profiles[1].Id == Beta.Id,
            "Invalid and duplicate profiles cannot consume another valid profile's ID or path");
        Check(GameProfileStore.Normalize(new(true, new[] { Alpha with { Id = Guid.Empty.ToString() } })).Profiles.Length == 0,
            "An empty profile ID cannot become an active profile");
        var excessive = Enumerable.Range(0, GameProfileStore.MaxProfiles + 10).Select(index =>
            Profile(Guid.NewGuid().ToString("D"), "Game " + index, @"C:\Games\Game" + index + ".exe")).ToArray();
        Check(GameProfileStore.Normalize(new(true, excessive)).Profiles.Length == GameProfileStore.MaxProfiles,
            "Profile count is bounded independently of the input JSON");
        Check(GameProfileStore.Normalize(new(true, new[] { Alpha with { Name = new string('x', 120) } })).Profiles[0].Name.Length == GameProfileStore.MaxNameLength,
            "Visible profile names cannot grow beyond the layout limit");
        var invalidSnapshot = new HudProfileSettings(new AppearanceSettings(double.NaN, ClickThrough: true, Scale: double.NaN), null!,
            new PlacementSettings(" ", (IslandPlacement)99, double.PositiveInfinity, -2));
        var validSnapshot = GameProfileStore.NormalizeSnapshot(invalidSnapshot);
        Check(validSnapshot.Appearance.GlassOpacity == AppearanceSettings.Default.GlassOpacity && validSnapshot.Appearance.Scale == 1 && !validSnapshot.Appearance.ClickThrough &&
            validSnapshot.Components == ComponentSettings.Default && validSnapshot.Placement.MonitorId is null && validSnapshot.Placement.XFraction == .5 && validSnapshot.Placement.YFraction == 0,
            "Unsafe nested values fall back without allowing profile input-lock changes");
        Check(GameProfileStore.NormalizeSnapshot(snapshot with { Appearance = snapshot.Appearance with { Scale = 99 } }).Appearance.Scale == AppearanceStore.MaxScale,
            "Stored profile scale cannot bypass the supported HUD size range");

        string previous = File.ReadAllText(path);
        var huge = Alpha with { Settings = snapshot with { Placement = snapshot.Placement with { MonitorId = new string('m', GameProfileStore.MaxFileBytes) } } };
        Check(!store.Save(new(true, new[] { huge })) && File.ReadAllText(path) == previous,
            "An oversized save fails before replacing valid profile settings");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Check(!store.Save(Config), "A locked destination reports profile save failure");
            Check(!store.Load().Enabled, "An unreadable settings file cannot silently enable process scanning");
        }
        Check(File.ReadAllText(path) == previous && Directory.GetFiles(directory, "*.tmp").Length == 0,
            "Failed replacement preserves old bytes and leaves no temporary files");
        File.WriteAllBytes(path, new byte[GameProfileStore.MaxFileBytes + 1]);
        Check(!store.Load().Enabled && new FileInfo(path).Length == GameProfileStore.MaxFileBytes + 1,
            "An oversized input file is rejected without rewriting it");
    }

    private static void SelectionChecks()
    {
        var alpha = ProcessFor(Alpha, 100);
        var beta = ProcessFor(Beta, 200);
        Check(GameProfileSelector.Select(Config, Snapshot(700, alpha)).ProfileId == Alpha.Id,
            "A game launched in the background selects its profile without FPS or foreground ownership");
        Check(GameProfileSelector.Select(Config, Snapshot(200, alpha, beta), Alpha.Id).ProfileId == Beta.Id,
            "The foreground game takes priority when two configured games run");
        Check(GameProfileSelector.Select(Config, Snapshot(700, alpha, beta), Beta.Id).ProfileId == Beta.Id,
            "Alt+Tab to a browser retains the current running game profile");
        Check(GameProfileSelector.Select(Config, Snapshot(700, alpha, beta), Alpha.Id).ProfileId == Alpha.Id,
            "Starting a second background game does not unexpectedly replace the current game profile");
        Check(GameProfileSelector.Select(Config, Snapshot(700, beta), Alpha.Id).ProfileId == Beta.Id,
            "Exiting the current game selects the remaining configured game");
        Check(GameProfileSelector.Select(Config, Snapshot(700), Alpha.Id).ProfileId is null,
            "Exiting the final game restores the ordinary HUD context");
        var failed = GameProfileSelector.Select(Config, new(false, Array.Empty<GameProcessInfo>(), 0), Alpha.Id);
        Check(!failed.IsValid && failed.ProfileId == Alpha.Id, "A failed process snapshot must not trigger an ordinary-HUD restoration");
        Check(GameProfileSelector.Select(Config with { Enabled = false }, Snapshot(100, alpha), Alpha.Id).ProfileId is null,
            "Explicitly disabling auto profiles overrides running games");
        Check(GameProfileSelector.Select(Config, Snapshot(0, beta, alpha)).ProfileId == Alpha.Id,
            "No foreground match uses configured profile order rather than process enumeration order");
        Check(GameProfileSelector.Select(Config, Snapshot(0, new GameProcessInfo(101, "ALPHA.EXE", Alpha.ExecutablePath.ToUpperInvariant()))).ProfileId == Alpha.Id,
            "Executable path and filename matching are case-insensitive");
        Check(GameProfileSelector.Select(Config, Snapshot(0, new GameProcessInfo(101, "alpha.exe", @"C:\Other\alpha.exe"))).ProfileId is null,
            "A readable nonmatching full path cannot fall back to a same-name game");
        var byName = GameProfileSelector.Select(Config, Snapshot(101, new GameProcessInfo(101, "alpha.exe", null)));
        Check(byName.ProfileId == Alpha.Id && byName.Status.Contains("имени", StringComparison.Ordinal),
            "A single configured filename remains usable when process image access is denied, with an explicit reason");
        var sameName = new GameProfileSettings(true, new[] { Alpha, Beta with { ExecutablePath = @"C:\Games\Beta\alpha.exe" } });
        Check(GameProfileSelector.Select(sameName, Snapshot(101, new GameProcessInfo(101, "alpha.exe", null)), Alpha.Id).ProfileId is null,
            "An inaccessible executable cannot ambiguously match two same-name profile bindings");
        Check(GameProfileSelector.Select(sameName, Snapshot(101, new GameProcessInfo(101, "alpha.exe", @"C:\Games\Beta\alpha.exe"))).ProfileId == Beta.Id,
            "Two games with the same executable filename are distinguished by full image path");
        Check(GameProfileSelector.Select(Config, Snapshot(0, ProcessFor(Alpha, 0))).ProfileId is null,
            "Invalid process IDs cannot activate a profile");
    }

    private static async Task MonitorChecks()
    {
        int captures = 0;
        int namesValid = 1;
        GameProcessSnapshot current = Snapshot(100, ProcessFor(Alpha, 100));
        var changes = Channel.CreateUnbounded<GameProfileSelection>();
        using var monitor = new GameProfileMonitor((names, token) =>
        {
            Interlocked.Increment(ref captures);
            if (names.Count != 2 || !names.Contains("alpha.exe") || !names.Contains("beta.exe")) Interlocked.Exchange(ref namesValid, 0);
            return Task.FromResult(Volatile.Read(ref current));
        }, TimeSpan.FromMilliseconds(15));
        monitor.Changed += selection => changes.Writer.TryWrite(selection);
        monitor.Configure(Config with { Enabled = false });
        await monitor.Completion;
        Check(!monitor.IsRunning && captures == 0, "Disabled profiles retain no worker, polling delay or native process scans");
        monitor.Configure(new(true, Array.Empty<GameProfile>()));
        await monitor.Completion;
        Check(!monitor.IsRunning && captures == 0, "Enabled but empty profiles also avoid creating a process monitor");
        monitor.Configure(Config);
        GameProfileSelection first = await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(first.ProfileId == Alpha.Id && first.IsValid && monitor.IsCurrent(first), "The worker publishes a current-generation initial game selection");
        await WaitFor(() => Volatile.Read(ref captures) >= 3);
        Check(namesValid == 1, "Only configured executable names reach the native scan provider");
        Check(!changes.Reader.TryRead(out _), "Unchanged process snapshots do not flood the UI dispatcher");
        Check(monitor.IsCurrent(first), "Unchanged non-emitted polls keep the queued valid selection current");
        Volatile.Write(ref current, new(false, Array.Empty<GameProcessInfo>(), 0));
        GameProfileSelection invalid = await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(!invalid.IsValid && invalid.ProfileId == Alpha.Id, "Snapshot failures preserve the worker's current game and are marked invalid for the UI");
        Check(monitor.IsCurrent(first) && !monitor.IsCurrent(invalid), "An invalid snapshot does not evict the latest valid queued selection");
        Volatile.Write(ref current, Snapshot(200, ProcessFor(Beta, 200)));
        GameProfileSelection second = await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(second.ProfileId == Beta.Id && second.IsValid, "A later valid snapshot can recover and select another game");
        Check(second.Generation == first.Generation && !monitor.IsCurrent(first) && monitor.IsCurrent(second),
            "A newer game selection invalidates an obsolete UI callback within the same generation");
        Volatile.Write(ref current, Snapshot(100, ProcessFor(Alpha, 100)));
        GameProfileSelection returnedAlpha = await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(returnedAlpha.ProfileId == first.ProfileId && !ReferenceEquals(first, returnedAlpha) &&
            !monitor.IsCurrent(first) && monitor.IsCurrent(returnedAlpha),
            "Returning to the same game cannot revive an older equal-valued queued selection");
        Volatile.Write(ref current, Snapshot(200, ProcessFor(Beta, 200)));
        GameProfileSelection returnedBeta = await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(returnedBeta == second && !ReferenceEquals(returnedBeta, second) && !monitor.IsCurrent(second) && monitor.IsCurrent(returnedBeta),
            "Selection identity, not record value equality, determines the latest emitted valid event");
        Volatile.Write(ref current, new(false, Array.Empty<GameProcessInfo>(), 0));
        GameProfileSelection invalidBeta = await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(!invalidBeta.IsValid && monitor.IsCurrent(returnedBeta) && !monitor.IsCurrent(invalidBeta),
            "A later scan failure preserves the newest valid B selection instead of reviving A or evicting B");
        monitor.Configure(Config with { Enabled = false });
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        int stoppedAt = captures;
        await Task.Delay(40);
        Check(!monitor.IsRunning && captures == stoppedAt && !monitor.IsCurrent(first), "Disabling cancels all future scans and invalidates already-queued selections");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GameProcessSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = Channel.CreateUnbounded<GameProfileSelection>();
        int calls = 0, concurrent = 0, maximum = 0;
        using var serialized = new GameProfileMonitor(async (names, token) =>
        {
            int active = Interlocked.Increment(ref concurrent);
            maximum = Math.Max(maximum, active);
            try
            {
                if (Interlocked.Increment(ref calls) == 1) { started.TrySetResult(); return await release.Task; }
                return Snapshot(200, ProcessFor(Beta, 200));
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }, TimeSpan.FromMilliseconds(15));
        serialized.Changed += selection => queued.Writer.TryWrite(selection);
        var callerArray = new[] { Alpha, Beta };
        serialized.Configure(new(true, callerArray));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        callerArray[0] = Beta;
        serialized.Configure(Config, Beta.Id);
        await Task.Delay(20);
        Check(calls == 1, "A new generation waits for an in-flight old scan instead of overlapping native enumeration");
        release.TrySetResult(Snapshot(100, ProcessFor(Alpha, 100)));
        GameProfileSelection latest = await queued.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(latest.ProfileId == Beta.Id && maximum == 1 && serialized.IsCurrent(latest),
            "A canceled in-flight game result cannot overwrite the new generation's selection");
        serialized.Dispose();
        await serialized.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!serialized.IsRunning && !serialized.IsCurrent(latest), "Disposal stops polling and invalidates pending callbacks");
        bool rejected = false;
        try { serialized.Configure(Config); } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "Disposed profile monitors cannot start a replacement worker");
    }

    private static void NativeChecks()
    {
        string executable = Path.GetFileName(Environment.ProcessPath!);
        GameProcessSnapshot native = GameProcessReader.Capture(new[] { executable });
        Check(native.Succeeded && native.Processes.Length > 0 && native.Processes.All(process => string.Equals(process.ExecutableName, executable, StringComparison.OrdinalIgnoreCase)),
            "The real process-only snapshot returns only configured filename candidates");
        GameProcessInfo? self = native.Processes.SingleOrDefault(process => process.ProcessId == Environment.ProcessId);
        Check(self is not null && string.Equals(self.ExecutablePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
            "The native structure ABI and query-limited image reader resolve the test executable itself");
        using var process = Process.GetCurrentProcess();
        int before = process.HandleCount;
        for (int index = 0; index < 25; index++) GameProcessReader.Capture(new[] { executable });
        int after = process.HandleCount;
        Check(after <= before + 4, "Repeated native snapshots release their snapshot and candidate-process handles");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool stopped = false;
        try { GameProcessReader.Capture(new[] { executable }, canceled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped && GameProcessReader.Capture(Array.Empty<string>()).Processes.Length == 0,
            "A canceled or unconfigured native reader does not retain a scan");
    }

    private sealed record NativeLifecycleResult(bool StartDetected, bool ExitDetected, int ProcessId, bool HelperAndAliasRemoved, long ElapsedMilliseconds);

    private static async Task<NativeLifecycleResult> NativeLifecycleCheck()
    {
        string originalHost = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        Check(File.Exists(originalHost), "A task-owned test apphost is available for the native lifecycle fixture");
        string binaryDirectory = Path.GetDirectoryName(originalHost)!;
        string alias = Path.GetFullPath(Path.Combine(binaryDirectory, "GameFixture-" + Guid.NewGuid().ToString("N") + ".exe"));
        if (!string.Equals(Path.GetDirectoryName(alias), binaryDirectory, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture alias escaped its binary directory.");
        File.Copy(originalHost, alias, overwrite: false);
        var profile = Profile(Guid.NewGuid().ToString("D"), "Native lifecycle fixture", alias);
        var settings = new GameProfileSettings(true, new[] { profile });
        var selections = Channel.CreateUnbounded<GameProfileSelection>();
        using var monitor = new GameProfileMonitor(); // The actual production native reader and two-second interval.
        monitor.Changed += selection => selections.Writer.TryWrite(selection);
        Process? target = null;
        int targetPid = 0;
        bool exited = false;
        var watch = Stopwatch.StartNew();
        try
        {
            monitor.Configure(settings);
            GameProfileSelection initial = await ReadSelection(selections.Reader, selection => selection.IsValid && selection.ProfileId is null);
            Check(monitor.IsCurrent(initial) && monitor.IsRunning, "The real production monitor starts in the ordinary HUD context before its configured fixture launches");
            var start = new ProcessStartInfo(alias)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = binaryDirectory, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--profile-target");
            target = Process.Start(start) ?? throw new InvalidOperationException("Could not start the task-owned profile fixture.");
            targetPid = target.Id;
            string? ready = await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (ready != "profile-target-ready") throw new InvalidOperationException("Native fixture apphost failed: " + await target.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            GameProfileSelection launched = await ReadSelection(selections.Reader, selection => selection.IsValid && selection.ProfileId == profile.Id);
            Check(launched.ProcessId == targetPid && monitor.IsCurrent(launched),
                "The production native monitor detects the actual hidden fixture launch and selects its configured executable path without FPS");
            await target.StandardInput.WriteLineAsync("exit");
            await target.StandardInput.FlushAsync();
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(target.ExitCode == 0, "The task-owned native fixture exits cleanly through its own stdin rather than termination");
            exited = true;
            GameProfileSelection restored = await ReadSelection(selections.Reader, selection => selection.IsValid && selection.ProfileId is null);
            Check(restored.ProcessId is null && monitor.IsCurrent(restored),
                "The same production monitor detects actual fixture exit and returns to the ordinary HUD context");
        }
        finally
        {
            try
            {
                monitor.Dispose();
                await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                try
                {
                    if (target is not null)
                    {
                        if (!target.HasExited)
                        {
                            try { await target.StandardInput.WriteLineAsync("exit"); await target.StandardInput.FlushAsync(); }
                            catch (Exception exception) when (exception is IOException or InvalidOperationException) { }
                            if (!target.WaitForExit(2000)) { target.Kill(); if (!target.WaitForExit(2000)) throw new InvalidOperationException("Native fixture did not stop."); }
                        }
                        target.Dispose();
                    }
                }
                finally { File.Delete(alias); } // Exact task-created file; no directory cleanup or recursive deletion.
            }
        }
        Check(exited && !File.Exists(alias) && !monitor.IsRunning, "The native lifecycle fixture removes its own process, alias file and production monitoring worker");
        return new NativeLifecycleResult(true, true, targetPid, true, watch.ElapsedMilliseconds);
    }

    private static async Task<GameProfileSelection> ReadSelection(ChannelReader<GameProfileSelection> reader, Func<GameProfileSelection, bool> match)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            TimeSpan remaining = TimeSpan.FromSeconds(10) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Production profile monitor did not observe the fixture lifecycle.");
            GameProfileSelection selection = await reader.ReadAsync().AsTask().WaitAsync(remaining);
            if (match(selection)) return selection;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException(); await Task.Delay(5); }
    }
    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Interlocked.Increment(ref _checks);
    }
}

namespace Luma.Island
{
    internal static class App { internal static void Log(Exception exception) { } }
}
