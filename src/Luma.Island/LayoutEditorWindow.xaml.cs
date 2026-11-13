using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Luma.Island.Services;

namespace Luma.Island;

public partial class LayoutEditorWindow : Window
{
    private static readonly (string Kind, string Title, string Detail)[] Components =
    [
        ("time", "Часы", "Текущее время"), ("date", "Дата", "День недели и дата"),
        ("music", "Плеер", "Обложка, трек и управление"), ("volume", "Громкость", "Ползунок и отключение звука"),
        ("cpu", "Нагрузка CPU", "Процент загрузки процессора"), ("gpu", "Нагрузка GPU", "Процент загрузки видеокарты"),
        ("memory", "Память RAM", "Использование оперативной памяти"), ("fps", "FPS", "Частота кадров"),
        ("low", "1% Low", "Редкие медленные кадры"), ("frame-time", "Время кадра", "График плавности игры"),
        ("cpu-temp", "Температура CPU", "Требуется модуль датчиков"), ("gpu-temp", "Температура GPU", "Датчик видеокарты"),
        ("memory-temp", "Температура RAM", "Требуется доступный датчик памяти"),
        ("cpu-chart", "График CPU", "История нагрузки"), ("gpu-chart", "График GPU", "История нагрузки"),
        ("memory-chart", "График RAM", "История использования памяти"), ("fps-chart", "График FPS", "История частоты кадров"),
        ("battery", "Батарея", "Скрывается при питании от сети")
    ];
    private static readonly (string Kind, string Title, string Detail)[] Containers =
    [
        ("row", "Строка", "Компоненты рядом по горизонтали"),
        ("column", "Колонка", "Компоненты друг под другом"),
        ("grid", "Сетка", "Равные колонки и несколько рядов")
    ];
    private HudLayoutNode? _selected;
    private bool _refreshing;
    private bool _allowedToClose;

    internal event Action<string, FrameworkElement, MouseButtonEventArgs>? PaletteDragRequested;
    internal event Action<string>? PaletteKeyboardRequested;
    internal event Action? UndoRequested;
    internal event Action? RedoRequested;
    internal event Action? FinishRequested;
    internal event Action? CancelRequested;
    internal event Action? DuplicateRequested;
    internal event Action? RemoveRequested;
    internal event Action<HudLayoutNode>? PropertiesChanged;
    internal event Action? PropertyGestureStarted;
    internal event Action? PropertyGestureCompleted;
    internal event Action<KeyEventArgs>? EditorKeyPressed;

    public LayoutEditorWindow(bool inspector = false)
    {
        InitializeComponent();
        if (inspector)
        {
            Title = "Свойства HUD · Luma";
            PanelHeading.Text = "Свойства";
            PanelCaption.Text = "Выберите компонент или контейнер.";
            PaletteArea.Visibility = Visibility.Collapsed;
            InspectorArea.Visibility = Visibility.Visible;
        }
        else
        {
            foreach (var item in Components) AddCard(ComponentCards, item);
            foreach (var item in Containers) AddCard(ContainerCards, item);
            PaletteSearch.TextChanged += (_, _) => FilterCards();
        }
        UndoButton.Click += (_, _) => UndoRequested?.Invoke();
        RedoButton.Click += (_, _) => RedoRequested?.Invoke();
        FinishButton.Click += (_, _) => FinishRequested?.Invoke();
        CancelButton.Click += (_, _) => CancelRequested?.Invoke();
        DuplicateButton.Click += (_, _) => DuplicateRequested?.Invoke();
        RemoveButton.Click += (_, _) => RemoveRequested?.Invoke();
        foreach (var slider in new[] { ColumnsSlider, GapSlider, PaddingSlider })
        {
            slider.ValueChanged += (_, _) => PublishProperties();
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => PropertyGestureStarted?.Invoke()));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => PropertyGestureCompleted?.Invoke()));
        }
        AlignmentChoice.SelectionChanged += (_, _) => PublishProperties();
        SizingChoice.SelectionChanged += (_, _) => PublishProperties();
        PreviewKeyDown += (_, e) => EditorKeyPressed?.Invoke(e);
        Closing += OnPanelClosing;
    }

    internal static string KindLabel(string kind) => Components.Concat(Containers).FirstOrDefault(item => item.Kind == kind).Title ?? kind;

    private void AddCard(Panel panel, (string Kind, string Title, string Detail) item)
    {
        var labels = new StackPanel { Margin = new Thickness(9, 0, 0, 0) };
        labels.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold });
        labels.Children.Add(new TextBlock { Text = item.Detail, Foreground = new SolidColorBrush(Color.FromRgb(169, 188, 195)), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
        var content = new DockPanel();
        content.Children.Add(new TextBlock { Text = "⠿", FontSize = 18, Foreground = new SolidColorBrush(Color.FromRgb(157, 230, 209)), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(labels);
        var card = new Border
        {
            Tag = item.Kind, Child = content, Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(0, 0, 0, 7),
            Background = new SolidColorBrush(Color.FromRgb(30, 43, 53)), BorderBrush = new SolidColorBrush(Color.FromRgb(54, 76, 83)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Cursor = Cursors.SizeAll,
            Focusable = true, ToolTip = "Перетащить на HUD. Enter добавляет в выбранный контейнер."
        };
        AutomationProperties.SetName(card, "Добавить · " + item.Title);
        card.PreviewMouseLeftButtonDown += (_, e) => PaletteDragRequested?.Invoke(item.Kind, card, e);
        card.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            PaletteKeyboardRequested?.Invoke(item.Kind);
        };
        panel.Children.Add(card);
    }

    private void FilterCards()
    {
        string query = PaletteSearch.Text.Trim();
        foreach (Border card in ComponentCards.Children.Cast<Border>().Concat(ContainerCards.Children.Cast<Border>()))
            card.Visibility = KindLabel((string)card.Tag).Contains(query, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetHistory(bool canUndo, bool canRedo)
    {
        UndoButton.IsEnabled = canUndo;
        RedoButton.IsEnabled = canRedo;
    }

    internal void SetStatus(string? message) => EditorStatus.Text = message ?? string.Empty;

    internal void SetSelection(HudLayoutNode? node, string path, bool isRoot)
    {
        if (_selected == node && SelectionPath.Text == path) return;
        _refreshing = true;
        _selected = node;
        SelectionPath.Text = path;
        SelectionTitle.Text = node is null ? "Выберите компонент" : KindLabel(node.Kind);
        SelectionDescription.Text = node?.IsContainer == true ? "Перетаскивайте за ручку, чтобы переместить группу." : "У каждого экземпляра своё место в раскладке.";
        ContainerProperties.Visibility = node?.IsContainer == true ? Visibility.Visible : Visibility.Collapsed;
        SelectionActions.Visibility = node is not null && !isRoot ? Visibility.Visible : Visibility.Collapsed;
        ColumnsField.Visibility = node?.Kind == "grid" ? Visibility.Visible : Visibility.Collapsed;
        SizingField.Visibility = isRoot ? Visibility.Collapsed : Visibility.Visible;
        if (node is not null)
        {
            ColumnsSlider.Value = node.Columns;
            GapSlider.Value = node.Gap;
            PaddingSlider.Value = node.Padding;
            SelectChoice(AlignmentChoice, node.Alignment);
            SelectChoice(SizingChoice, node.Sizing);
        }
        UpdatePropertyLabels();
        _refreshing = false;
    }

    private static void SelectChoice(ComboBox control, string value) => control.SelectedItem = control.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == value);

    private void PublishProperties()
    {
        UpdatePropertyLabels();
        if (_refreshing || _selected is not { IsContainer: true } node) return;
        var next = node with
        {
            Columns = (int)ColumnsSlider.Value, Gap = GapSlider.Value, Padding = PaddingSlider.Value,
            Alignment = (AlignmentChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? "start",
            Sizing = (SizingChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? "content"
        };
        PropertiesChanged?.Invoke(next);
    }

    private void UpdatePropertyLabels()
    {
        if (ColumnsLabel is null) return;
        ColumnsLabel.Text = "Колонки · " + (int)ColumnsSlider.Value;
        GapLabel.Text = "Интервал · " + (int)GapSlider.Value;
        PaddingLabel.Text = "Внутренний отступ · " + (int)PaddingSlider.Value;
    }

    private void OnPanelClosing(object? sender, CancelEventArgs e)
    {
        if (_allowedToClose) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(() => CancelRequested?.Invoke());
    }

    internal void CloseFromEditor()
    {
        _allowedToClose = true;
        Close();
    }
}
