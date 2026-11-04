using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luma.Island.Services;

public enum IslandAppearance { Matte, Glass }

public sealed record AppearanceSettings(IslandAppearance Mode = IslandAppearance.Glass, double GlassOpacity = .40, bool Compact = false, bool ClickThrough = false, double Scale = 1)
{
    public static AppearanceSettings Default { get; } = new();
    public HudLayoutSettings Layout { get; init; } = HudLayoutSettings.Default;
    public AlertSettings Alerts { get; init; } = AlertSettings.Default;
}

/// <summary>Independent appearance preferences, replaced atomically after a complete write.</summary>
public class AppearanceStore
{
    public const double MinOpacity = .05;
    public const double MaxOpacity = 1;
    public const double MinScale = .75;
    public const double MaxScale = 1.5;

    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter<IslandAppearance>(allowIntegerValues: false) }
    };

    public AppearanceStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LumaIsland", "appearance.json"));
    }

    public virtual AppearanceSettings Load()
    {
        if (!File.Exists(_path)) return AppearanceSettings.Default;
        try
        {
            using var stream = File.OpenRead(_path);
            return Normalize(JsonSerializer.Deserialize<AppearanceSettings>(stream, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            App.Log(ex);
            return AppearanceSettings.Default;
        }
    }

    public virtual bool Save(AppearanceSettings settings)
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

    public static AppearanceSettings Normalize(AppearanceSettings? settings)
    {
        settings ??= AppearanceSettings.Default;
        return settings with
        {
            Mode = Enum.IsDefined(settings.Mode) ? settings.Mode : IslandAppearance.Matte,
            GlassOpacity = double.IsFinite(settings.GlassOpacity)
                ? Math.Clamp(settings.GlassOpacity, MinOpacity, MaxOpacity)
                : AppearanceSettings.Default.GlassOpacity,
            Scale = double.IsFinite(settings.Scale)
                ? Math.Clamp(settings.Scale, MinScale, MaxScale)
                : AppearanceSettings.Default.Scale,
            Layout = HudLayoutSettings.Normalize(settings.Layout),
            Alerts = AlertSettings.Normalize(settings.Alerts)
        };
    }
}
