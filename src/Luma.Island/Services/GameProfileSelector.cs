using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Luma.Island.Services;

public sealed record GameProcessInfo(int ProcessId, string ExecutableName, string? ExecutablePath);
public sealed record GameProcessSnapshot(bool Succeeded, GameProcessInfo[] Processes, int ForegroundProcessId, string? Error = null);
public sealed record GameProfileSelection(string? ProfileId, int? ProcessId, string Status, bool IsValid = true, long Generation = 0);

/// <summary>Foreground game wins; Alt+Tab keeps a running game; a valid empty snapshot restores the ordinary HUD.</summary>
public static class GameProfileSelector
{
    public static GameProfileSelection Select(GameProfileSettings settings, GameProcessSnapshot snapshot, string? activeProfileId = null)
    {
        if (!settings.Enabled) return new(null, null, "Автопереключение выключено.");
        if (!snapshot.Succeeded) return new(activeProfileId, null, "Не удалось проверить процессы. Текущий профиль сохранён.", false);
        GameProfile[] profiles = settings.Profiles ?? Array.Empty<GameProfile>();
        var matches = new List<(GameProfile Profile, GameProcessInfo Process, bool ByName)>();
        foreach (GameProcessInfo process in snapshot.Processes ?? Array.Empty<GameProcessInfo>())
        {
            if (process.ProcessId <= 0) continue;
            string name = Path.GetFileName(process.ExecutableName);
            GameProfile[] candidates = profiles.Where(profile => string.Equals(Path.GetFileName(profile.ExecutablePath), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0) continue;
            if (!string.IsNullOrWhiteSpace(process.ExecutablePath))
            {
                if (!GameProfileStore.TryNormalizeExecutablePath(process.ExecutablePath, out string actualPath)) continue;
                GameProfile? exact = candidates.FirstOrDefault(profile => string.Equals(profile.ExecutablePath, actualPath, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) matches.Add((exact, process, false));
            }
            else if (candidates.Length == 1) matches.Add((candidates[0], process, true));
        }
        var foreground = matches.FirstOrDefault(match => match.Process.ProcessId == snapshot.ForegroundProcessId);
        if (foreground.Profile is not null) return Result(foreground);
        var active = matches.FirstOrDefault(match => string.Equals(match.Profile.Id, activeProfileId, StringComparison.OrdinalIgnoreCase));
        if (active.Profile is not null) return Result(active);
        foreach (GameProfile profile in profiles)
        {
            var first = matches.Where(match => match.Profile.Id == profile.Id).OrderBy(match => match.Process.ProcessId).FirstOrDefault();
            if (first.Profile is not null) return Result(first);
        }
        return new(null, null, "Игр с профилем нет. Обычные настройки HUD.");
    }

    private static GameProfileSelection Result((GameProfile Profile, GameProcessInfo Process, bool ByName) match) => new(
        match.Profile.Id, match.Process.ProcessId, match.ByName
            ? "Игра найдена по имени процесса; полный путь недоступен."
            : "Игра найдена по пути исполняемого файла.");
}
