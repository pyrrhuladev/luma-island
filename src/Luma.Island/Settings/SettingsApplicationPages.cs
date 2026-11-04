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
    private TextBlock? _profilePath, _profileStatus, _profileActive, _profileNotice;
    private Button? _addProfile, _removeProfile, _copyProfile, _changeExecutable;
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
        page.Children.Add(Caption("Положение изменится после сохранения. Для перетаскивания самой капсулы снимите закрепление."));
        var monitorCard = Card("Экран");
        var monitorRow = new Grid();
        monitorRow.ColumnDefinitions.Add(new ColumnDefinition());
        monitorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _monitor = Named(new ComboBox { MinWidth = 170, MinHeight = 34, DisplayMemberPath = nameof(MonitorChoice.Label) }, "PlacementMonitorCombo", "Монитор для HUD");
        _monitor.SelectionChanged += (_, _) =>
        {
            if (_syncing || _monitor.SelectedItem is not MonitorChoice choice || _placement.MonitorId == choice.Id) return;
            _placement = _placement with { MonitorId = choice.Id }; Changed();
        };
        monitorRow.Children.Add(_monitor);
        var refresh = Named(Button("Обновить", () =>
        {
            try { if (_context.RefreshDisplays is not null) _displays = _context.RefreshDisplays().ToArray(); FillMonitors(); PositionStatus(string.Empty); }
            catch (Exception error) { PositionStatus("Не удалось обновить список экранов: " + error.Message); }
        }), "RefreshDisplaysButton", "Обновить список мониторов");
        refresh.Margin = new Thickness(10, 0, 0, 0); refresh.IsEnabled = _context.RefreshDisplays is not null;
        Grid.SetColumn(refresh, 1); monitorRow.Children.Add(refresh); monitorCard.Children.Add(monitorRow);
        monitorCard.Children.Add(Named(Caption(string.Empty), "PlacementStatus", "Состояние выбора экрана"));
        page.Children.Add(WrapCard(monitorCard)); FillMonitors();

        var anchors = Card("Расположение на экране");
        var grid = new UniformGrid { Rows = 3, Columns = 3, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 8) };
        var positions = new[] { IslandPlacement.TopLeft, IslandPlacement.TopCenter, IslandPlacement.TopRight,
            IslandPlacement.MiddleLeft, IslandPlacement.Center, IslandPlacement.MiddleRight,
            IslandPlacement.BottomLeft, IslandPlacement.BottomCenter, IslandPlacement.BottomRight };
        var symbols = new[] { "↖", "↑", "↗", "←", "•", "→", "↙", "↓", "↘" };
        for (int index = 0; index < positions.Length; index++)
        {
            var preset = positions[index];
            var button = Named(new ToggleButton { Content = symbols[index], FontSize = 21, MinHeight = 42,
                MinWidth = 70, Margin = new Thickness(0, 0, 7, 7), ToolTip = PlacementLabel(preset), Style = AnchorStyle },
                "PlacementPreset_" + preset, PlacementLabel(preset));
            button.Click += (_, _) => { _placement = _placement with { Placement = preset }; RefreshPosition(); Changed(); };
            _presets.Add(preset, button); grid.Children.Add(button);
        }
        anchors.Children.Add(grid);
        anchors.Children.Add(Named(Button("Свободное положение", () =>
        {
            _placement = _placement with { Placement = IslandPlacement.Custom }; RefreshPosition(); Changed();
        }), "CustomPlacementButton", "Настроить свободное положение HUD"));
        anchors.Children.Add(Caption("Доли X и Y задают путь между краями экрана с учётом размера HUD."));
        _x = PositionSlider(anchors, "X — по горизонтали", "PlacementXSlider", _placement.XFraction * 100, true);
        _y = PositionSlider(anchors, "Y — по вертикали", "PlacementYSlider", _placement.YFraction * 100, false);
        _locked = Named(new CheckBox { Content = "Закрепить положение", IsChecked = _placement.Locked, Margin = new Thickness(0, 12, 0, 0) },
            "PlacementLockCheckBox", "Закрепить положение HUD");
        _locked.Checked += (_, _) => ChangeLock(); _locked.Unchecked += (_, _) => ChangeLock();
        anchors.Children.Add(_locked); page.Children.Add(WrapCard(anchors));
        RefreshPosition();
        return _positionPage = page;
    }

    public FrameworkElement CreateProfilesPage()
    {
        if (_profilesPage is not null) return _profilesPage;
        var page = Page();
        _auto = Named(new CheckBox { Content = "Переключать профили игр автоматически", IsChecked = _autoProfiles },
            "ProfilesAutoCheckBox", "Автоматическое переключение игровых профилей");
        _auto.Checked += (_, _) => ChangeAuto(); _auto.Unchecked += (_, _) => ChangeAuto();
        page.Children.Add(_auto);
        page.Children.Add(Caption("У каждой игры — свой HUD, положение и предупреждения. Привязывайте профиль к .exe самой игры, а не к лаунчеру."));
        _profileNotice = Named(Caption(string.Empty), "ProfilesDraftNotice", "Изменения маршрутизации профилей");
        page.Children.Add(_profileNotice);
        var card = Card("Игры");
        _profileList = Named(new ListBox { ItemsSource = _profiles, DisplayMemberPath = nameof(ProfileRow.Name),
            MinHeight = 90, MaxHeight = 180, Margin = new Thickness(0, 0, 0, 10) }, "SettingsProfileList", "Игровые профили");
        _profileList.SelectionChanged += (_, _) => RefreshProfileSelection(); card.Children.Add(_profileList);
        var actions = new WrapPanel();
        _addProfile = Named(Button("Добавить игру…", () => PickGame(null)), "AddProfileButton", "Добавить игру в профили");
        _removeProfile = Named(Button("Удалить", () => { if (Selected is { } selected) RemoveGame(selected.Id); }), "RemoveProfileButton", "Удалить выбранный профиль из черновика");
        actions.Children.Add(_addProfile); actions.Children.Add(_removeProfile); card.Children.Add(actions);
        _profileActive = Named(Caption(string.Empty), "SettingsActiveProfileLabel", "Активный игровой профиль");
        card.Children.Add(_profileActive);
        card.Children.Add(Label("Название"));
        _profileName = Named(new TextBox { MaxLength = GameProfileStore.MaxNameLength, MinHeight = 34, Margin = new Thickness(0, 0, 0, 8) },
            "SettingsProfileNameBox", "Название выбранного профиля");
        _profileName.SetBinding(TextBox.TextProperty, new Binding(nameof(ProfileRow.Name)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        card.Children.Add(_profileName);
        card.Children.Add(Label("Исполняемый файл"));
        _profilePath = Named(Caption(string.Empty), "SettingsProfilePath", "Исполняемый файл выбранной игры"); card.Children.Add(_profilePath);
        _changeExecutable = Named(Button("Выбрать другой .exe…", () => { if (Selected is { } selected) PickGame(selected.Id); }),
            "ChangeProfileExecutableButton", "Изменить исполняемый файл выбранной игры"); card.Children.Add(_changeExecutable);
        _copyProfile = Named(Button("Скопировать текущий HUD", () => { if (Selected is { } selected) CopyCurrentHud(selected.Id); }),
            "CopyProfileButton", "Скопировать текущий черновик HUD в выбранный профиль");
        card.Children.Add(_copyProfile);
        card.Children.Add(Caption("Копируются текущие несохранённые настройки HUD. Общая кнопка «Сохранить» применит изменения всех разделов."));
        _profileStatus = Named(Caption(string.Empty), "SettingsProfileStatus", "Состояние редактирования профилей"); card.Children.Add(_profileStatus);
        page.Children.Add(WrapCard(card));
        _profileList.SelectedItem = _profiles.FirstOrDefault(row => row.Id == _context.ActiveProfileId) ?? _profiles.FirstOrDefault();
        RefreshProfileSelection();
        return _profilesPage = page;
    }

    public FrameworkElement CreateApplicationPage()
    {
        if (_applicationPage is not null) return _applicationPage;
        var page = Page();
        var startup = Card("Запуск приложения");
        _autostart = Named(new CheckBox { Content = "Запускать вместе с Windows", IsChecked = _autostartRegistered,
            IsEnabled = _context.Autostart.Error is null }, "AppAutostartCheckBox", "Автозапуск Luma вместе с Windows");
        _autostart.Checked += (_, _) => ChangeStartup(); _autostart.Unchecked += (_, _) => ChangeStartup();
        startup.Children.Add(_autostart);
        string status = _context.Autostart.Error is { } error ? "Не удалось прочитать автозапуск: " + error
            : _context.Autostart.DisabledByWindows ? "Автозапуск отключён в Windows. Включите его в параметрах автозагрузки Windows."
            : "Изменение автозапуска применится после сохранения.";
        startup.Children.Add(Named(Caption(status), "AutostartStatusText", "Состояние автозапуска Windows"));
        var startupStatus = Named(Caption(string.Empty), "StartupActionStatus", "Открытие параметров Windows");
        var openStartup = Named(Button("Автозагрузка в Windows…", () => RunAction(_context.OpenWindowsStartup, startupStatus)),
            "OpenWindowsStartupButton", "Открыть автозагрузку в параметрах Windows");
        openStartup.IsEnabled = _context.OpenWindowsStartup is not null;
        startup.Children.Add(openStartup); startup.Children.Add(startupStatus); page.Children.Add(WrapCard(startup));

        var sensors = Card("Датчики CPU и RAM");
        sensors.Children.Add(Caption("Температуры CPU и памяти используют необязательный локальный модуль датчиков. Если датчик недоступен, HUD показывает прочерк."));
        var sensorStatus = Named(Caption(string.Empty), "SensorActionStatus", "Открытие настройки датчиков");
        var openSensors = Named(Button("Настроить модуль датчиков…", () =>
            RunAction(_context.OpenSensors is null ? null : () => _context.OpenSensors(_owner()), sensorStatus)),
            "OpenSensorsButton", "Открыть настройку модуля датчиков CPU и RAM");
        openSensors.IsEnabled = _context.OpenSensors is not null;
        sensors.Children.Add(openSensors);
        sensors.Children.Add(Caption("Установка и удаление модуля выполняются в отдельном окне, независимо от сохранения этих настроек."));
        sensors.Children.Add(sensorStatus); page.Children.Add(WrapCard(sensors));

        var shortcuts = Card("Управление во время игры");
        shortcuts.Children.Add(Shortcut("Ctrl + Alt + Space", "Показать HUD или скрыть в трей"));
        shortcuts.Children.Add(Shortcut("Ctrl + Alt + I", "Переключить пропуск кликов в игру"));
        shortcuts.Children.Add(Caption("Пропуск кликов — общая настройка. Вернуть управление HUD можно через значок в трее."));
        string version = typeof(SettingsApplicationPages).Assembly.GetName().Version?.ToString(3) ?? "—";
        shortcuts.Children.Add(Named(Caption("Luma Island " + version), "SettingsVersionText", "Версия Luma Island"));
        page.Children.Add(WrapCard(shortcuts));
        return _applicationPage = page;
    }

    public bool TryAddGame(string path, out string error)
    {
        if (!GameProfileStore.TryNormalizeExecutablePath(path, out string normalized)) { error = "Выберите исполняемый файл игры с расширением .exe."; ProfileStatus(error); return false; }
        var existing = _profiles.FirstOrDefault(row => string.Equals(row.ExecutablePath, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { if (_profileList is not null) _profileList.SelectedItem = existing; error = "Для этой игры уже есть профиль."; ProfileStatus(error); return false; }
        if (_profiles.Count >= GameProfileStore.MaxProfiles) { error = "Можно сохранить не более " + GameProfileStore.MaxProfiles + " профилей."; ProfileStatus(error); return false; }
        string name = Path.GetFileNameWithoutExtension(normalized); if (name.Length > GameProfileStore.MaxNameLength) name = name[..GameProfileStore.MaxNameLength];
        var row = new ProfileRow(new GameProfile(Guid.NewGuid().ToString("D"), name, normalized, GameProfileStore.NormalizeSnapshot(_currentHud())));
        row.PropertyChanged += OnProfileChanged; _profiles.Add(row);
        if (_profileList is not null) _profileList.SelectedItem = row;
        RefreshProfileSelection(); Changed(); error = string.Empty; ProfileStatus("Игра добавлена в черновик. Изменения применятся после сохранения."); return true;
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
        if (!GameProfileStore.TryNormalizeExecutablePath(path, out string normalized)) { error = "Выберите файл игры с расширением .exe."; ProfileStatus(error); return false; }
        if (_profiles.Any(item => item.Id != id && string.Equals(item.ExecutablePath, normalized, StringComparison.OrdinalIgnoreCase)))
        { error = "Для выбранного файла уже есть другой профиль."; ProfileStatus(error); return false; }
        row.ExecutablePath = normalized; RefreshProfileSelection(); error = string.Empty; ProfileStatus("Исполняемый файл изменён в черновике."); return true;
    }

    private void FillMonitors()
    {
        if (_monitor is null) return;
        _syncing = true;
        try
        {
            var choices = new List<MonitorChoice> { new(null, "Основной монитор — автоматически") };
            choices.AddRange(_displays.Select(display => new MonitorChoice(display.Id, display.Label + (display.IsPrimary ? " · основной" : string.Empty))));
            if (_placement.MonitorId is { } missing && !choices.Any(choice => string.Equals(choice.Id, missing, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new(missing, "Недоступный экран — временно используется основной"));
            _monitor.ItemsSource = choices;
            _monitor.SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice.Id, _placement.MonitorId, StringComparison.OrdinalIgnoreCase));
        }
        finally { _syncing = false; }
    }

    private Slider PositionSlider(Panel parent, string label, string name, double initial, bool horizontal)
    {
        var header = new Grid { Margin = new Thickness(0, 12, 0, 3) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Label(label));
        var value = Caption(initial.ToString("0.#", CultureInfo.CurrentCulture) + "%"); Grid.SetColumn(value, 1); header.Children.Add(value); parent.Children.Add(header);
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
        if (_x is not null) _x.IsEnabled = _placement.Placement == IslandPlacement.Custom;
        if (_y is not null) _y.IsEnabled = _placement.Placement == IslandPlacement.Custom;
    }
    private void ChangeLock() { if (_syncing || _locked is null) return; _placement = _placement with { Locked = _locked.IsChecked == true }; Changed(); }
    private void ChangeAuto() { if (_syncing || _auto is null) return; _autoProfiles = _auto.IsChecked == true; RefreshProfileNotice(); Changed(); }
    private void ChangeStartup() { if (_syncing || _autostart is null) return; _autostartRegistered = _autostart.IsChecked == true; Changed(); }
    private ProfileRow? Selected => _profileList?.SelectedItem as ProfileRow;
    private void OnProfileChanged(object? sender, PropertyChangedEventArgs e) => Changed();
    private void RefreshProfileSelection()
    {
        if (_profileList is null) return;
        var selected = Selected;
        if (_profileName is not null) { _profileName.DataContext = selected; _profileName.IsEnabled = selected is not null; }
        if (_profilePath is not null) { _profilePath.Text = selected?.ExecutablePath ?? "Добавьте игру, чтобы сохранить её отдельный HUD."; _profilePath.ToolTip = selected?.ExecutablePath; }
        if (_profileActive is not null) _profileActive.Text = selected is not null && selected.Id == _context.ActiveProfileId ? "Сейчас используется этот профиль" : string.Empty;
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
        _profileNotice.Text = removedActive ? "Активный профиль удалён из черновика. После сохранения вернутся общие настройки HUD."
            : !_autoProfiles ? "Автопереключение выключено. Текущий отредактированный HUD сохранится как общий."
            : "Все изменения профилей применятся общей кнопкой «Сохранить».";
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
        catch (Exception error) { ProfileStatus("Не удалось выбрать игру: " + error.Message); }
    }
    private void PositionStatus(string text) { if (FindControl("PlacementStatus") is TextBlock status) status.Text = text; }
    private void ProfileStatus(string text) { if (_profileStatus is not null) _profileStatus.Text = text; }
    private void Changed() { if (!_syncing) _changed(); }
    private static void RunAction(Action? action, TextBlock status)
    { try { action?.Invoke(); status.Text = string.Empty; } catch (Exception error) { status.Text = "Не удалось открыть: " + error.Message; } }
    private T Named<T>(T control, string name, string accessibleName) where T : FrameworkElement
    { control.Name = name; AutomationProperties.SetName(control, accessibleName); _controls.Add(name, control); return control; }
    private static StackPanel Page() => new() { Margin = new Thickness(0, 0, 4, 0) };
    private static StackPanel Card(string title) { var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) }); return panel; }
    private static Border WrapCard(FrameworkElement content) => new() { Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(10), Background = Brush("#1C272D"), Margin = new Thickness(0, 12, 0, 0) };
    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 12, Foreground = Brush("#AAB8C1"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 4) };
    private static Button Button(string text, Action action) { var button = new Button { Content = text, MinHeight = 34, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left }; button.Click += (_, _) => action(); return button; }
    private static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static Style CreateAnchorStyle()
    {
        var style = new Style(typeof(ToggleButton));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#203038")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#E3EEF0")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#3B525B")));
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
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#2C4249"))); style.Triggers.Add(hover);
        var selected = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#A6E8CF")));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#142A23")));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#A6E8CF"))); style.Triggers.Add(selected);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#D0FFEA"))); style.Triggers.Add(focus);
        return style;
    }
    private static FrameworkElement Shortcut(string key, string description) { var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.Children.Add(new TextBlock { Text = key, Foreground = Brush("#A6E8CF") }); var label = Label(description); label.Margin = new Thickness(0); Grid.SetColumn(label, 1); grid.Children.Add(label); return grid; }
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
        public string ExecutablePath { get => _path; set { if (_path == value) return; _path = value; PropertyChanged?.Invoke(this, new(nameof(ExecutablePath))); } }
        public HudProfileSettings Hud { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public ProfileRow(GameProfile profile) { Id = profile.Id; _name = profile.Name; _path = profile.ExecutablePath; Hud = profile.Settings; }
        public GameProfile Snapshot() => new(Id, Name, ExecutablePath, Hud);
    }
}
