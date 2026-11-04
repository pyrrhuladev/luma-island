using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private static readonly DropShadowEffect StrongGlassTextShadow = CreateGlassTextShadow(.85);
    private AppearanceStore _appearanceStore = null!;
    private AppearanceSettings _savedAppearance = AppearanceSettings.Default;
    public AppearanceSettings Appearance { get; private set; } = AppearanceSettings.Default;

    private void InitializeAppearance(AppearanceStore store)
    {
        _appearanceStore = store;
        Appearance = store.Load();
        _savedAppearance = Appearance;
        ApplyAppearance();
    }

    public void SetAppearance(IslandAppearance mode, double? opacity = null)
    {
        VerifyAccess();
        CompleteTransition();
        Appearance = AppearanceStore.Normalize(Appearance with { Mode = mode, GlassOpacity = opacity ?? Appearance.GlassOpacity });
        ApplyAppearance();
        SaveAppearance();
    }

    private void SaveAppearance()
    {
        if (Appearance == _savedAppearance) return;
        if (_appearanceStore.Save(Appearance)) _savedAppearance = Appearance;
        else System.Windows.MessageBox.Show(
            "Оформление изменено, но сохранить выбор не удалось.", "Luma Island", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ApplyAppearance()
    {
        bool glass = Appearance.Mode == IslandAppearance.Glass;
        // A flat translucent surface keeps the game visible without a blur pass or sheen layer.
        GlassSheen.Visibility = Visibility.Collapsed;
        Resources["GlassTextShadow"] = glass && Appearance.GlassOpacity <= .50 ? StrongGlassTextShadow : null;
        if (glass)
        {
            byte alpha = (byte)Math.Round(Appearance.GlassOpacity * 255);
            var fill = new SolidColorBrush(Color.FromArgb(alpha, 18, 23, 28));
            fill.Freeze();
            Resources["SurfaceFill"] = fill;
            SetBrush("SurfaceStroke", "#00000000");
            SetBrush("TextPrimary", "#FFFFFF");
            SetBrush("TextSecondary", "#F0F4F8");
            SetBrush("Muted", "#E4EBF2");
            SetBrush("Subtle", "#DCE6EF");
            SetBrush("ButtonHover", "#38000000");
            SetBrush("ButtonPressed", "#60000000");
            SetBrush("AlbumSurface", "#D925332E");
            SetBrush("VolumeTrack", "#70DCE7EF");
        }
        else
        {
            SetBrush("SurfaceFill", "#12171C");
            SetBrush("SurfaceStroke", "#00000000");
            SetBrush("TextPrimary", "#F0F3F5");
            SetBrush("TextSecondary", "#C7CFD5");
            SetBrush("Muted", "#989FA8");
            SetBrush("Subtle", "#7F8A94");
            SetBrush("ButtonHover", "#293038");
            SetBrush("ButtonPressed", "#343D45");
            SetBrush("AlbumSurface", "#25332E");
            SetBrush("VolumeTrack", "#343B42");
        }
    }

    private void SetBrush(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Resources[key] = brush;
    }

    private static DropShadowEffect CreateGlassTextShadow(double opacity)
    {
        var shadow = new DropShadowEffect
        {
            Color = Colors.Black,
            Opacity = opacity,
            BlurRadius = 2,
            ShadowDepth = 1
        };
        shadow.Freeze();
        return shadow;
    }

    public MenuItem CreateAppearanceMenu()
    {
        var menu = new MenuItem { Header = "Оформление" };
        void Populate()
        {
            menu.Items.Clear();
            var matte = new MenuItem { Header = "Матовое", IsCheckable = true, IsChecked = Appearance.Mode == IslandAppearance.Matte };
            matte.Click += (_, _) => SetAppearance(IslandAppearance.Matte);
            var glass = new MenuItem { Header = "Стекло", IsCheckable = true, IsChecked = Appearance.Mode == IslandAppearance.Glass };
            glass.Click += (_, _) => SetAppearance(IslandAppearance.Glass);
            menu.Items.Add(matte);
            menu.Items.Add(glass);
            menu.Items.Add(new Separator());
            foreach (var preset in new[]
            {
                ("Прозрачность 10%", .90), ("Прозрачность 24%", .76), ("Прозрачность 35%", .65),
                ("Прозрачность 50%", .50), ("Прозрачность 65%", .35), ("Прозрачность 75%", .25),
                ("Прозрачность 0%", 1.0), ("Прозрачность 60%", .40),
                ("Прозрачность 85%", .15), ("Прозрачность 95%", .05)
            })
            {
                var item = new MenuItem { Header = preset.Item1, IsCheckable = true,
                    IsChecked = Appearance.Mode == IslandAppearance.Glass && Math.Abs(Appearance.GlassOpacity - preset.Item2) < .005 };
                item.Click += (_, _) => SetAppearance(IslandAppearance.Glass, preset.Item2);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateTransparencyControl());
            menu.Items.Add(new Separator());
            var compact = new MenuItem { Header = "Компактная капсула", IsCheckable = true, IsChecked = Appearance.Compact };
            compact.Click += (_, _) => SetCompactMode(compact.IsChecked);
            menu.Items.Add(compact);
        }
        Populate();
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) Populate(); };
        menu.SubmenuClosed += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) SaveAppearance(); };
        return menu;
    }

    private MenuItem CreateTransparencyControl()
    {
        double transparency = Appearance.Mode == IslandAppearance.Glass
            ? Math.Round((1 - Appearance.GlassOpacity) * 100) : 0;
        var label = new TextBlock { Text = $"Прозрачность фона: {transparency:0}%", Margin = new Thickness(0, 0, 0, 6) };
        var slider = new Slider
        {
            Name = "BackgroundTransparencySlider", Minimum = 0, Maximum = 95,
            Value = transparency, TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = 5, Width = 230, IsMoveToPointEnabled = true,
            ToolTip = "Меняется только фон. Текст и кнопки остаются непрозрачными."
        };
        System.Windows.Automation.AutomationProperties.SetName(slider, "Прозрачность фона, проценты");
        slider.ValueChanged += (_, _) =>
        {
            double value = Math.Round(slider.Value);
            label.Text = $"Прозрачность фона: {value:0}%";
            Appearance = AppearanceStore.Normalize(Appearance with
            {
                Mode = IslandAppearance.Glass, GlassOpacity = 1 - value / 100
            });
            ApplyAppearance();
        };
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => SaveAppearance()));
        slider.PreviewMouseLeftButtonUp += (_, _) => SaveAppearance();
        slider.KeyUp += (_, _) => SaveAppearance();
        slider.LostKeyboardFocus += (_, _) => SaveAppearance();
        var panel = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
        panel.Children.Add(label);
        panel.Children.Add(slider);
        return new MenuItem { Header = panel, StaysOpenOnClick = true };
    }
}
