using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Luma.Island.Services;
using Microsoft.Win32;

namespace Luma.Island;

/// <summary>Edits a local draft. The caller saves Settings only after an accepted dialog.</summary>
public partial class GameProfilesWindow : Window
{
    private readonly ObservableCollection<ProfileDraft> _profiles;
    private readonly HudProfileSettings _currentHud;
    public GameProfileSettings Settings { get; private set; }

    public GameProfilesWindow(GameProfileSettings settings, HudProfileSettings currentHud, string? activeProfileId = null)
    {
        Settings = GameProfileStore.Normalize(settings);
        _currentHud = GameProfileStore.NormalizeSnapshot(currentHud);
        _profiles = new ObservableCollection<ProfileDraft>(Settings.Profiles.Select(profile => new ProfileDraft(profile,
            string.Equals(profile.Id, activeProfileId, StringComparison.OrdinalIgnoreCase))));
        InitializeComponent();
        AutoSwitchCheckBox.IsChecked = Settings.Enabled;
        ProfileList.ItemsSource = _profiles;
        ProfileList.SelectedItem = _profiles.FirstOrDefault(profile => profile.Active) ?? _profiles.FirstOrDefault();
        RefreshSelection();
    }

    private ProfileDraft? Selected => ProfileList.SelectedItem as ProfileDraft;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSelection();

    private void RefreshSelection()
    {
        if (ProfileEditor is null) return;
        ProfileEditor.DataContext = Selected;
        ProfileEditor.Visibility = Selected is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyEditorMessage.Visibility = Selected is null ? Visibility.Visible : Visibility.Collapsed;
        NoProfilesMessage.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveProfileButton.IsEnabled = Selected is not null;
        AddProfileButton.IsEnabled = _profiles.Count < GameProfileStore.MaxProfiles;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count >= GameProfileStore.MaxProfiles) return;
        string? path = SelectExecutable(null);
        if (path is null) return;
        ProfileDraft? existing = _profiles.FirstOrDefault(profile => string.Equals(profile.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ProfileList.SelectedItem = existing;
            StatusText.Text = "Для этой игры уже есть профиль.";
            return;
        }
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Length > 80) name = name[..80];
        var draft = new ProfileDraft(new GameProfile(Guid.NewGuid().ToString("D"), name, path, _currentHud), false);
        _profiles.Add(draft);
        ProfileList.SelectedItem = draft;
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
        StatusText.Text = "Добавлена игра с текущим HUD. Нажмите «Сохранить», чтобы применить изменения.";
        RefreshSelection();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Selected is not ProfileDraft selected) return;
        int index = ProfileList.SelectedIndex;
        _profiles.Remove(selected);
        if (_profiles.Count > 0) ProfileList.SelectedIndex = Math.Min(index, _profiles.Count - 1);
        StatusText.Text = "Профиль удалён из списка. Изменение применится после сохранения.";
        RefreshSelection();
    }

    private void OnChangeExecutable(object sender, RoutedEventArgs e)
    {
        if (Selected is not ProfileDraft selected) return;
        string? path = SelectExecutable(selected.ExecutablePath);
        if (path is null) return;
        if (_profiles.Any(profile => !ReferenceEquals(profile, selected) && string.Equals(profile.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "Для выбранного .exe уже есть другой профиль.";
            return;
        }
        selected.ExecutablePath = path;
        StatusText.Text = "Исполняемый файл изменён в черновике.";
    }

    private string? SelectExecutable(string? current)
    {
        var picker = new OpenFileDialog
        {
            Title = "Выберите .exe самой игры", Filter = "Приложения Windows (*.exe)|*.exe",
            CheckFileExists = true, Multiselect = false
        };
        if (current is not null)
        {
            picker.InitialDirectory = Path.GetDirectoryName(current);
            picker.FileName = Path.GetFileName(current);
        }
        if (picker.ShowDialog(this) != true) return null;
        if (GameProfileStore.TryNormalizeExecutablePath(picker.FileName, out string path)) return path;
        StatusText.Text = "Выберите исполняемый файл игры с расширением .exe.";
        return null;
    }

    private void OnCopyCurrentHud(object sender, RoutedEventArgs e)
    {
        if (Selected is not ProfileDraft selected) return;
        selected.Snapshot = _currentHud;
        StatusText.Text = "Для профиля выбран текущий HUD. Нажмите «Сохранить», чтобы записать его.";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var result = new List<GameProfile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProfileDraft draft in _profiles)
        {
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                ProfileList.SelectedItem = draft;
                StatusText.Text = "Укажите название профиля.";
                ProfileNameBox.Focus();
                return;
            }
            if (!GameProfileStore.TryNormalizeExecutablePath(draft.ExecutablePath, out string path) || !paths.Add(path))
            {
                ProfileList.SelectedItem = draft;
                StatusText.Text = "Каждой игре нужен отдельный .exe без повторений в списке.";
                return;
            }
            result.Add(new GameProfile(draft.Id, draft.Name.Trim(), path, GameProfileStore.NormalizeSnapshot(draft.Snapshot)));
        }
        Settings = GameProfileStore.Normalize(new GameProfileSettings(AutoSwitchCheckBox.IsChecked == true, result.ToArray()));
        DialogResult = true;
    }

    private sealed class ProfileDraft : INotifyPropertyChanged
    {
        private string _name;
        private string _executablePath;
        private HudProfileSettings _snapshot;
        public string Id { get; }
        public bool Active { get; }
        public string DisplayName => (string.IsNullOrWhiteSpace(Name) ? Path.GetFileNameWithoutExtension(ExecutablePath) : Name.Trim())
            + (Active ? " · активен" : string.Empty);
        public string Name
        {
            get => _name;
            set { _name = value; Changed(nameof(Name)); Changed(nameof(DisplayName)); }
        }
        public string ExecutablePath
        {
            get => _executablePath;
            set { _executablePath = value; Changed(nameof(ExecutablePath)); Changed(nameof(DisplayName)); }
        }
        public HudProfileSettings Snapshot
        {
            get => _snapshot;
            set { _snapshot = value; Changed(nameof(Snapshot)); Changed(nameof(Summary)); }
        }
        public string Summary
        {
            get
            {
                AppearanceSettings appearance = Snapshot.Appearance;
                string surface = appearance.Mode == IslandAppearance.Glass
                    ? $"Стекло · прозрачность {(1 - appearance.GlassOpacity) * 100:0}%" : "Матовое оформление";
                string size = appearance.Compact ? "Компактный" : "Обычный";
                return PlacementLabel(Snapshot.Placement.Placement) + "\n" + surface + "\n" + size + " · масштаб "
                    + (appearance.Scale * 100).ToString("0", CultureInfo.CurrentCulture) + "%\n" + ComponentSummary(Snapshot.Components);
            }
        }
        public ProfileDraft(GameProfile profile, bool active)
        {
            Id = profile.Id; _name = profile.Name; _executablePath = profile.ExecutablePath; _snapshot = profile.Settings; Active = active;
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private static string PlacementLabel(IslandPlacement placement) => placement switch
    {
        IslandPlacement.TopLeft => "Сверху слева", IslandPlacement.TopCenter => "Сверху по центру", IslandPlacement.TopRight => "Сверху справа",
        IslandPlacement.MiddleLeft => "Слева по центру", IslandPlacement.Center => "В центре экрана", IslandPlacement.MiddleRight => "Справа по центру",
        IslandPlacement.BottomLeft => "Снизу слева", IslandPlacement.BottomCenter => "Снизу по центру", IslandPlacement.BottomRight => "Снизу справа",
        _ => "Произвольное положение"
    };

    private static string ComponentSummary(ComponentSettings settings)
    {
        var labels = new List<string>();
        if (settings.Music) labels.Add("музыка");
        if (settings.Volume) labels.Add("громкость");
        if (settings.Time) labels.Add("часы");
        if (settings.Date) labels.Add("дата");
        if (settings.Battery) labels.Add("батарея");
        if (settings.Cpu) labels.Add("CPU");
        if (settings.Gpu) labels.Add("GPU");
        if (settings.Memory) labels.Add("RAM");
        if (settings.Fps) labels.Add("FPS");
        if (settings.OnePercentLow) labels.Add("1% Low");
        if (settings.FrameTime) labels.Add("время кадра");
        if (settings.CpuTemperature) labels.Add("температура CPU");
        if (settings.GpuTemperature) labels.Add("температура GPU");
        if (settings.MemoryTemperature) labels.Add("температура RAM");
        if (settings.Charts) labels.Add("мини-графики");
        return labels.Count == 0 ? "Показатели выключены" : "Компоненты: " + string.Join(", ", labels);
    }
}
