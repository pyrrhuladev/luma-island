using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luma.Island.Services;

public enum IslandPlacement
{
    Custom, TopLeft, TopCenter, TopRight, MiddleLeft, Center,
    MiddleRight, BottomLeft, BottomCenter, BottomRight
}

/// <summary>Custom fractions describe the free travel distance after subtracting the island's size.</summary>
public sealed record PlacementSettings(
    string? MonitorId = null,
    IslandPlacement Placement = IslandPlacement.TopCenter,
    double XFraction = 0.5,
    double YFraction = 0,
    bool Locked = false)
{
    public static PlacementSettings Default { get; } = new();
}

/// <summary>Small, atomic settings file. An explicit filename keeps tests isolated.</summary>
public class PlacementStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter<IslandPlacement>(allowIntegerValues: false) }
    };

    public PlacementStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LumaIsland", "placement.json"));
    }

    public virtual PlacementSettings Load()
    {
        if (!File.Exists(_path)) return PlacementSettings.Default;
        try
        {
            using var stream = File.OpenRead(_path);
            return Normalize(JsonSerializer.Deserialize<PlacementSettings>(stream, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            App.Log(ex);
            return PlacementSettings.Default;
        }
    }

    public virtual bool Save(PlacementSettings settings)
    {
        string temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, Normalize(settings), JsonOptions);
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

    public static PlacementSettings Normalize(PlacementSettings? settings)
    {
        settings ??= PlacementSettings.Default;
        return settings with
        {
            MonitorId = string.IsNullOrWhiteSpace(settings.MonitorId) ? null : settings.MonitorId.Trim(),
            Placement = Enum.IsDefined(settings.Placement) ? settings.Placement : IslandPlacement.TopCenter,
            XFraction = double.IsFinite(settings.XFraction) ? Math.Clamp(settings.XFraction, 0, 1) : 0.5,
            YFraction = double.IsFinite(settings.YFraction) ? Math.Clamp(settings.YFraction, 0, 1) : 0
        };
    }
}
