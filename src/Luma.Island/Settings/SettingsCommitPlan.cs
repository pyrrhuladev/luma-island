using System;
using Luma.Island.Services;

namespace Luma.Island.Settings;

/// <summary>A normalized save target resolved before any store or visible HUD is changed.</summary>
public sealed record SettingsCommitPlan(HudProfileSettings VisibleHud, GameProfileSettings Profiles,
    string? ActiveProfileId, bool SaveHudAsGlobal)
{
    public static SettingsCommitPlan Create(HudProfileSettings baseHud, string? activeProfileId,
        HudProfileSettings editedHud, GameProfileSettings draftProfiles)
    {
        HudProfileSettings visible = CloneVisible(editedHud);
        GameProfileSettings normalized = GameProfileStore.Normalize(draftProfiles);
        // The existing settings format contains an array; never retain a caller-owned array.
        var profiles = new GameProfile[normalized.Profiles.Length];
        for (int index = 0; index < profiles.Length; index++)
        {
            GameProfile profile = normalized.Profiles[index];
            profiles[index] = profile with { Settings = CloneVisible(profile.Settings) };
        }
        var configuration = normalized with { Profiles = profiles };

        if (activeProfileId is null)
            return new(visible, configuration, null, SaveHudAsGlobal: true);

        string? canonicalId = Guid.TryParse(activeProfileId, out Guid parsed) && parsed != Guid.Empty
            ? parsed.ToString("D") : null;
        int activeIndex = canonicalId is null ? -1 : Array.FindIndex(profiles,
            profile => string.Equals(profile.Id, canonicalId, StringComparison.OrdinalIgnoreCase));
        if (activeIndex < 0)
        {
            // Removing the selected game returns to the entry-time base HUD. The deleted
            // game's draft must never be captured into ordinary appearance or position files.
            return new(CloneVisible(baseHud), configuration, null, SaveHudAsGlobal: false);
        }

        profiles[activeIndex] = profiles[activeIndex] with
        {
            Settings = GameProfileStore.NormalizeSnapshot(visible)
        };
        return new(visible, configuration, normalized.Enabled ? canonicalId : null,
            SaveHudAsGlobal: !normalized.Enabled);
    }

    private static HudProfileSettings CloneVisible(HudProfileSettings? snapshot) => new(
        AppearanceStore.Normalize(snapshot?.Appearance),
        (snapshot?.Components ?? ComponentSettings.Default) with { },
        PlacementStore.Normalize(snapshot?.Placement));
}
