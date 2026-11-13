using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Luma.Island.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly Dictionary<string, FrameworkElement> _instanceWidgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _primaryInstanceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _temperatureWidgets = new(StringComparer.Ordinal);
    private string? _instanceWidgetRows;

    private void PrepareInstanceWidgets(string[][] rows)
    {
        if (!PresentsComponentInstances)
        {
            ClearInstanceWidgets();
            return;
        }

        foreach (TextBlock widget in _temperatureWidgets.Values) widget.FontSize = 12 * LayoutTextScale;
        string rowsKey = string.Join(';', rows.Select(row => string.Join(',', row)));
        if (_instanceWidgetRows == rowsKey) return;

        // Reuse one existing widget for each kind. Additional instances share its state,
        // but retain their own visuals, controls and drag targets.
        var previous = new Dictionary<string, FrameworkElement>(_instanceWidgets, StringComparer.Ordinal);
        ClearInstanceWidgets();
        foreach (string id in rows.SelectMany(row => row))
        {
            string kind = HudCapsuleLayout.KindOf(id);
            FrameworkElement? source = ClassicPresentedWidget(kind) ?? _temperatureWidgets.GetValueOrDefault(kind);
            if (source is null) continue;

            if (_primaryInstanceIds.TryAdd(kind, id)) _instanceWidgets.Add(id, source);
            else if (previous.TryGetValue(id, out var widget) && widget != source) _instanceWidgets.Add(id, widget);
            else _instanceWidgets.Add(id, CreateLinkedWidget(source));
        }
        _instanceWidgetRows = rowsKey;
    }

    private void ClearInstanceWidgets()
    {
        _instanceWidgets.Clear();
        _primaryInstanceIds.Clear();
        _instanceWidgetRows = null;
    }

    private void ShowStandaloneTemperature(string kind, string text, string tooltip)
    {
        if (!_temperatureWidgets.TryGetValue(kind, out var widget))
        {
            widget = new TextBlock
            {
                FontSize = 12 * LayoutTextScale, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            widget.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            _temperatureWidgets.Add(kind, widget);
        }
        widget.Text = text;
        widget.ToolTip = tooltip;
    }

    private FrameworkElement CreateLinkedWidget(FrameworkElement source)
    {
        FrameworkElement widget = source switch
        {
            TextBlock text => LinkedText(text),
            Grid grid => LinkedGrid(grid),
            StackPanel stack => LinkedStack(stack),
            Border border => new Border
            {
                Child = border.Child is FrameworkElement child ? CreateLinkedWidget(child) : null,
                CornerRadius = border.CornerRadius, BorderThickness = border.BorderThickness,
                BorderBrush = border.BorderBrush, Padding = border.Padding, Background = border.Background
            },
            Canvas plot => LinkedPlot(plot),
            Image image => LinkedImage(image),
            ShapePath path => LinkedPath(path),
            Button button => LinkedButton(button),
            Slider slider => LinkedSlider(slider),
            _ => throw new InvalidOperationException("Unsupported HUD widget element: " + source.GetType().Name)
        };
        LinkValues(widget, source, VisibilityProperty, ToolTipProperty, IsHitTestVisibleProperty, WidthProperty, HeightProperty,
            HorizontalAlignmentProperty, VerticalAlignmentProperty, MarginProperty, ClipProperty, ClipToBoundsProperty,
            OpacityProperty, AutomationProperties.NameProperty);
        Grid.SetRow(widget, Grid.GetRow(source));
        Grid.SetColumn(widget, Grid.GetColumn(source));
        Grid.SetRowSpan(widget, Grid.GetRowSpan(source));
        Grid.SetColumnSpan(widget, Grid.GetColumnSpan(source));
        return widget;
    }

    private static TextBlock LinkedText(TextBlock source)
    {
        var text = new TextBlock
        {
            TextAlignment = source.TextAlignment, TextTrimming = source.TextTrimming, TextWrapping = source.TextWrapping
        };
        LinkValues(text, source, TextBlock.TextProperty, TextBlock.FontFamilyProperty, TextBlock.FontSizeProperty,
            TextBlock.FontWeightProperty, TextBlock.ForegroundProperty, TextBlock.LineHeightProperty,
            Typography.NumeralAlignmentProperty);
        return text;
    }

    private Grid LinkedGrid(Grid source)
    {
        var grid = new Grid { UseLayoutRounding = source.UseLayoutRounding };
        foreach (ColumnDefinition column in source.ColumnDefinitions)
        {
            var copy = new ColumnDefinition();
            LinkValues(copy, column, ColumnDefinition.WidthProperty);
            grid.ColumnDefinitions.Add(copy);
        }
        foreach (RowDefinition row in source.RowDefinitions)
        {
            var copy = new RowDefinition();
            LinkValues(copy, row, RowDefinition.HeightProperty);
            grid.RowDefinitions.Add(copy);
        }
        foreach (FrameworkElement child in source.Children) grid.Children.Add(CreateLinkedWidget(child));
        return grid;
    }

    private StackPanel LinkedStack(StackPanel source)
    {
        var stack = new StackPanel { Orientation = source.Orientation };
        foreach (FrameworkElement child in source.Children) stack.Children.Add(CreateLinkedWidget(child));
        return stack;
    }

    private Viewbox LinkedPlot(Canvas source)
    {
        // The chart points use pixels from their source plot. Reuse its geometry and
        // scale only the plot, so each copy retains its normal heading and labels.
        var canvas = new Canvas { ClipToBounds = true };
        LinkValue(canvas, WidthProperty, source, ActualWidthProperty);
        LinkValue(canvas, HeightProperty, source, ActualHeightProperty);
        foreach (FrameworkElement child in source.Children) canvas.Children.Add(CreateLinkedWidget(child));
        return new Viewbox { Child = canvas, Stretch = Stretch.Fill };
    }

    private static Image LinkedImage(Image source)
    {
        var image = new Image { Stretch = source.Stretch };
        LinkValues(image, source, Image.SourceProperty);
        return image;
    }

    private static ShapePath LinkedPath(ShapePath source)
    {
        var path = new ShapePath
        {
            Stretch = source.Stretch, StrokeThickness = source.StrokeThickness, StrokeStartLineCap = source.StrokeStartLineCap,
            StrokeEndLineCap = source.StrokeEndLineCap, StrokeLineJoin = source.StrokeLineJoin
        };
        LinkValues(path, source, ShapePath.DataProperty);
        foreach (DependencyProperty brush in new[] { ShapePath.FillProperty, ShapePath.StrokeProperty })
        {
            if (BindingOperations.IsDataBound(source, brush))
                path.SetBinding(brush, new Binding(nameof(Button.Foreground))
                { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            else LinkValues(path, source, brush);
        }
        return path;
    }

    private Button LinkedButton(Button source)
    {
        var button = new Button { Style = source.Style, Content = CreateLinkedWidget((FrameworkElement)source.Content) };
        if (source.ReadLocalValue(Control.ForegroundProperty) != DependencyProperty.UnsetValue)
            LinkValues(button, source, Control.ForegroundProperty);
        LinkValues(button, source, IsEnabledProperty);
        // Reuse the existing playback and audio handlers, including their guards.
        button.Click += source.Name switch
        {
            nameof(CompactPreviousButton) => OnPrevious,
            nameof(CompactPlayButton) => OnTogglePlayback,
            nameof(CompactNextButton) => OnNext,
            nameof(MuteButton) => OnMute,
            _ => throw new InvalidOperationException("Unsupported HUD widget button: " + source.Name)
        };
        return button;
    }

    private static Slider LinkedSlider(Slider source)
    {
        var slider = new Slider { Style = source.Style };
        LinkValues(slider, source, IsEnabledProperty);
        LinkValue(slider, Slider.ValueProperty, source, Slider.ValueProperty, BindingMode.TwoWay);
        return slider;
    }

    private static void LinkValues(DependencyObject target, DependencyObject source, params DependencyProperty[] properties)
    {
        foreach (DependencyProperty property in properties) LinkValue(target, property, source, property);
    }

    private static void LinkValue(DependencyObject target, DependencyProperty property, DependencyObject source,
        DependencyProperty sourceProperty, BindingMode mode = BindingMode.OneWay) =>
        BindingOperations.SetBinding(target, property, new Binding { Source = source, Path = new PropertyPath(sourceProperty), Mode = mode });
}
