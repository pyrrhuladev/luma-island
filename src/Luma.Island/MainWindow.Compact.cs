using System.Windows;
using System.Windows.Media;

namespace Luma.Island;

public partial class MainWindow
{
    private double BaseCollapsedWindowWidth => Appearance.Compact ? 352 : 408;
    public double CollapsedWindowWidth => ScaledHudDimension(BaseCollapsedWindowWidth);
    private double CollapsedSurfaceWidth => Appearance.Compact ? 320 : 376;

    public void SetCompactMode(bool enabled)
    {
        VerifyAccess();
        if (Appearance.Compact == enabled) return;
        Appearance = Appearance with { Compact = enabled };
        ApplyComponentVisibility();
        OnMediaChanged(_lastMedia ?? _media.Current);
        SaveAppearance();
    }

    private void ApplyCompactDensity()
    {
        bool compact = Appearance.Compact;
        CompactView.Margin = new Thickness(13, compact ? 5 : 6, 13, compact ? 5 : 6);
        CompactContentGrid.Width = compact ? 292 : 348;
        CompactArtworkFrame.Width = CompactArtworkFrame.Height = CompactArtwork.Width = CompactArtwork.Height = compact ? 24 : 29;
        CompactArtworkFrame.CornerRadius = new CornerRadius(compact ? 7 : 9);
        var clip = new RectangleGeometry(new Rect(0, 0, compact ? 24 : 29, compact ? 24 : 29), compact ? 7 : 9, compact ? 7 : 9);
        clip.Freeze();
        CompactArtwork.Clip = clip;
        CompactFallback.FontSize = compact ? 12 : 14;
        CompactMusicText.Margin = compact ? new Thickness(7, 0, 5, 0) : new Thickness(10, 0, 8, 0);
        CompactClock.FontSize = compact ? 12 : 13;
        CompactClock.Margin = compact ? new Thickness(3, 0, 5, 0) : new Thickness(6, 0, 8, 0);
        CompactBattery.FontSize = compact ? 10 : 11;
        CompactBattery.Margin = compact ? new Thickness(2, 0, 5, 0) : new Thickness(4, 0, 8, 0);
        foreach (var button in new[] { CompactPreviousButton, CompactPlayButton, CompactNextButton })
        {
            button.Width = compact ? 22 : 24;
            button.Height = compact ? 26 : 28;
        }
        CompactPerformance.Margin = compact ? new Thickness(0, 4, 0, 4) : new Thickness(0, 6, 0, 6);
        CompactCpu.FontSize = CompactGpu.FontSize = CompactMemory.FontSize = compact ? 11 : 12;
        CompactFps.FontSize = compact ? 28 : 34;
        CompactOnePercentLow.FontSize = Components.Fps ? compact ? 16 : 18 : compact ? 20 : 24;
        CompactLowPanel.Margin = new Thickness(Components.Fps ? 12 : 0, 0, 0, 0);
        CompactCpu.LineHeight = CompactGpu.LineHeight = CompactMemory.LineHeight = compact ? 14 : 16;
        UpdateSurfaceRounding();
    }

    private void UpdateSurfaceRounding()
    {
        double radius = Appearance.Compact ? 14 : 16;
        IslandSurface.CornerRadius = new CornerRadius(radius);
        GlassSheen.CornerRadius = new CornerRadius(radius - 1);
    }
}
