using System;
using System.Linq;

namespace Luma.Island.Services;

/// <summary>Routes explicit HUD edits to the active game, while automatic selection leaves base files intact.</summary>
public sealed class GameProfileController
{
    private readonly AppearanceStore _baseAppearance;
    private readonly ComponentStore _baseComponents;
    private readonly PlacementStore _basePlacement;
    private readonly GameProfileStore _profileStore;
    private HudProfileSettings _savedBase;
    private GameProfileSettings _settings;
    private GameProfileSettings _savedSettings;
    private int _suppressed;
    public HudProfileSettings BaseSettings { get; private set; }
    public GameProfileSettings Settings => Clone(_settings);
    public string? ActiveProfileId { get; private set; }
    public GameProfile? ActiveProfile => _settings.Profiles.FirstOrDefault(profile => profile.Id == ActiveProfileId);
    public AppearanceStore AppearanceStore { get; }
    public ComponentStore ComponentStore { get; }
    public PlacementStore PlacementStore { get; }
    public HudProfileSettings CurrentSettings => ActiveProfile is { } game
        ? game.Settings with { Appearance = game.Settings.Appearance with { ClickThrough = BaseSettings.Appearance.ClickThrough } }
        : BaseSettings;

    public GameProfileController(AppearanceStore appearance, ComponentStore components, PlacementStore placement, GameProfileStore profiles)
    {
        _baseAppearance = appearance; _baseComponents = components; _basePlacement = placement; _profileStore = profiles;
        BaseSettings = NormalizeBase(new(appearance.Load(), components.Load(), placement.Load()));
        _savedBase = BaseSettings;
        _settings = GameProfileStore.Normalize(profiles.Load());
        _savedSettings = Clone(_settings);
        AppearanceStore = new RoutedAppearance(this);
        ComponentStore = new RoutedComponents(this);
        PlacementStore = new RoutedPlacement(this);
    }

    public bool ReplaceSettings(GameProfileSettings settings)
    {
        _settings = GameProfileStore.Normalize(settings);
        if (ActiveProfileId is not null && !_settings.Profiles.Any(profile => profile.Id == ActiveProfileId)) ActiveProfileId = null;
        return SaveProfiles();
    }

    public HudProfileSettings Activate(string? id)
    {
        if (id is not null && !_settings.Profiles.Any(profile => profile.Id == id)) throw new ArgumentException("Unknown game profile.", nameof(id));
        ActiveProfileId = id;
        return CurrentSettings;
    }

    public IDisposable SuppressWrites()
    {
        _suppressed++;
        return new Suppression(this);
    }

    public bool CaptureCurrent(HudProfileSettings snapshot)
    {
        if (_suppressed > 0) return true;
        if (ActiveProfileId is not null)
        {
            BaseSettings = BaseSettings with { Appearance = BaseSettings.Appearance with { ClickThrough = snapshot.Appearance.ClickThrough } };
            return FlushPendingBase() & SaveGameSnapshot(snapshot);
        }
        // Bitwise AND intentionally attempts all three independent existing stores after a failure.
        return SaveAppearance(snapshot.Appearance) & SaveComponents(snapshot.Components) & SavePlacement(snapshot.Placement);
    }

    public bool KeepCurrentAsGlobal(HudProfileSettings snapshot)
    {
        ActiveProfileId = null;
        return CaptureCurrent(snapshot);
    }

    private bool SaveAppearance(AppearanceSettings appearance)
    {
        if (_suppressed > 0) return true;
        appearance = Services.AppearanceStore.Normalize(appearance);
        if (ActiveProfileId is null)
        {
            BaseSettings = BaseSettings with { Appearance = appearance };
            if (_savedBase.Appearance == appearance) return true;
            bool saved = _baseAppearance.Save(appearance);
            if (saved) _savedBase = _savedBase with { Appearance = appearance };
            return saved;
        }
        // Input recovery is global. Selecting a game must never silently change click-through mode.
        BaseSettings = BaseSettings with { Appearance = BaseSettings.Appearance with { ClickThrough = appearance.ClickThrough } };
        bool inputSaved = FlushPendingBase();
        return SaveGameSnapshot(CurrentSettings with { Appearance = appearance }) & inputSaved;
    }

    private bool SaveComponents(ComponentSettings components)
    {
        if (_suppressed > 0) return true;
        if (ActiveProfileId is not null) return SaveGameSnapshot(CurrentSettings with { Components = components });
        BaseSettings = BaseSettings with { Components = components };
        if (_savedBase.Components == components) return true;
        bool saved = _baseComponents.Save(components);
        if (saved) _savedBase = _savedBase with { Components = components };
        return saved;
    }

    private bool SavePlacement(PlacementSettings placement)
    {
        if (_suppressed > 0) return true;
        placement = Services.PlacementStore.Normalize(placement);
        if (ActiveProfileId is not null) return SaveGameSnapshot(CurrentSettings with { Placement = placement });
        BaseSettings = BaseSettings with { Placement = placement };
        if (_savedBase.Placement == placement) return true;
        bool saved = _basePlacement.Save(placement);
        if (saved) _savedBase = _savedBase with { Placement = placement };
        return saved;
    }

    private bool SaveGameSnapshot(HudProfileSettings snapshot)
    {
        var active = ActiveProfile;
        if (active is null) return false;
        var normalized = GameProfileStore.NormalizeSnapshot(snapshot);
        if (active.Settings != normalized)
            _settings = _settings with { Profiles = _settings.Profiles.Select(profile => profile.Id == active.Id ? profile with { Settings = normalized } : profile).ToArray() };
        return SaveProfiles();
    }

    private bool SaveProfiles()
    {
        if (_settings.Enabled == _savedSettings.Enabled && _settings.Profiles.SequenceEqual(_savedSettings.Profiles)) return true;
        if (!_profileStore.Save(_settings)) return false;
        _savedSettings = Clone(_settings);
        return true;
    }

    private bool FlushPendingBase()
    {
        bool saved = true;
        if (_savedBase.Appearance != BaseSettings.Appearance)
        {
            bool result = _baseAppearance.Save(BaseSettings.Appearance);
            if (result) _savedBase = _savedBase with { Appearance = BaseSettings.Appearance };
            saved &= result;
        }
        if (_savedBase.Components != BaseSettings.Components)
        {
            bool result = _baseComponents.Save(BaseSettings.Components);
            if (result) _savedBase = _savedBase with { Components = BaseSettings.Components };
            saved &= result;
        }
        if (_savedBase.Placement != BaseSettings.Placement)
        {
            bool result = _basePlacement.Save(BaseSettings.Placement);
            if (result) _savedBase = _savedBase with { Placement = BaseSettings.Placement };
            saved &= result;
        }
        return saved;
    }

    private static GameProfileSettings Clone(GameProfileSettings settings) => settings with { Profiles = (GameProfile[])settings.Profiles.Clone() };
    private static HudProfileSettings NormalizeBase(HudProfileSettings settings) => new(
        Services.AppearanceStore.Normalize(settings.Appearance), settings.Components, Services.PlacementStore.Normalize(settings.Placement));

    private sealed class Suppression(GameProfileController owner) : IDisposable
    {
        private GameProfileController? _owner = owner;
        public void Dispose() { if (_owner is not { } current) return; _owner = null; current._suppressed--; }
    }
    private sealed class RoutedAppearance(GameProfileController owner) : AppearanceStore
    {
        public override AppearanceSettings Load() => owner.CurrentSettings.Appearance;
        public override bool Save(AppearanceSettings settings) => owner.SaveAppearance(settings);
    }
    private sealed class RoutedComponents(GameProfileController owner) : ComponentStore
    {
        public override ComponentSettings Load() => owner.CurrentSettings.Components;
        public override bool Save(ComponentSettings settings) => owner.SaveComponents(settings);
    }
    private sealed class RoutedPlacement(GameProfileController owner) : PlacementStore
    {
        public override PlacementSettings Load() => owner.CurrentSettings.Placement;
        public override bool Save(PlacementSettings settings) => owner.SavePlacement(settings);
    }
}
