using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Luma.Island;

public partial class CapsuleWidgetPicker : UserControl
{
    private static readonly (string Title, (string Id, string Label)[] Options)[] Groups =
    [
        ("Кадры", [("fps", "FPS"), ("low", "1% Low"), ("frame-time", "Время кадра")]),
        ("Нагрузка и температура", [("cpu", "Нагрузка CPU"), ("cpu-temp", "Температура CPU"),
            ("gpu", "Нагрузка GPU"), ("gpu-temp", "Температура GPU"), ("memory", "Память RAM"), ("memory-temp", "Температура RAM")]),
        ("Мини-графики", [("cpu-chart", "График CPU"), ("gpu-chart", "График GPU"),
            ("memory-chart", "График RAM"), ("fps-chart", "График FPS")]),
        ("Музыка и время", [("music", "Плеер"), ("volume", "Ползунок громкости"),
            ("time", "Часы"), ("date", "Дата"), ("battery", "Батарея")])
    ];

    private static readonly Brush Heading = CreateHeadingBrush();
    private readonly List<(string Id, CheckBox Control)> _options = new();
    internal event Action<string, bool>? WidgetChanged;

    public CapsuleWidgetPicker()
    {
        InitializeComponent();
        foreach (var (title, options) in Groups)
        {
            WidgetGroups.Children.Add(new TextBlock
            {
                Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12,
                Foreground = Heading, Margin = new Thickness(6, 5, 0, 2)
            });
            var group = new UniformGrid { Columns = 2 };
            foreach (var (id, label) in options)
            {
                var option = new CheckBox
                {
                    Tag = id, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                    ToolTip = id == "battery" ? "Батарея скрывается при питании от сети." : null
                };
                AutomationProperties.SetName(option, label);
                option.Click += OnOptionClick;
                _options.Add((id, option));
                group.Children.Add(option);
            }
            WidgetGroups.Children.Add(group);
        }
    }

    internal void UpdateSelection(Func<string, bool> isEnabled)
    {
        foreach (var (id, option) in _options) option.IsChecked = isEnabled(id);
    }

    internal void FocusFirstOption() => Keyboard.Focus(_options[0].Control);

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string id } option) WidgetChanged?.Invoke(id, option.IsChecked == true);
    }

    private static Brush CreateHeadingBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(157, 230, 209));
        brush.Freeze();
        return brush;
    }
}
