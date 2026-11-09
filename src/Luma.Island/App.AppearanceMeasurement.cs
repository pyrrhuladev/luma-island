using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Luma.Island;

public partial class App
{
    private async Task MeasureAppearanceAsync(string directory)
    {
        var samples = new List<object>();
        using var process = Process.GetCurrentProcess();
        foreach (double opacity in new[] { 1, .76 })
        {
            _island!.SetGlassOpacity(opacity);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(2000);
            process.Refresh();
            double before = process.TotalProcessorTime.TotalMilliseconds;
            var watch = Stopwatch.StartNew();
            await Task.Delay(5000);
            watch.Stop();
            process.Refresh();
            double cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds - before;
            samples.Add(new
            {
                opacity, windowVisible = _island.IsVisible,
                sampleSeconds = Math.Round(watch.Elapsed.TotalSeconds, 3),
                cpuMilliseconds,
                cpuPercentOfMachine = Math.Round(100 * cpuMilliseconds / (watch.Elapsed.TotalMilliseconds * Environment.ProcessorCount), 3),
                workingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 1),
                privateCommittedMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1)
            });
        }
        File.WriteAllText(Path.Combine(directory, "appearance-performance.json"),
            JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
    }
}
