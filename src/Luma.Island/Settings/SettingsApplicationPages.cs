using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Luma.Island.Services;
using Microsoft.Win32;

namespace Luma.Island.Settings;

/// <summary>Application pages edit a local draft. Creating, visiting and cancelling pages never persists preferences.</summary>
public sealed class SettingsApplicationPages
{
    private readonly SettingsEditorContext _context;
    private readonly Func<HudProfileSettings> _currentHud;
    private readonly Action _changed;
    private readonly Func<Window> _owner;
    private readonly Dictionary<string, FrameworkElement> _controls = new(StringComparer.Ordinal);
    private readonly ObservableCollection<ProfileRow> _profiles;
    private readonly Dictionary<IslandPlacement, ToggleButton> _presets = new();
    private PlacementSettings _placement;
    private IReadOnlyList<DisplayInfo> _displays;
    private bool _autoProfiles, _autostartRegistered, _syncing;
    private FrameworkElement? _positionPage, _profilesPage, _applicationPage;
    private ComboBox? _monitor;
    private Slider? _x, _y;
    private CheckBox? _locked, _auto, _autostart;
    private ListBox? _profileList;
    private TextBox? _profileName;
    private TextBlock? _placementMode, _monitorDetails, _profileCount;
    private TextBlock? _profilePath, _profileStatus, _profileActive, _profileNotice;
    private FrameworkElement? _profileEmpty, _profileDetails, _profileActiveBadge;
    private Button? _addProfile, _removeProfile, _copyProfile, _changeExecutable;
    private static readonly Brush CardFill = Brush("#1B232B");
    private static readonly Brush CardBorder = Brush("#2D3944");
    private static readonly Brush InsetFill = Brush("#141B22");
    private static readonly Brush MutedText = Brush("#A6B4C4");
    private static readonly Brush Accent = Brush("#9DE6D1");
    private static readonly Brush WarningText = Brush("#F2C296");
    private static readonly Style AnchorStyle = CreateAnchorStyle();

    public SettingsApplicationPages(SettingsEditorContext context, Func<HudProfileSettings> getCurrentHudDraft,
        Action onChanged, Func<Window> owner)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _currentHud = getCurrentHudDraft ?? throw new ArgumentNullException(nameof(getCurrentHudDraft));
        _changed = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _placement = PlacementStore.Normalize(context.Placement);
        _displays = context.Displays.ToArray();
        var profiles = GameProfileStore.Normalize(context.Profiles);
        _autoProfiles = profiles.Enabled;
        _profiles = new ObservableCollection<ProfileRow>(profiles.Profiles.Select(profile => new ProfileRow(profile)));
        foreach (var row in _profiles) row.PropertyChanged += OnProfileChanged;
        _autostartRegistered = context.Autostart.Registered;
    }

    public FrameworkElement? FindControl(string name) => _controls.TryGetValue(name, out var value) ? value : null;

    public SettingsApplicationDraft Capture() => new(_placement,
        GameProfileStore.Normalize(new(_autoProfiles, _profiles.Select(row => row.Snapshot()).ToArray())),
        _autostartRegistered, _autostartRegistered != _context.Autostart.Registered);

    public string? Validate()
    {
        if (_profiles.Count > GameProfileStore.MaxProfiles) return "Слишком много игровых профилей.";
        foreach (var row in _profiles)
        {
            if (row.Name.Any(char.IsControl)) return "В названии профиля не должно быть переносов строк или управляющих символов.";
            if (!GameProfileStore.TryNormalizeExecutablePath(row.ExecutablePath, out _)) return "Укажите исполняемый файл игры с расширением .exe.";
        }
        return null;
    }

    public FrameworkElement CreatePositionPage()
    {
        if (_positionPage is not null) return _positionPage;
        var page = Page();
        var monitorCard = Card("Экран", "Выберите монитор для основного островка.");
        var monitorRow = new Grid();
        monitorRow.ColumnDefinitions.Add(new ColumnDefinition());
        monitorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _monitor = Named(new ComboBox { MinHeight = 36, DisplayMemberPath = nameof(MonitorChoice.Label) },
            "PlacementMonitorCombo", "Монитор для HUD");
        _monitor.SelectionChanged += (_, _) =>
        {
            RefreshMonitorDetails();
            if (_syncing || _monitor.SelectedItem is not MonitorChoice choice || _placement.MonitorId == choice.Id) return;
            _placement = _placement with { MonitorId = choice.Id }; Changed();
        };
        monitorRow.Children.Add(_monitor);
        var refresh = Named(Button("Обновить", () =>
        {
            try
            {
                if (_context.RefreshDisplays is not null) _displays = _context.RefreshDisplays().ToArray();
                FillMonitors();
                PositionStatus("Список экранов обновлён.");
            }
            catch (Exception error) { PositionStatus("Не удалось обновить список экранов. " + error.Message, warning: true); }
        }), "RefreshDisplaysButton", "Обновить список мониторов");
        refresh.Margin = new Thickness(10, 0, 0, 0); refresh.IsEnabled = _context.RefreshDisplays is not null;
        Grid.SetColumn(refresh, 1); monitorRow.Children.Add(refresh); monitorCard.Children.Add(monitorRow);
        _monitorDetails = Caption(string.Empty);
        _monitorDetails.Margin = new Thickness(0, 9, 0, 0);
        monitorCard.Children.Add(_monitorDetails);
        monitorCard.Children.Add(Named(Status(), "PlacementStatus", "Состояние выбора экрана"));
        page.Children.Add(WrapCard(monitorCard)); FillMonitors();

        var anchors = Card("Точка на экране", "Выберите одну из девяти привязок или задайте свободное положение.");
        _placementMode = Caption(string.Empty);
        _placementMode.Foreground = Accent;
        anchors.Children.Add(_placementMode);
        var grid = new UniformGrid { Rows = 3, Columns = 3 };
        var positions = new[] { IslandPlacement.TopLeft, IslandPlacement.TopCenter, IslandPlacement.TopRight,
            IslandPlacement.MiddleLeft, IslandPlacement.Center, IslandPlacement.MiddleRight,
            IslandPlacement.BottomLeft, IslandPlacement.BottomCenter, IslandPlacement.BottomRight };
        var symbols = new[] { "↖", "↑", "↗", "←", "•", "→", "↙", "↓", "↘" };
        for (int index = 0; index < positions.Length; index++)
        {
            var preset = positions[index];
            var button = Named(new ToggleButton { Content = symbols[index], FontSize = 20, MinHeight = 36,
                Margin = new Thickness(3), ToolTip = PlacementLabel(preset), Style = AnchorStyle },
                "PlacementPreset_" + preset, PlacementLabel(preset));
            button.Click += (_, _) => { _placement = _placement with { Placement = preset }; RefreshPosition(); Changed(); };
            _presets.Add(preset, button); grid.Children.Add(button);
        }
        anchors.Children.Add(new Border { Child = grid, Background = InsetFill, BorderBrush = CardBorder,
            BorderThickness = new Thickness(1), Padding = new Thickness(6), CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 330, Margin = new Thickness(0, 3, 0, 8) });
        anchors.Children.Add(Named(Button("Свободное положение", () =>
        {
            _placement = _placement with { Placement = IslandPlacement.Custom }; RefreshPosition(); Changed();
        }), "CustomPlacementButton", "Настроить свободное положение HUD"));
        anchors.Children.Add(Caption("Координаты в процентах учитывают размер HUD и расстояние между краями экрана."));
        _x = PositionSlider(anchors, "X — по горизонтали", "PlacementXSlider", _placement.XFraction * 100, true);
        _y = PositionSlider(anchors, "Y — по вертикали", "PlacementYSlider", _placement.YFraction * 100, false);
        page.Children.Add(WrapCard(anchors));

        var movement = Card("Перетаскивание", "Закрепление защищает оба островка от случайного перемещения мышью.");
        _locked = Named(new CheckBox { Content = "Закрепить положение", IsChecked = _placement.Locked },
            "PlacementLockCheckBox", "Закрепить положение HUD");
        _locked.Checked += (_, _) => ChangeLock(); _locked.Unchecked += (_, _) => ChangeLock();
        movement.Children.Add(_locked);
        page.Children.Add(WrapCard(movement));
        RefreshPosition();
        return _positionPage = page;
    }

    public FrameworkElement CreateProfilesPage()
    {
        if (_profilesPage is not null) return _profilesPage;
        var page = Page();
        var switching = Card("Автоматическое переключение", "Профиль задаёт отдельное положение, состав HUD и предупреждения для игры.");
        _auto = Named(new CheckBox { Content = "Переключать профили игр автоматически", IsChecked = _autoProfiles },
            "ProfilesAutoCheckBox", "Автоматическое переключение игровых профилей");
        _auto.Checked += (_, _) => ChangeAuto(); _auto.Unchecked += (_, _) => ChangeAuto();
        switching.Children.Add(_auto);
        _profileNotice = Named(Status(), "ProfilesDraftNotice", "Изменения маршрутизации профилей");
        switching.Children.Add(_profileNotice);
        page.Children.Add(WrapCard(switching));

        var collection = Card("Игровые профили", "Добавьте исполняемый файл самой игры.");
        var collectionActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _addProfile = Named(Button("Добавить игру…", () => PickGame(null)), "AddProfileButton", "Добавить игру в профили");
        _addProfile.BorderBrush = Accent;
        _addProfile.Foreground = Accent;
        _profileCount = Caption(string.Empty);
        collectionActions.Children.Add(_addProfile);
        collectionActions.Children.Add(Badge(_profileCount));
        collection.Children.Add(collectionActions);
        _profileList = Named(new ListBox { ItemsSource = _profiles, ItemTemplate = CreateProfileTemplate(),
            MinHeight = 58, MaxHeight = 188, Background = InsetFill, BorderBrush = CardBorder },
            "SettingsProfileList", "Игровые профили");
        _profileList.SelectionChanged += (_, _) =>
        {
            ProfileStatus(string.Empty);
            RefreshProfileSelection();
        };
        collection.Children.Add(_profileList);
        var empty = Card("Пока нет игровых профилей", "При добавлении Luma скопирует текущие настройки HUD в профиль игры.");
        _profileEmpty = Inset(empty);
        collection.Children.Add(_profileEmpty);
        page.Children.Add(WrapCard(collection));

        var details = Card("Выбранная игра");
        _profileActive = Named(Caption(string.Empty), "SettingsActiveProfileLabel", "Активный игровой профиль");
        _profileActive.Foreground = Accent;
        _profileActiveBadge = Badge(_profileActive);
        details.Children.Add(_profileActiveBadge);
        details.Children.Add(Label("Название профиля"));
        _profileName = Named(new TextBox { MaxLength = GameProfileStore.MaxNameLength, MinHeight = 34, Margin = new Thickness(0, 0, 0, 8) },
            "SettingsProfileNameBox", "Название выбранного профиля");
        _profileName.SetBinding(TextBox.TextProperty, new Binding(nameof(ProfileRow.Name)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        details.Children.Add(_profileName);
        details.Children.Add(Label("Исполняемый файл"));
        _profilePath = Named(new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Foreground = MutedText, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis },
            "SettingsProfilePath", "Исполняемый файл выбранной игры");
        details.Children.Add(Inset(_profilePath));
        _changeExecutable = Named(Button("Выбрать другой .exe…", () => { if (Selected is { } selected) PickGame(selected.Id); }),
            "ChangeProfileExecutableButton", "Изменить исполняемый файл выбранной игры");
        details.Children.Add(_changeExecutable);
        details.Children.Add(Caption("Копирование заменит настройки выбранного профиля текущим черновиком HUD."));
        var profileActions = new WrapPanel();
        _copyProfile = Named(Button("Скопировать текущий HUD", () => { if (Selected is { } selected) CopyCurrentHud(selected.Id); }),
            "CopyProfileButton", "Скопировать текущий черновик HUD в выбранный профиль");
        _removeProfile = Named(Button("Удалить профиль", () => { if (Selected is { } selected) RemoveGame(selected.Id); }),
            "RemoveProfileButton", "Удалить выбранный профиль из черновика");
        _removeProfile.Foreground = WarningText;
        profileActions.Children.Add(_copyProfile);
        profileActions.Children.Add(_removeProfile);
        details.Children.Add(profileActions);
        _profileDetails = WrapCard(details);
        page.Children.Add(_profileDetails);
        _profileStatus = Named(Status(), "SettingsProfileStatus", "Состояние редактирования профилей");
        page.Children.Add(_profileStatus);
        _profileList.SelectedItem = _profiles.FirstOrDefault(row => row.Id == _context.ActiveProfileId) ?? _profiles.FirstOrDefault();
        RefreshProfileSelection();
        return _profilesPage = page;
    }

    public FrameworkElement CreateApplicationPage()
    {
        if (_applicationPage is not null) return _applicationPage;
        var page = Page();
        var startup = Card("Автозапуск", "Luma появится после входа в Windows, если автозагрузка разрешена системой.");
        _autostart = Named(new CheckBox { Content = "Запускать вместе с Windows", IsChecked = _autostartRegistered,
            IsEnabled = _context.Autostart.Error is null }, "AppAutostartCheckBox", "Автозапуск Luma вместе с Windows");
        _autostart.Checked += (_, _) => ChangeStartup(); _autostart.Unchecked += (_, _) => ChangeStartup();
        startup.Children.Add(_autostart);
        string status = _context.Autostart.Error is { } error ? "Не удалось прочитать автозапуск. " + error
            : _context.Autostart.DisabledByWindows ? "Автозапуск отключён в Windows. Включите его в параметрах автозагрузки Windows."
            : "Изменение автозапуска применится после сохранения.";
        var startupInfo = Named(Caption(status), "AutostartStatusText", "Состояние автозапуска Windows");
        if (_context.Autostart.Error is not null || _context.Autostart.DisabledByWindows) startupInfo.Foreground = WarningText;
        startup.Children.Add(startupInfo);
        var startupStatus = Named(Status(), "StartupActionStatus", "Открытие параметров Windows");
        var openStartup = Named(Button("Автозагрузка в Windows…", () => RunAction(_context.OpenWindowsStartup, startupStatus)),
            "OpenWindowsStartupButton", "Открыть автозагрузку в параметрах Windows");
        openStartup.IsEnabled = _context.OpenWindowsStartup is not null;
        startup.Children.Add(openStartup); startup.Children.Add(startupStatus); page.Children.Add(WrapCard(startup));

        var sensors = Card("Датчики CPU и RAM", "Температуру процессора и памяти читает дополнительный локальный модуль.");
        var sensorStatus = Named(Status(), "SensorActionStatus", "Открытие настройки датчиков");
        var openSensors = Named(Button("Настроить модуль датчиков…", () =>
            RunAction(_context.OpenSensors is null ? null : () => _context.OpenSensors(_owner()), sensorStatus)),
            "OpenSensorsButton", "Открыть настройку модуля датчиков CPU и RAM");
        openSensors.IsEnabled = _context.OpenSensors is not null;
        sensors.Children.Add(openSensors);
        sensors.Children.Add(Caption("Установка и удаление модуля выполняются в отдельном окне, независимо от сохранения этих настроек."));
        sensors.Children.Add(sensorStatus); page.Children.Add(WrapCard(sensors));

        var shortcuts = Card("Горячие клавиши", "Сочетания действуют на оба островка.");
        shortcuts.Children.Add(Shortcut("Показать HUD или скрыть в трей", "Ctrl", "Alt", "Space"));
        shortcuts.Children.Add(Shortcut("Переключить пропуск кликов в игру", "Ctrl", "Alt", "I"));
        shortcuts.Children.Add(Caption("Управление HUD также можно вернуть через значок в трее."));
        string version = typeof(SettingsApplicationPages).Assembly.GetName().Version?.ToString(3) ?? "—";
        var versionBadge = Named(Caption("Luma Island " + version), "SettingsVersionText", "Версия Luma Island");
        shortcuts.Children.Add(Badge(versionBadge));
        page.Children.Add(WrapCard(shortcuts));
        return _applicationPage = page;
    }

    public bool TryAddGame(string path, out string error)
    {
        if (!GameProfileStore.TryNormalizeExecutablePath(path, out string normalized)) { error = "Выберите исполняемый файл игры с расширением .exe."; ProfileStatus(error, warning: true); return false; }
        var existing = _profiles.FirstOrDefault(row => string.Equals(row.ExecutablePath, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { if (_profileList is not null) _profileList.SelectedItem = existing; error = "Для этой игры уже есть профиль."; ProfileStatus(error, warning: true); return false; }
        if (_profiles.Count >= GameProfileStore.MaxProfiles) { error = "Можно сохранить не более " + GameProfileStore.MaxProfiles + " профилей."; ProfileStatus(error, warning: true); return false; }
        string name = Path.GetFileNameWithoutExtension(normalized); if (name.Length > GameProfileStore.MaxNameLength) name = name[..GameProfileStore.MaxNameLength];
        var row = new ProfileRow(new GameProfile(Guid.NewGuid().ToString("D"), name, normalized, GameProfileStore.NormalizeSnapshot(_currentHud())));
        row.PropertyChanged += OnProfileChanged; _profiles.Add(row);
        if (_profileList is not null) _profileList.SelectedItem = row;
        RefreshProfileSelection();
        _profileList?.ScrollIntoView(row);
        Changed(); error = string.Empty; ProfileStatus("Игра добавлена в черновик. Изменения применятся после сохранения."); return true;
    }

    public bool RemoveGame(string id)
    {
        var row = _profiles.FirstOrDefault(item => item.Id == id); if (row is null) return false;
        int index = _profiles.IndexOf(row); row.PropertyChanged -= OnProfileChanged; _profiles.Remove(row);
        if (_profileList is not null && _profiles.Count > 0) _profileList.SelectedIndex = Math.Min(index, _profiles.Count - 1);
        RefreshProfileSelection(); Changed(); ProfileStatus("Профиль удалён из черновика. Изменение применится после сохранения."); return true;
    }

    public bool CopyCurrentHud(string id)
    {
        var row = _profiles.FirstOrDefault(item => item.Id == id); if (row is null) return false;
        row.Hud = GameProfileStore.NormalizeSnapshot(_currentHud()); Changed(); ProfileStatus("Текущий черновик HUD скопирован в профиль."); return true;
    }

    public bool TryChangeGameExecutable(string id, string path, out string error)
    {
        var row = _profiles.FirstOrDefault(item => item.Id == id);
        if (row is null) { error = "Профиль не найден."; return false; }
        if (!GameProfileStore.TryNormalizeExecutablePath(path, out string normalized)) { error = "Выберите файл игры с расширением .exe."; ProfileStatus(error, warning: true); return false; }
        if (_profiles.Any(item => item.Id != id && string.Equals(item.ExecutablePath, normalized, StringComparison.OrdinalIgnoreCase)))
        { error = "Для выбранного файла уже есть другой профиль."; ProfileStatus(error, warning: true); return false; }
        row.ExecutablePath = normalized; RefreshProfileSelection(); error = string.Empty; ProfileStatus("Исполняемый файл изменён в черновике."); return true;
    }

    private void FillMonitors()
    {
        if (_monitor is null) return;
        _syncing = true;
        try
        {
            var choices = new List<MonitorChoice> { new(null, "Основной монитор — автоматически") };
            choices.AddRange(_displays.Select(display => new MonitorChoice(display.Id, display.Label)));
            if (_placement.MonitorId is { } missing && !choices.Any(choice => string.Equals(choice.Id, missing, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new(missing, "Недоступный экран — временно используется основной"));
            _monitor.ItemsSource = choices;
            _monitor.SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice.Id, _placement.MonitorId, StringComparison.OrdinalIgnoreCase));
        }
        finally { _syncing = false; }
        RefreshMonitorDetails();
    }

    private void RefreshMonitorDetails()
    {
        if (_monitorDetails is null || _monitor?.SelectedItem is not MonitorChoice choice) return;
        DisplayInfo? display = choice.Id is null
            ? _displays.FirstOrDefault(item => item.IsPrimary) ?? _displays.FirstOrDefault()
            : _displays.FirstOrDefault(item => string.Equals(item.Id, choice.Id, StringComparison.OrdinalIgnoreCase));
        if (display is null)
        {
            _monitorDetails.Foreground = WarningText;
            _monitorDetails.Text = "Luma использует основной монитор, пока выбранный экран недоступен.";
            return;
        }
        _monitorDetails.Foreground = MutedText;
        _monitorDetails.Text = $"Рабочая область · {display.WorkArea.Width:0} × {display.WorkArea.Height:0} пикселей";
    }

    private Slider PositionSlider(Panel parent, string label, string name, double initial, bool horizontal)
    {
        var header = new Grid { Margin = new Thickness(0, 12, 0, 3) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Label(label));
        var value = Caption(initial.ToString("0.#", CultureInfo.CurrentCulture) + "%");
        value.Foreground = Accent;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1); header.Children.Add(value); parent.Children.Add(header);
        var slider = Named(new Slider { Minimum = 0, Maximum = 100, Value = initial, SmallChange = .1, LargeChange = 1,
            IsMoveToPointEnabled = true, IsSnapToTickEnabled = false }, name, label + ", проценты");
        slider.ValueChanged += (_, _) =>
        {
            value.Text = slider.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%";
            if (_syncing) return;
            double fraction = slider.Value / 100;
            _placement = horizontal ? _placement with { XFraction = KeepEquivalent(_placement.XFraction, fraction) }
                : _placement with { YFraction = KeepEquivalent(_placement.YFraction, fraction) };
            Changed();
        };
        parent.Children.Add(slider); return slider;
    }

    private void RefreshPosition()
    {
        foreach (var pair in _presets) pair.Value.IsChecked = _placement.Placement == pair.Key;
        if (_placementMode is not null) _placementMode.Text = PlacementLabel(_placement.Placement);
        if (_x is not null) _x.IsEnabled = _placement.Placement == IslandPlacement.Custom;
        if (_y is not null) _y.IsEnabled = _placement.Placement == IslandPlacement.Custom;
    }
    private void ChangeLock() { if (_syncing || _locked is null) return; _placement = _placement with { Locked = _locked.IsChecked == true }; Changed(); }
    private void ChangeAuto() { if (_syncing || _auto is null) return; _autoProfiles = _auto.IsChecked == true; RefreshProfileNotice(); Changed(); }
    private void ChangeStartup() { if (_syncing || _autostart is null) return; _autostartRegistered = _autostart.IsChecked == true; Changed(); }
    private ProfileRow? Selected => _profileList?.SelectedItem as ProfileRow;
    private void OnProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProfileRow.ExecutableFileName)) Changed();
    }
    private void RefreshProfileSelection()
    {
        if (_profileList is null) return;
        var selected = Selected;
        bool hasProfiles = _profiles.Count > 0;
        bool isActive = selected is not null && selected.Id == _context.ActiveProfileId;
        _profileList.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        if (_profileCount is not null) _profileCount.Text = $"{_profiles.Count} из {GameProfileStore.MaxProfiles}";
        if (_profileEmpty is not null) _profileEmpty.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
        if (_profileDetails is not null) _profileDetails.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (_profileName is not null) { _profileName.DataContext = selected; _profileName.IsEnabled = selected is not null; }
        if (_profilePath is not null) { _profilePath.Text = selected?.ExecutablePath ?? string.Empty; _profilePath.ToolTip = selected?.ExecutablePath; }
        if (_profileActive is not null) _profileActive.Text = isActive ? "Сейчас используется этот профиль" : string.Empty;
        if (_profileActiveBadge is not null) _profileActiveBadge.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        if (_addProfile is not null) _addProfile.IsEnabled = _profiles.Count < GameProfileStore.MaxProfiles;
        if (_removeProfile is not null) _removeProfile.IsEnabled = selected is not null;
        if (_copyProfile is not null) _copyProfile.IsEnabled = selected is not null;
        if (_changeExecutable is not null) _changeExecutable.IsEnabled = selected is not null;
        RefreshProfileNotice();
    }
    private void RefreshProfileNotice()
    {
        if (_profileNotice is null) return;
        bool removedActive = _context.ActiveProfileId is { } id && !_profiles.Any(row => row.Id == id);
        SetStatus(_profileNotice, removedActive ? "Активный профиль удалён из черновика. После сохранения вернутся общие настройки HUD."
            : !_autoProfiles ? "Автопереключение выключено. Текущий отредактированный HUD сохранится как общий."
            : "Все изменения профилей применятся общей кнопкой «Сохранить».", warning: removedActive);
    }
    private void PickGame(string? id)
    {
        try
        {
            var picker = new OpenFileDialog { Title = "Выберите .exe самой игры", Filter = "Приложения Windows (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
            if (id is not null && _profiles.FirstOrDefault(row => row.Id == id) is { } selected)
            { picker.InitialDirectory = Path.GetDirectoryName(selected.ExecutablePath); picker.FileName = Path.GetFileName(selected.ExecutablePath); }
            if (picker.ShowDialog(_owner()) != true) return;
            if (id is null) TryAddGame(picker.FileName, out _); else TryChangeGameExecutable(id, picker.FileName, out _);
        }
        catch (Exception error) { ProfileStatus("Не удалось выбрать игру. " + error.Message, warning: true); }
    }
    private void PositionStatus(string text, bool warning = false) => SetStatus(FindControl("PlacementStatus") as TextBlock, text, warning);
    private void ProfileStatus(string text, bool warning = false) => SetStatus(_profileStatus, text, warning);
    private void Changed() { if (!_syncing) _changed(); }
    private static void RunAction(Action? action, TextBlock status)
    {
        try { action?.Invoke(); SetStatus(status, string.Empty); }
        catch (Exception error) { SetStatus(status, "Не удалось открыть. " + error.Message, warning: true); }
    }
    private T Named<T>(T control, string name, string accessibleName) where T : FrameworkElement
    { control.Name = name; AutomationProperties.SetName(control, accessibleName); _controls.Add(name, control); return control; }
    private static StackPanel Page() => new() { Margin = new Thickness(0, 0, 2, 0) };

    private static StackPanel Card(string title, string? description = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, description is null ? 10 : 4)
        });
        if (description is not null)
        {
            var intro = Caption(description);
            intro.Margin = new Thickness(0, 0, 0, 11);
            panel.Children.Add(intro);
        }
        return panel;
    }

    private static Border WrapCard(FrameworkElement content) => new()
    {
        Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(12),
        Background = CardFill, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 0, 11)
    };

    private static Border Inset(FrameworkElement content) => new()
    {
        Child = content, Padding = new Thickness(11, 9, 11, 9), CornerRadius = new CornerRadius(8),
        Background = InsetFill, BorderBrush = CardBorder, BorderThickness = new Thickness(1)
    };

    private static Border Badge(TextBlock content)
    {
        content.Margin = new Thickness(0);
        return new Border
        {
            Child = content, Padding = new Thickness(9, 4, 9, 4), CornerRadius = new CornerRadius(6),
            Background = InsetFill, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 3)
        };
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = 13, Foreground = MutedText,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
    };

    private static TextBlock Status()
    {
        var status = Caption(string.Empty);
        status.Visibility = Visibility.Collapsed;
        return status;
    }

    private static void SetStatus(TextBlock? status, string text, bool warning = false)
    {
        if (status is null) return;
        status.Text = text;
        status.Foreground = warning ? WarningText : MutedText;
        status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 7, 0, 5)
    };

    private static Button Button(string text, Action action)
    {
        var button = new Button
        {
            Content = text, MinHeight = 36, Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }

    private static DataTemplate CreateProfileTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(ProfileRow.Name)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        row.AppendChild(name);

        var executable = new FrameworkElementFactory(typeof(TextBlock));
        executable.SetBinding(TextBlock.TextProperty, new Binding(nameof(ProfileRow.ExecutableFileName)));
        executable.SetValue(TextBlock.FontSizeProperty, 11d);
        executable.SetValue(TextBlock.ForegroundProperty, MutedText);
        executable.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        executable.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        executable.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        row.AppendChild(executable);
        return new DataTemplate(typeof(ProfileRow)) { VisualTree = row };
    }

    private static Style CreateAnchorStyle()
    {
        var style = new Style(typeof(ToggleButton));
        style.Setters.Add(new Setter(Control.BackgroundProperty, CardFill));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#E5EDF3")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, CardBorder));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness)) { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content)) { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        border.AppendChild(content);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ToggleButton)) { VisualTree = border }));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#2A3742"))); style.Triggers.Add(hover);
        var selected = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#233C38")));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Accent));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, Accent)); style.Triggers.Add(selected);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#DCFFF5"))); style.Triggers.Add(focus);
        return style;
    }

    private static FrameworkElement Shortcut(string description, params string[] keys)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = description, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        var keyCaps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in keys)
        {
            var keyCap = Badge(new TextBlock { Text = key, FontSize = 11, Foreground = Accent, FontWeight = FontWeights.SemiBold });
            keyCap.Padding = new Thickness(7, 4, 7, 4);
            keyCap.Margin = new Thickness(3, 0, 0, 0);
            keyCaps.Children.Add(keyCap);
        }
        Grid.SetColumn(keyCaps, 1);
        row.Children.Add(keyCaps);
        return row;
    }
    private static double KeepEquivalent(double oldValue, double value) => Math.Abs(oldValue - value) < 1e-10 ? oldValue : value;
    private static string PlacementLabel(IslandPlacement placement) => placement switch
    {
        IslandPlacement.TopLeft => "Сверху слева", IslandPlacement.TopCenter => "Сверху по центру", IslandPlacement.TopRight => "Сверху справа",
        IslandPlacement.MiddleLeft => "Слева по центру", IslandPlacement.Center => "В центре", IslandPlacement.MiddleRight => "Справа по центру",
        IslandPlacement.BottomLeft => "Снизу слева", IslandPlacement.BottomCenter => "Снизу по центру", IslandPlacement.BottomRight => "Снизу справа", _ => "Свободное положение"
    };
    private sealed record MonitorChoice(string? Id, string Label);
    private sealed class ProfileRow : INotifyPropertyChanged
    {
        private string _name, _path;
        public string Id { get; }
        public string Name { get => _name; set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
        public string ExecutablePath
        {
            get => _path;
            set
            {
                if (_path == value) return;
                _path = value;
                PropertyChanged?.Invoke(this, new(nameof(ExecutablePath)));
                PropertyChanged?.Invoke(this, new(nameof(ExecutableFileName)));
            }
        }
        public string ExecutableFileName => Path.GetFileName(ExecutablePath);
        public HudProfileSettings Hud { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public ProfileRow(GameProfile profile) { Id = profile.Id; _name = profile.Name; _path = profile.ExecutablePath; Hud = profile.Settings; }
        public GameProfile Snapshot() => new(Id, Name, ExecutablePath, Hud);
    }
}
