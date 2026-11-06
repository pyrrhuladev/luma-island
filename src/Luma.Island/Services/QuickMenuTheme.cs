using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Luma.Island.Services;

internal enum QuickMenuIcon { Hide, Show, Expand, Collapse, Input, Settings, Exit }

/// <summary>Shared resources for the HUD and tray quick-action menus.</summary>
public static class QuickMenuTheme
{
    private static ResourceDictionary? _resources;
    private static readonly Geometry[] Glyphs =
    [
        Glyph("M 2,8 V 13 H 14 V 8 M 8,2 V 10 M 5,7 L 8,10 L 11,7"),
        Glyph("M 2,8 V 13 H 14 V 8 M 8,10 V 2 M 5,5 L 8,2 L 11,5"),
        Glyph("M 2,5 L 8,11 L 14,5"),
        Glyph("M 2,11 L 8,5 L 14,11"),
        Glyph("M 4,2 L 13,9 H 9 L 11,13 L 8.5,14 L 6.5,10 L 4,12 Z"),
        Glyph("M 2,4 H 5 M 8,4 H 14 M 5,2 V 6 M 2,8 H 10 M 13,8 H 14 M 10,6 V 10 M 2,12 H 5 M 8,12 H 14 M 5,10 V 14"),
        Glyph("M 8,2 H 3 V 14 H 8 M 6,8 H 14 M 11,5 L 14,8 L 11,11")
    ];
    private static readonly Brush IconInk = Ink(0xBA, 0xC8, 0xD3);
    private static readonly Brush SettingsInk = Ink(0x9D, 0xE6, 0xD1);
    private static readonly Brush ExitInk = Ink(0xE9, 0xAC, 0xA8);

    public static void Apply(ContextMenu menu)
    {
        _resources ??= (ResourceDictionary)XamlReader.Parse(Markup);
        if (!menu.Resources.MergedDictionaries.Contains(_resources)) menu.Resources.MergedDictionaries.Add(_resources);
        menu.Style = (Style)_resources[typeof(ContextMenu)];
    }

    internal static void UpdateVisibility(MenuItem item, bool visible, bool hotkeyAvailable)
    {
        item.Header = visible ? "Скрыть в трей" : "Показать капсулу";
        item.InputGestureText = hotkeyAvailable ? "Ctrl+Alt+Space" : string.Empty;
        SetIcon(item, visible ? QuickMenuIcon.Hide : QuickMenuIcon.Show);
    }

    internal static void UpdateExpansion(MenuItem item, bool expanded)
    {
        item.Header = expanded ? "Свернуть" : "Развернуть";
        SetIcon(item, expanded ? QuickMenuIcon.Collapse : QuickMenuIcon.Expand);
    }

    internal static void SetIcon(MenuItem item, QuickMenuIcon icon)
    {
        Geometry data = Glyphs[(int)icon];
        if (item.Icon is Path path && ReferenceEquals(path.Data, data)) return;
        item.Icon = new Path
        {
            Data = data, Width = 15, Height = 15, Stretch = Stretch.Uniform,
            Stroke = icon switch { QuickMenuIcon.Settings => SettingsInk, QuickMenuIcon.Exit => ExitInk, _ => IconInk },
            StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false, SnapsToDevicePixels = true
        };
    }

    private static Geometry Glyph(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Brush Ink(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private const string Markup = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="ContextMenu">
            <Setter Property="Background" Value="#141A21"/>
            <Setter Property="Foreground" Value="#EDF3F7"/>
            <Setter Property="BorderBrush" Value="#33414E"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="7"/>
            <Setter Property="MinWidth" Value="294"/>
            <Setter Property="FontFamily" Value="Segoe UI"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu">
              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                      BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="13" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                <DockPanel>
                  <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="9,5,9,8" IsHitTestVisible="False">
                    <Border Width="3" Height="11" CornerRadius="1.5" Background="#9DE6D1" Margin="0,0,8,0"/>
                    <TextBlock Text="Luma Island" FontSize="11" FontWeight="SemiBold" Foreground="#B4C3CF"/>
                  </StackPanel>
                  <ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/>
                  </ScrollViewer>
                </DockPanel>
              </Border>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="MenuItem">
            <Setter Property="Foreground" Value="#EDF3F7"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Padding" Value="7,5"/>
            <Setter Property="MinHeight" Value="38"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
              <Grid>
                <Border x:Name="Row" Background="{TemplateBinding Background}" CornerRadius="8" Padding="{TemplateBinding Padding}">
                  <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="28"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                    <Border x:Name="IconPlate" Width="27" Height="27" Background="#202A33" CornerRadius="7" HorizontalAlignment="Left" VerticalAlignment="Center">
                      <Grid>
                        <ContentPresenter x:Name="Icon" ContentSource="Icon" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        <Path x:Name="Check" Data="M 1,5 L 4,8 L 11,1" Stroke="#9DE6D1" StrokeThickness="1.7" Width="12" Height="9" Stretch="Uniform"
                              StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                              HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
                      </Grid>
                    </Border>
                    <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center" Margin="10,0,13,0"/>
                    <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
                      <Border x:Name="ActiveBadge" Background="#263D38" CornerRadius="4" Padding="5,3" Margin="0,0,5,0" Visibility="Collapsed">
                        <TextBlock Text="ВКЛ" Foreground="#9DE6D1" FontSize="10" FontWeight="SemiBold"/>
                      </Border>
                      <Border x:Name="ShortcutBadge" Background="#202A33" BorderBrush="#34424E" BorderThickness="1" CornerRadius="5" Padding="6,2">
                        <TextBlock Text="{TemplateBinding InputGestureText}" Foreground="#B8C7D3" FontSize="11"/>
                      </Border>
                    </StackPanel>
                    <Path x:Name="Arrow" Grid.Column="3" Data="M 1,1 L 5,5 L 1,9" Stroke="#AEBECB" StrokeThickness="1.2" Width="5" Height="9"
                          Margin="8,0,1,0" VerticalAlignment="Center" Visibility="Collapsed"/>
                  </Grid>
                </Border>
                <Popup x:Name="PART_Popup" Placement="Right" IsOpen="{TemplateBinding IsSubmenuOpen}" Focusable="False"
                       AllowsTransparency="True" PopupAnimation="None" HorizontalOffset="2" VerticalOffset="-5">
                  <Border Background="#141A21" BorderBrush="#33414E" BorderThickness="1" CornerRadius="10" Padding="6">
                    <ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter/></ScrollViewer>
                  </Border>
                </Popup>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Row" Property="Background" Value="#253540"/></Trigger>
                <Trigger Property="IsChecked" Value="True">
                  <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                  <Setter TargetName="Icon" Property="Visibility" Value="Collapsed"/>
                  <Setter TargetName="IconPlate" Property="Background" Value="#263D38"/>
                  <Setter TargetName="ActiveBadge" Property="Visibility" Value="Visible"/>
                </Trigger>
                <Trigger Property="InputGestureText" Value=""><Setter TargetName="ShortcutBadge" Property="Visibility" Value="Collapsed"/></Trigger>
                <Trigger Property="HasItems" Value="True"><Setter TargetName="Arrow" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Row" Property="Opacity" Value="0.50"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="Separator">
            <Setter Property="Margin" Value="7,5"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="1" Background="#2D3944"/></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator" BasedOn="{StaticResource {x:Type Separator}}"/>
        </ResourceDictionary>
        """;
}
