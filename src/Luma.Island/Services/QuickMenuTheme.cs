using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Luma.Island.Services;

/// <summary>Shared resources for the HUD and tray quick-action menus.</summary>
public static class QuickMenuTheme
{
    private static ResourceDictionary? _resources;
    public static void Apply(ContextMenu menu)
    {
        _resources ??= (ResourceDictionary)XamlReader.Parse(Markup);
        if (!menu.Resources.MergedDictionaries.Contains(_resources)) menu.Resources.MergedDictionaries.Add(_resources);
        menu.Style = (Style)_resources[typeof(ContextMenu)];
    }

    private const string Markup = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="ContextMenu">
            <Setter Property="Background" Value="#172127"/>
            <Setter Property="Foreground" Value="#EDF4F3"/>
            <Setter Property="BorderBrush" Value="#35484C"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="5"/>
            <Setter Property="FontFamily" Value="Segoe UI"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu">
              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                      BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="9" Padding="{TemplateBinding Padding}">
                <ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/>
                </ScrollViewer>
              </Border>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="MenuItem">
            <Setter Property="Foreground" Value="#EDF4F3"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Padding" Value="9,8"/>
            <Setter Property="MinHeight" Value="32"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
              <Grid>
                <Border x:Name="Row" Background="{TemplateBinding Background}" CornerRadius="6" Padding="{TemplateBinding Padding}">
                  <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="10"/></Grid.ColumnDefinitions>
                    <ContentPresenter x:Name="Icon" ContentSource="Icon" VerticalAlignment="Center"/>
                    <Path x:Name="Check" Data="M 1,5 L 4,8 L 11,1" Stroke="#A6E8CF" StrokeThickness="1.6" Width="11" Height="8" Stretch="Uniform"
                          HorizontalAlignment="Left" VerticalAlignment="Center" Visibility="Collapsed"/>
                    <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center" Margin="0,0,18,0"/>
                    <TextBlock Grid.Column="2" Text="{TemplateBinding InputGestureText}" Foreground="#9DB5B9" FontSize="11" VerticalAlignment="Center"/>
                    <Path x:Name="Arrow" Grid.Column="3" Data="M 1,1 L 5,5 L 1,9" Stroke="#9DB5B9" StrokeThickness="1.2" Width="5" Height="9"
                          HorizontalAlignment="Right" VerticalAlignment="Center" Visibility="Collapsed"/>
                  </Grid>
                </Border>
                <Popup x:Name="PART_Popup" Placement="Right" IsOpen="{TemplateBinding IsSubmenuOpen}" Focusable="False"
                       AllowsTransparency="True" PopupAnimation="None" HorizontalOffset="2" VerticalOffset="-5">
                  <Border Background="#172127" BorderBrush="#35484C" BorderThickness="1" CornerRadius="9" Padding="5">
                    <ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter/></ScrollViewer>
                  </Border>
                </Popup>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Row" Property="Background" Value="#2A4245"/></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/><Setter TargetName="Icon" Property="Visibility" Value="Collapsed"/></Trigger>
                <Trigger Property="HasItems" Value="True"><Setter TargetName="Arrow" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Row" Property="Opacity" Value="0.38"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="Separator">
            <Setter Property="Margin" Value="6,4"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="1" Background="#2B3D42"/></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator" BasedOn="{StaticResource {x:Type Separator}}"/>
        </ResourceDictionary>
        """;
}
