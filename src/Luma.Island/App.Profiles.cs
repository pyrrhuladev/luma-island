using System;
using Luma.Island.Services;

namespace Luma.Island;

public partial class App
{
    private GameProfileSelection? _pendingProfileSelection;
    private void InitializeGameProfiles()
    {
        if (_island is null || _profileController is null) return;
        _profileMonitor = new GameProfileMonitor();
        _profileMonitor.Changed += OnGameProfileSelected;
        _island.ProfileConfigurationChanged += ConfigureGameProfiles;
        _island.ProfileEditorVisibilityChanged += OnProfileEditorVisibility;
        _island.ProfileInteractionEnded += OnProfileInteractionEnded;
        ConfigureGameProfiles(_island.GameProfiles);
    }

    private void ConfigureGameProfiles(GameProfileSettings settings)
    {
        _pendingProfileSelection = null;
        _profileMonitor?.Configure(_island?.IsProfileEditorOpen == true ? GameProfileSettings.Default : settings,
            _island?.ActiveGameProfileId);
    }

    private void OnProfileEditorVisibility(bool open) => ConfigureGameProfiles(_island?.GameProfiles ?? GameProfileSettings.Default);

    private void OnGameProfileSelected(GameProfileSelection selection)
    {
        if (!selection.IsValid || _profileMonitor?.IsCurrent(selection) != true || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_profileMonitor?.IsCurrent(selection) != true || _island is null || _island.IsProfileEditorOpen) return;
            if (_island.IsHudDragging) { _pendingProfileSelection = selection; return; }
            _pendingProfileSelection = null;
            if (_island.ActiveGameProfileId != selection.ProfileId) _tray?.CloseMenu();
            _island.ActivateGameProfile(selection.ProfileId);
        }));
    }

    private void OnProfileInteractionEnded()
    {
        var pending = _pendingProfileSelection;
        _pendingProfileSelection = null;
        if (pending is not null) OnGameProfileSelected(pending);
    }

    private void DisposeGameProfiles()
    {
        if (_island is not null)
        {
            _island.ProfileConfigurationChanged -= ConfigureGameProfiles;
            _island.ProfileEditorVisibilityChanged -= OnProfileEditorVisibility;
            _island.ProfileInteractionEnded -= OnProfileInteractionEnded;
        }
        if (_profileMonitor is null) return;
        _profileMonitor.Changed -= OnGameProfileSelected;
        _profileMonitor.Dispose();
        try { if (!_profileMonitor.Completion.Wait(TimeSpan.FromSeconds(2))) Log(new TimeoutException("Проверка профилей игр не завершилась вовремя.")); }
        catch (Exception exception) { Log(exception); }
    }
}
