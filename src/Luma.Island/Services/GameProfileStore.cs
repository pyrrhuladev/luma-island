using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luma.Island.Services;

public sealed record HudProfileSettings(AppearanceSettings Appearance, ComponentSettings Components, PlacementSettings Placement)
{
    public static HudProfileSettings Default { get; } = new(AppearanceSettings.Default, ComponentSettings.Default, PlacementSettings.Default);
}

public sealed record GameProfile(string Id, string Name, string ExecutablePath, HudProfileSettings Settings);

public sealed record GameProfileSettings(bool Enabled, GameProfile[] Profiles)
{
    public static GameProfileSettings Default { get; } = new(false, Array.Empty<GameProfile>());
}

/// <summary>Separate, bounded game bindings. No normal HUD preference file is changed by this store.</summary>
public sealed class GameProfileStore
{
    public const int MaxProfiles = 64;
    public const int MaxNameLength = 80;
    public const int MaxFileBytes = 1024 * 1024;
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 40,
        Converters =
        {
            new JsonStringEnumConverter<IslandPlacement>(allowIntegerValues: false)
        }
    };

    public GameProfileStore(string? path = null) => _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland", "profiles.json"));

    public GameProfileSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return GameProfileSettings.Default;
            using var stream = File.OpenRead(_path);
            if (stream.Length > MaxFileBytes) throw new InvalidDataException("Game profile settings exceed the size limit.");
            return Normalize(JsonSerializer.Deserialize<GameProfileSettings>(stream, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            App.Log(exception);
            return GameProfileSettings.Default;
        }
    }

    public bool Save(GameProfileSettings settings)
    {
        string temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Normalize(settings), JsonOptions);
            if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Game profile settings exceed the size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            App.Log(exception);
            return false;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { App.Log(exception); }
        }
    }

    public static GameProfileSettings Normalize(GameProfileSettings? settings)
    {
        if (settings is null) return GameProfileSettings.Default;
        var profiles = new List<GameProfile>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameProfile? profile in settings.Profiles ?? Array.Empty<GameProfile>())
        {
            if (profile is null || !Guid.TryParse(profile.Id, out Guid id) || id == Guid.Empty ||
                !TryNormalizeExecutablePath(profile.ExecutablePath, out string path)) continue;
            string normalizedId = id.ToString("D");
            if (ids.Contains(normalizedId) || paths.Contains(path)) continue;
            ids.Add(normalizedId);
            paths.Add(path);
            string name = string.IsNullOrWhiteSpace(profile.Name) ? Path.GetFileNameWithoutExtension(path) : profile.Name.Trim();
            if (ContainsControl(name)) name = Path.GetFileNameWithoutExtension(path);
            if (name.Length > MaxNameLength) name = name[..MaxNameLength];
            profiles.Add(new GameProfile(normalizedId, name, path, NormalizeSnapshot(profile.Settings)));
            if (profiles.Count == MaxProfiles) break;
        }
        return new GameProfileSettings(settings.Enabled, profiles.ToArray());
    }

    public static HudProfileSettings NormalizeSnapshot(HudProfileSettings? settings) => new(
        AppearanceStore.Normalize(settings?.Appearance) with { ClickThrough = false },
        settings?.Components ?? ComponentSettings.Default,
        PlacementStore.Normalize(settings?.Placement));

    public static bool TryNormalizeExecutablePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || ContainsControl(path)) return false;
        try
        {
            path = path.Trim();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && path[5] == ':') path = path[4..];
            if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                path.IndexOfAny(new[] { '*', '?' }) >= 0 || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
            normalized = Path.GetFullPath(path);
            return normalized.IndexOf(':', 2) < 0 && Path.GetFileName(normalized).IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                !string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(normalized));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool ContainsControl(string value)
    {
        foreach (char character in value) if (char.IsControl(character)) return true;
        return false;
    }
}
