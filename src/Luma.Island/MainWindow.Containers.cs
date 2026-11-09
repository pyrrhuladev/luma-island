using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly Dictionary<string, Border> _containerCells = new(StringComparer.Ordinal);
    private readonly Dictionary<FrameworkElement, Panel?> _classicWidgetParents = new();
    private StackPanel? _containerStack;
    private string? _appliedContainerRows;
    private Size? _containerContentSize;

    private FrameworkElement? CapsuleContainerCell(string item) => _containerCells.GetValueOrDefault(item);

    private bool ApplyContainerRows()
    {
        if (PresentationRows is not { } rows)
        {
            RestoreClassicContainers();
            return false;
        }

        string[][] visibleRows = rows
            .Select(row => row.Where(item => PresentedWidget(item)?.Visibility == Visibility.Visible).ToArray())
            .Where(row => row.Length > 0)
            .SelectMany(WrapContainerRow)
            .ToArray();
        string key = string.Join(';', visibleRows.Select(row => string.Join(',', row)));
        if (_containerStack is null)
        {
            _containerStack = new StackPanel { Orientation = Orientation.Vertical };
            CompactView.Children.Add(_containerStack);
            CompactContentGrid.Visibility = Visibility.Collapsed;
        }
        if (_appliedContainerRows != key)
        {
            RebuildContainerRows(visibleRows);
            _appliedContainerRows = key;
        }

        SizeContainerRows(visibleRows);
        return true;
    }

    private IEnumerable<string[]> WrapContainerRow(string[] row)
    {
        if (row.All(HudCapsuleLayout.IsChart)) return row.Chunk(2);
        if (row.All(HudCapsuleLayout.IsMetric)) return row.Chunk(LayoutMetricColumns);
        return [row];
    }

    private void RebuildContainerRows(IEnumerable<string[]> rows)
    {
        foreach (Border cell in _containerCells.Values) cell.Child = null;
        _containerCells.Clear();
        _containerStack!.Children.Clear();
        foreach (string[] row in rows)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (string item in row)
            {
                FrameworkElement widget = PresentedWidget(item)!;
                var parent = widget.Parent as Panel;
                _classicWidgetParents.TryAdd(widget, parent);
                parent?.Children.Remove(widget);
                var cell = new Border { Child = widget, UseLayoutRounding = item is not ("fps" or "low") };
                _containerCells.Add(item, cell);
                panel.Children.Add(cell);
            }
            _containerStack.Children.Add(panel);
        }
    }

    private void SizeContainerRows(string[][] rows)
    {
        double gap = LayoutSpacing;
        double MinimumWidth(string[] row) => row.Sum(ContainerTileWidth) + gap * (row.Length - 1);
        double width = rows.Aggregate(_capsuleId is null ? 348d : 68d, (size, row) => Math.Max(size, MinimumWidth(row)));
        double height = 0;
        _containerStack!.Width = width;
        for (int index = 0; index < rows.Length; index++)
        {
            string[] row = rows[index];
            bool hardwareOnly = row.All(HudCapsuleLayout.IsMetric);
            int flexible = row.Count(item => hardwareOnly || IsFlexibleContainerTile(item));
            double extra = flexible > 0 ? (width - MinimumWidth(row)) / flexible : 0;
            double rowHeight = row.Max(ContainerTileHeight);
            double after = index + 1 < rows.Length ? gap : 0;
            var panel = (StackPanel)_containerStack.Children[index];
            panel.Height = rowHeight;
            panel.Margin = new Thickness(0, 0, 0, after);
            for (int column = 0; column < row.Length; column++)
            {
                string item = row[column];
                Border cell = _containerCells[item];
                cell.Width = ContainerTileWidth(item) + (hardwareOnly || IsFlexibleContainerTile(item) ? extra : 0);
                cell.Margin = new Thickness(0, 0, column + 1 < row.Length ? gap : 0, 0);
            }
            height += rowHeight + after;
        }
        _containerContentSize = new Size(width, height);
    }

    private static bool IsFlexibleContainerTile(string item) => HudCapsuleLayout.IsChart(item) ||
        item is "music" or "volume" or "frame-time";

    private double ContainerTileWidth(string item) => LayoutGrowth * (item switch
    {
        "time" => 52, "battery" => 62, "low" => 72, "fps" => 120, "date" => 132,
        "music" => 184, "volume" => 160, "frame-time" => 208,
        _ => HudCapsuleLayout.IsChart(item) ? 126 : 96
    });

    private double ContainerTileHeight(string item) => item switch
    {
        "fps" or "low" => 50 * LayoutGrowth,
        "time" or "battery" => 24 * LayoutGrowth,
        "date" => HudDateHeight, "music" => HudMusicContentHeight,
        "frame-time" => LayoutFrameTimeHeight, "volume" => HudVolumeHeight,
        _ => HudCapsuleLayout.IsChart(item) ? ChartCardHeight : CompactMetricCellHeight
    };

    private void RestoreClassicContainers()
    {
        if (_containerStack is null) return;
        foreach (Border cell in _containerCells.Values) cell.Child = null;
        foreach (var (widget, parent) in _classicWidgetParents)
            parent?.Children.Add(widget);
        _classicWidgetParents.Clear();
        _containerCells.Clear();
        CompactView.Children.Remove(_containerStack);
        _containerStack = null;
        _containerContentSize = null;
        _appliedContainerRows = null;
        _appliedBlockOrder = _appliedMetricOrder = null;
        _appliedHeaderOrder = string.Empty;
        CompactContentGrid.Visibility = Visibility.Visible;
    }
}
