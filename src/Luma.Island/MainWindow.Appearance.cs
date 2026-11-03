using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private static readonly DropShadowEffect StrongGlassTextShadow = CreateGlassTextShadow(.85);
    private AppearanceStore _appearanceStore = null!;
    public AppearanceSettings Appearance { get; private set; } = AppearanceSettings.Default;

    private void InitializeAppearance(AppearanceStore store)
    {
        _appearanceStore = store;
        Appearance = store.Load();
        ApplyAppearance();
    }

    public void SetAppearance(IslandAppearance mode, double? opacity = null)
    {
        VerifyAccess();
        CompleteTransition();
        Appearance = AppearanceStore.Normalize(Appearance with { Mode = mode, GlassOpacity = opacity ?? Appearance.GlassOpacity });
        ApplyAppearance();
        if (!_appearanceStore.Save(Appearance)) System.Windows.MessageBox.Show(
            "Оформление изменено, но сохранить выбор не удалось.", "Luma Island", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ApplyAppearance()
    {
        bool glass = Appearance.Mode == IslandAppearance.Glass;
        GlassSheen.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        Resources["GlassTextShadow"] = glass && Appearance.GlassOpacity <= .50 ? StrongGlassTextShadow : null;
        if (glass)
        {
            byte alpha = (byte)Math.Round(Appearance.GlassOpacity * 255);
            bool airy = Appearance.GlassOpacity < .72;
            Resources["SurfaceFill"] = Gradient(
                airy ? Color.FromArgb(alpha, 8, 11, 16) : Color.FromArgb(alpha, 23, 31, 40),
                airy ? Color.FromArgb(alpha, 2, 5, 10) : Color.FromArgb(alpha, 11, 17, 25));
            Resources["SurfaceStroke"] = Gradient(Color.FromArgb(105, 235, 244, 255), Color.FromArgb(48, 164, 203, 216));
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
            SetBrush("SurfaceFill", "#161A1F");
            SetBrush("SurfaceStroke", "#343A41");
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

    private static LinearGradientBrush Gradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
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
                ("Прозрачность 50%", .50), ("Прозрачность 65%", .35), ("Прозрачность 75%", .25)
            })
            {
                var item = new MenuItem { Header = preset.Item1, IsCheckable = true,
                    IsChecked = Appearance.Mode == IslandAppearance.Glass && Math.Abs(Appearance.GlassOpacity - preset.Item2) < .005 };
                item.Click += (_, _) => SetAppearance(IslandAppearance.Glass, preset.Item2);
                menu.Items.Add(item);
            }
        }
        Populate();
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) Populate(); };
        return menu;
    }
}
