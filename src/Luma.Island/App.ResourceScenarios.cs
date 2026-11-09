using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Luma.Island.Services;

namespace Luma.Island;

public partial class App
{
    // Measurement itself runs in a separate process, so its CPU/RAM is not charged to Luma.
    private async Task RunResourceScenariosAsync(string directory)
    {
        var events = new List<object>();
        string phaseFile = Path.Combine(directory, "phase.json");
        _island!.SetGlassOpacity(.76);
        _island.Placement.SetPreset(IslandPlacement.TopRight);
        try
        {
            await SignalAsync("game-preflight", false, false, "waiting-for-game");
            _island.SetComponents(ComponentSettings.Default with { Fps = true });
            var readiness = System.Diagnostics.Stopwatch.StartNew();
            int readySamples = 0;
            FpsSnapshot? previous = null;
            string? readyProcess = null;
            while (readySamples < 5)
            {
                await Task.Delay(1000);
                var frame = _fps!.Current;
                if (frame.Fps is > 0 && !ReferenceEquals(previous, frame))
                {
                    if (readyProcess != frame.ProcessName) { readySamples = 0; readyProcess = frame.ProcessName; }
                    readySamples++;
                    previous = frame;
                }
                else if (frame.Fps is null) readySamples = 0;
                if (readiness.Elapsed > TimeSpan.FromSeconds(50))
                    throw new InvalidOperationException("Не получены устойчивые FPS активной игры: " + frame.Status);
            }
            await SignalAsync("game-preflight", false, false, "game-ready");
            foreach (var phase in new (string Name, bool Counters, bool Charts, int Warmup)[]
                { ("off-before", false, false, 4), ("counters-on", true, false, 6),
                  ("charts-on", true, true, 4), ("off-after", false, false, 4) })
            {
                await SignalAsync(phase.Name, false, false, "configure");
                _island.SetComponents(ComponentSettings.Default with { Cpu = phase.Counters, Gpu = phase.Counters,
                    Memory = phase.Counters, Fps = phase.Counters, Charts = phase.Charts });
                if (!phase.Counters) await _fps!.Completion.WaitAsync(TimeSpan.FromSeconds(15));
                await SignalAsync(phase.Name, false, false, "warmup");
                await Task.Delay(TimeSpan.FromSeconds(phase.Warmup));
                await SignalAsync(phase.Name, true, false, "measure");
                await Task.Delay(TimeSpan.FromSeconds(20));
                await SignalAsync(phase.Name, false, false, "completed");
            }
        }
        finally
        {
            _island.SetComponents(ComponentSettings.Default);
            await _fps!.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            await SignalAsync("done", false, true, "done");
        }

        // Render preview artifacts only after every measured interval has ended.
        _island.SetComponents(ComponentSettings.Default with { Cpu = true, Gpu = true, Memory = true, Fps = true, Charts = true });
        await Task.Delay(TimeSpan.FromSeconds(12));
        SavePreview(_island.CapturePreview(), Path.Combine(directory, "charts-on.png"));
        _island.SetGlassOpacity(.25);
        await Dispatcher.InvokeAsync(_island.UpdateLayout);
        SavePreview(_island.CapturePreview(), Path.Combine(directory, "charts-transparent.png"));
        _island.SetComponents(_island.Components with { Charts = false });
        await Dispatcher.InvokeAsync(_island.UpdateLayout);
        SavePreview(_island.CapturePreview(), Path.Combine(directory, "counters-on.png"));
        _island.SetComponents(ComponentSettings.Default);
        await _fps!.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        async Task SignalAsync(string scenario, bool measure, bool done, string stage)
        {
            var signal = new { scenario, measure, done, stage, targetPid = Environment.ProcessId,
                utc = DateTimeOffset.UtcNow, fps = _fps?.Current, load = _performance?.Current, components = _island.Components };
            string temporary = phaseFile + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(signal));
            File.Move(temporary, phaseFile, overwrite: true);
            events.Add(signal);
            await File.WriteAllTextAsync(Path.Combine(directory, "scenarios.json"),
                JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
