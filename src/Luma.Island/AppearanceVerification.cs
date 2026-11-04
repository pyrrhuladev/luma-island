using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island.Services;

namespace Luma.Island;

/// <summary>Exercises the actual appearance menu and renders isolated visual evidence.</summary>
public static class AppearanceVerification
{
    public sealed record Result(bool Passed, int GlassAlpha, int MatteAlpha,
        bool OpaqueText, bool PlacementUnchanged, int MenuItems);

    public static async Task<Result> RunAsync(MainWindow window, string directory, List<string> errors)
    {
        window.VerifyAccess();
        Directory.CreateDirectory(directory);
        int initialErrors = errors.Count;
        var originalPlacement = window.Placement.Settings;
        Rect collapsedBounds = DisplayService.GetWindowBounds(window);
        bool placementUnchanged = true;
        bool opaqueText = false;

        // Make the starting opacity deterministic even when reusing an evidence directory.
        window.SetAppearance(window.Appearance.Mode, .76);
        MenuItem menu = window.CreateAppearanceMenu();
        int menuItems = menu.Items.Count;
        if (!menu.HasItems || menuItems != 19)
            errors.Add("Меню оформления не содержит оба режима, пресеты и настройку прозрачности фона.");

        Click(menu, 0);
        await SettleAsync(window);
        BitmapSource matteCollapsed = window.CapturePreview();
        int matteAlpha = SurfaceAlpha(window, matteCollapsed);
        if (window.Appearance.Mode != IslandAppearance.Matte || matteAlpha != 255)
            errors.Add($"Матовое оформление не стало непрозрачным: alpha={matteAlpha}.");
        if (AlphaAt(matteCollapsed, 0, 0) != 0)
            errors.Add("Снаружи матовой капсулы потеряна прозрачность окна.");
        placementUnchanged &= SameBounds(collapsedBounds, DisplayService.GetWindowBounds(window));

        Click(menu, 1);
        await SettleAsync(window);
        BitmapSource glassCollapsed = window.CapturePreview();
        int glassAlpha = SurfaceAlpha(window, glassCollapsed);
        if (window.Appearance.Mode != IslandAppearance.Glass || Math.Abs(glassAlpha - 194) > 1)
            errors.Add($"Переключатель стекла не установил ожидаемую альфу .76: alpha={glassAlpha}.");
        if (AlphaAt(glassCollapsed, 0, 0) != 0)
            errors.Add("Снаружи стеклянной капсулы потеряна прозрачность окна.");
        Save(glassCollapsed, Path.Combine(directory, "glass-collapsed.png"));
        placementUnchanged &= SameBounds(collapsedBounds, DisplayService.GetWindowBounds(window));

        var presetAlphas = new int[10];
        double[] expectedOpacities = { .90, .76, .65, .50, .35, .25, 1, .40, .15, .05 };
        for (int index = 0; index < presetAlphas.Length; index++)
        {
            Click(menu, index + 3);
            await SettleAsync(window);
            presetAlphas[index] = SurfaceAlpha(window, window.CapturePreview());
            int expectedAlpha = (int)Math.Round(expectedOpacities[index] * 255);
            if (Math.Abs(window.Appearance.GlassOpacity - expectedOpacities[index]) > .0001 ||
                Math.Abs(presetAlphas[index] - expectedAlpha) > 1)
                errors.Add($"Пресет стекла {expectedOpacities[index]:0.00} не применился: alpha={presetAlphas[index]}.");
            placementUnchanged &= SameBounds(collapsedBounds, DisplayService.GetWindowBounds(window));
        }
        for (int i = 1; i < 6; i++)
            if (presetAlphas[i] >= presetAlphas[i - 1]) errors.Add("Пресеты прозрачности не дают последовательно уменьшающуюся альфу.");

        if (menu.Items[14] is not MenuItem { Header: StackPanel sliderPanel } ||
            sliderPanel.Children.Count != 2 || sliderPanel.Children[1] is not Slider slider ||
            slider.Minimum != 0 || slider.Maximum != 95)
            errors.Add("В меню нет доступного ползунка прозрачности от 0 до 95 процентов.");
        else
        {
            foreach (int transparency in new[] { 0, 46, 60, 95 })
            {
                slider.Value = transparency;
                await SettleAsync(window);
                int alpha = SurfaceAlpha(window, window.CapturePreview());
                int expected = (int)Math.Round((1 - transparency / 100d) * 255);
                if (window.Appearance.Mode != IslandAppearance.Glass ||
                    Math.Abs(window.Appearance.GlassOpacity - (1 - transparency / 100d)) > .0001 ||
                    Math.Abs(alpha - expected) > 1)
                    errors.Add($"Ползунок прозрачности {transparency}% не изменил только фон: alpha={alpha}.");
            }
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            placementUnchanged &= SameBounds(collapsedBounds, DisplayService.GetWindowBounds(window));
        }

        Click(menu, 4); // Glass, .76; the default used for comparison.
        await SettleAsync(window);
        window.ToggleExpanded();
        await window.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await SettleAsync(window);
        Rect expandedBounds = DisplayService.GetWindowBounds(window);
        BitmapSource glassExpanded = window.CapturePreview();
        Save(glassExpanded, Path.Combine(directory, "glass-expanded.png"));

        window.SetAppearance(IslandAppearance.Glass, .05);
        await SettleAsync(window);
        BitmapSource transparentExpanded = window.CapturePreview();
        Save(transparentExpanded, Path.Combine(directory, "transparent-expanded.png"));
        if (window.FindName("ExpandedTitle") is TextBlock title)
        {
            Rect titleBounds = title.TransformToAncestor(window).TransformBounds(
                new Rect(new Point(), title.RenderSize));
            opaqueText = window.Opacity == 1 && title.Opacity == 1 &&
                title.Foreground is SolidColorBrush foreground && foreground.Color.A == 255 &&
                CountOpaquePixels(transparentExpanded, titleBounds) > 0;
        }
        if (!opaqueText) errors.Add("Стеклянный фон сделал основной текст полупрозрачным.");

        Click(menu, 0);
        await SettleAsync(window);
        BitmapSource matteExpanded = window.CapturePreview();
        Save(matteExpanded, Path.Combine(directory, "matte-expanded.png"));
        placementUnchanged &= SameBounds(expandedBounds, DisplayService.GetWindowBounds(window));

        Click(menu, 4);
        await SettleAsync(window);
        placementUnchanged &= SameBounds(expandedBounds, DisplayService.GetWindowBounds(window));
        window.ToggleExpanded();
        await window.WaitForTransitionAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await SettleAsync(window);
        Save(window.CapturePreview(), Path.Combine(directory, "glass-collapsed.png"));
        placementUnchanged &= SameBounds(collapsedBounds, DisplayService.GetWindowBounds(window)) &&
            window.Placement.Settings == originalPlacement;
        if (!placementUnchanged) errors.Add("Переключение оформления изменило положение или настройки размещения.");
        if (window.Opacity != 1) errors.Add("Оформление изменило общую непрозрачность окна.");

        Save(CreateComparison(matteExpanded, glassExpanded), Path.Combine(directory, "glass-comparison.png"));
        return new Result(errors.Count == initialErrors, glassAlpha, matteAlpha,
            opaqueText, placementUnchanged, menuItems);
    }

    private static void Click(MenuItem menu, int index)
    {
        if (index >= menu.Items.Count || menu.Items[index] is not MenuItem item)
            throw new InvalidOperationException($"Отсутствует пункт оформления {index}.");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    }

    private static async Task SettleAsync(MainWindow window)
    {
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }

    private static bool SameBounds(Rect first, Rect second) =>
        Math.Abs(first.X - second.X) <= 1 && Math.Abs(first.Y - second.Y) <= 1 &&
        Math.Abs(first.Width - second.Width) <= 1 && Math.Abs(first.Height - second.Height) <= 1;

    private static int AlphaAt(BitmapSource source, int x, int y)
    {
        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static int SurfaceAlpha(MainWindow window, BitmapSource source)
    {
        var surface = (Border)window.FindName("IslandSurface");
        Point point = surface.TransformToAncestor(window).Transform(new Point(surface.ActualWidth / 2, 3));
        return AlphaAt(source, (int)Math.Round(point.X), (int)Math.Round(point.Y));
    }

    private static int CountOpaquePixels(BitmapSource source, Rect logicalBounds)
    {
        int x = Math.Clamp((int)Math.Floor(logicalBounds.X), 0, source.PixelWidth);
        int y = Math.Clamp((int)Math.Floor(logicalBounds.Y), 0, source.PixelHeight);
        int width = Math.Max(0, Math.Min(source.PixelWidth, (int)Math.Ceiling(logicalBounds.Right)) - x);
        int height = Math.Max(0, Math.Min(source.PixelHeight, (int)Math.Ceiling(logicalBounds.Bottom)) - y);
        if (width == 0 || height == 0) return 0;
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(new Int32Rect(x, y, width, height), pixels, stride, 0);
        int count = 0;
        for (int offset = 3; offset < pixels.Length; offset += 4)
            if (pixels[offset] == 255) count++;
        return count;
    }

    private static BitmapSource CreateComparison(BitmapSource matte, BitmapSource glass)
    {
        const int width = 980, height = 750;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brush("#10151B"), null, new Rect(0, 0, width, height));
            Text(drawing, "Luma Island", 22, "#F4F7F9", new Point(22, 16), FontWeights.SemiBold);
            Text(drawing, "Сравнение оформления · прозрачность стекла 24%", 12, "#A9B7C2", new Point(23, 48));
            Text(drawing, "Матовое", 15, "#E8EEF2", new Point(22, 80), FontWeights.SemiBold);
            Text(drawing, "Стекло", 15, "#B5EAD6", new Point(500, 80), FontWeights.SemiBold);
            DrawPanel(drawing, matte, new Rect(22, 108, 458, 300), light: true);
            DrawPanel(drawing, glass, new Rect(500, 108, 458, 300), light: true);
            DrawPanel(drawing, matte, new Rect(22, 426, 458, 300), light: false);
            DrawPanel(drawing, glass, new Rect(500, 426, 458, 300), light: false);
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    private static void DrawPanel(DrawingContext drawing, BitmapSource island, Rect bounds, bool light)
    {
        var clipping = new RectangleGeometry(bounds, 16, 16);
        drawing.PushClip(clipping);
        drawing.DrawRectangle(Brush(light ? "#DFE7EF" : "#17232D"), null, bounds);

        // Identical, known backgrounds make the actual rendered alpha easy to compare.
        drawing.DrawRectangle(Brush(light ? "#C8D8E1" : "#233E4A"), null,
            new Rect(bounds.X + 254, bounds.Y, 204, bounds.Height));
        drawing.DrawEllipse(Brush(light ? "#D7E5D9" : "#263C33"), null,
            new Point(bounds.X + 110, bounds.Y + 192), 162, 142);
        var desktopWindow = new Rect(bounds.X + 48, bounds.Y + 53, 366, 221);
        drawing.DrawRoundedRectangle(Brush(light ? "#F8FAFB" : "#1F2A36"),
            new Pen(Brush(light ? "#B7C7D3" : "#3A4D60"), 1), desktopWindow, 10, 10);
        drawing.DrawRoundedRectangle(Brush(light ? "#E6EDF2" : "#2D3B49"), null,
            new Rect(desktopWindow.X, desktopWindow.Y, desktopWindow.Width, 34), 10, 10);
        for (int row = 0; row < 5; row++)
            drawing.DrawRoundedRectangle(Brush(light ? "#DCE5EB" : "#34495A"), null,
                new Rect(desktopWindow.X + 19, desktopWindow.Y + 56 + row * 26, row % 2 == 0 ? 292 : 224, 5), 2, 2);
        Text(drawing, light ? "Светлый фон" : "Тёмный фон", 11,
            light ? "#435563" : "#B7CAD6", new Point(bounds.X + 14, bounds.Y + 6));

        double scale = Math.Min(1, Math.Min((bounds.Width - 30) / island.PixelWidth,
            (bounds.Height - 25) / island.PixelHeight));
        double imageWidth = island.PixelWidth * scale, imageHeight = island.PixelHeight * scale;
        drawing.DrawImage(island, new Rect(bounds.X + (bounds.Width - imageWidth) / 2,
            bounds.Y + 22, imageWidth, imageHeight));
        drawing.Pop();
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static void Text(DrawingContext drawing, string text, double size, string color,
        Point origin, FontWeight? weight = null)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, Brush(color), 1);
        drawing.DrawText(formatted, origin);
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
