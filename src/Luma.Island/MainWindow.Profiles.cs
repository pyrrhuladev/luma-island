using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly GameProfileController? _profiles;
    private string? _profileSaveError;
    public string? ActiveGameProfileId => _profiles?.ActiveProfileId;
    public GameProfileSettings GameProfiles => _profiles?.Settings ?? GameProfileSettings.Default;
    public bool IsProfileEditorOpen { get; private set; }
    public bool IsHudDragging => _placement?.IsDragging == true || _musicIsland?._placement?.IsDragging == true;
    public event Action<GameProfileSettings>? ProfileConfigurationChanged;
    public event Action<bool>? ProfileEditorVisibilityChanged;
    public event Action? ProfileInteractionEnded;
    private void OnProfileDragCompleted()
    {
        if (_islandOwner is not null) { _islandOwner.OnProfileDragCompleted(); return; }
        SynchronizeMusicIsland();
        if (_musicIsland is not null)
        {
            SaveAppearance();
            // A newly selected profile may still contain an automatic (null)
            // music position even when the rendered appearance is already equal.
            SaveCurrentProfileEdits();
        }
        ProfileInteractionEnded?.Invoke();
    }

    public HudProfileSettings CaptureHudSettings() => new(Appearance, Components,
        _placement?.Settings ?? _placementStore.Load());

    public bool ActivateGameProfile(string? id)
    {
        VerifyAccess();
        if (_closed || _profiles is null || !GameProfiles.Enabled || IsProfileEditorOpen || IsHudDragging) return false;
        if (id == _profiles.ActiveProfileId) return true;
        if (id is not null && !GameProfiles.Profiles.Any(profile => profile.Id == id)) return false;
        IslandSurface.ContextMenu.IsOpen = false;
        bool saved = _profiles.CaptureCurrent(CaptureHudSettings());
        ApplyProfileSnapshot(_profiles.Activate(id));
        RecordProfileSaveResult(saved);
        return true;
    }

    public bool SetGameProfiles(GameProfileSettings settings)
    {
        VerifyAccess();
        if (_closed || _profiles is null) return false;
        var current = CaptureHudSettings();
        string? previousId = _profiles.ActiveProfileId;
        bool saved = _profiles.CaptureCurrent(current);
        saved &= _profiles.ReplaceSettings(settings);
        if (!GameProfiles.Enabled)
        {
            // Disabling is an explicit choice to keep the current view as the ordinary HUD.
            if (previousId is not null) saved &= _profiles.KeepCurrentAsGlobal(current);
        }
        else if (previousId is not null)
        {
            ApplyProfileSnapshot(_profiles.CurrentSettings); // Includes a deleted active game's return to base.
        }
        RecordProfileSaveResult(saved);
        ProfileConfigurationChanged?.Invoke(GameProfiles);
        return saved;
    }

    private void ApplyProfileSnapshot(HudProfileSettings settings)
    {
        if (_profiles is null || _placement is null) return;
        using (_profiles.SuppressWrites())
        {
            ResetAlerts();
            bool inputPreference = Appearance.ClickThrough;
            Appearance = AppearanceStore.Normalize(settings.Appearance with { ClickThrough = inputPreference });
            Components = settings.Components;
            _placement.ApplySettings(settings.Placement);
            UpdateClockAndBattery();
            ApplyComponentVisibility();
            ApplyAppearance();
            OnMediaChanged(_lastMedia ?? _media.Current);
            _savedAppearance = Appearance;
        }
        ComponentsChanged?.Invoke(Components);
    }

    public bool SaveCurrentProfileEdits()
    {
        VerifyAccess();
        if (_profiles is null) return true;
        bool saved = _profiles.CaptureCurrent(CaptureHudSettings());
        RecordProfileSaveResult(saved);
        return saved;
    }

    private void RecordProfileSaveResult(bool saved)
    {
        _profileSaveError = saved ? null : "Не удалось сохранить изменения HUD. Проверьте доступ к папке настроек.";
        if (!saved) App.Log(new System.IO.IOException("Game HUD profile settings could not be saved."));
    }

    public MenuItem CreateProfilesMenu()
    {
        var menu = new MenuItem { Header = "Профили игр" };
        void Populate()
        {
            menu.Items.Clear();
            var automatic = new MenuItem { Header = "Переключать автоматически", IsCheckable = true,
                IsChecked = GameProfiles.Enabled, IsEnabled = _profiles is not null };
            automatic.ToolTip = "При выключении текущий HUD останется на месте. Дальнейшие изменения сохраняются как обычные настройки.";
            automatic.Click += (_, _) =>
            {
                if (!SetGameProfiles(GameProfiles with { Enabled = automatic.IsChecked })) ShowProfileSaveWarning();
            };
            menu.Items.Add(automatic);
            menu.Items.Add(new MenuItem { Header = "Сейчас: " + (_profiles?.ActiveProfile?.Name ?? "Общий HUD"), IsEnabled = false,
                ToolTip = _profileSaveError });
            menu.Items.Add(new Separator());
            var manage = new MenuItem { Header = "Настроить профили…", IsEnabled = _profiles is not null };
            manage.Click += (_, _) => OpenGameProfiles();
            menu.Items.Add(manage);
        }
        Populate();
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) Populate(); };
        return menu;
    }

    public void OpenGameProfiles()
    {
        VerifyAccess();
        if (_closed || _profiles is null || IsProfileEditorOpen || IsHudDragging) return;
        SaveCurrentProfileEdits();
        IsProfileEditorOpen = true;
        ProfileEditorVisibilityChanged?.Invoke(true);
        try
        {
            var editor = new GameProfilesWindow(GameProfiles, CaptureHudSettings(), ActiveGameProfileId) { Owner = this };
            double availableHeight = Placement.CurrentDisplay.WorkArea.Height / DisplayService.GetWindowScale(this) - 16;
            editor.MaxHeight = Math.Max(editor.MinHeight, availableHeight);
            editor.Height = Math.Min(editor.Height, editor.MaxHeight);
            if (editor.ShowDialog() == true && !SetGameProfiles(editor.Settings)) ShowProfileSaveWarning();
        }
        finally
        {
            IsProfileEditorOpen = false;
            ProfileEditorVisibilityChanged?.Invoke(false);
        }
    }

    private static void ShowProfileSaveWarning() => MessageBox.Show(
        "Настройки применены, но сохранить их не удалось. После перезапуска они могут сброситься.",
        "Luma Island", MessageBoxButton.OK, MessageBoxImage.Warning);
}
