using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luma.Island.Services;

public sealed record ComponentSettings(
    bool Music = true,
    bool Volume = true,
    bool Time = true,
    bool Date = true,
    bool Battery = true,
    bool Cpu = false,
    bool Gpu = false,
    bool Memory = false,
    bool Fps = false,
    bool Charts = false,
    bool GpuTemperature = false,
    bool CpuTemperature = false,
    bool MemoryTemperature = false,
    bool OnePercentLow = false,
    bool FrameTime = false,
    bool VolumeWheel = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? CpuChart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? GpuChart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MemoryChart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FpsChart = null)
{
    public static ComponentSettings Default { get; } = new();

    [JsonIgnore] public bool ShowCpuChart => CpuChart ?? (Charts && Cpu);
    [JsonIgnore] public bool ShowGpuChart => GpuChart ?? (Charts && Gpu);
    [JsonIgnore] public bool ShowMemoryChart => MemoryChart ?? (Charts && Memory);
    [JsonIgnore] public bool ShowFpsChart => FpsChart ?? (Charts && Fps);
    [JsonIgnore] public bool HasCharts => ShowCpuChart || ShowGpuChart || ShowMemoryChart || ShowFpsChart;
}

/// <summary>Component choices only; reading settings does not start any system monitors.</summary>
public class ComponentStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 8
    };

    public ComponentStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LumaIsland", "components.json"));
    }

    public virtual ComponentSettings Load()
    {
        if (!File.Exists(_path)) return ComponentSettings.Default;
        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<ComponentSettings>(stream, JsonOptions) ?? ComponentSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            App.Log(ex);
            return ComponentSettings.Default;
        }
    }

    public virtual bool Save(ComponentSettings settings)
    {
        string temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings ?? ComponentSettings.Default, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex);
            return false;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log(ex); }
        }
    }
}
