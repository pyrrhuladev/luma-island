using System;
using System.Collections.Generic;
using System.Windows;
using Luma.Island.Services;

namespace Luma.Island.Settings;

/// <summary>Read-only application state captured when opening the settings draft.</summary>
public sealed record SettingsEditorContext(
    PlacementSettings Placement,
    HudProfileSettings BaseHud,
    GameProfileSettings Profiles,
    string? ActiveProfileId,
    IReadOnlyList<DisplayInfo> Displays,
    AutostartState Autostart,
    Action<Window>? OpenSensors = null,
    Action? OpenWindowsStartup = null,
    Func<IReadOnlyList<DisplayInfo>>? RefreshDisplays = null);

/// <summary>Additional preferences remain local until the outer settings window is accepted.</summary>
public sealed record SettingsApplicationDraft(PlacementSettings Placement, GameProfileSettings Profiles,
    bool AutostartRegistered, bool AutostartChanged);
