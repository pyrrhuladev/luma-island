using System.Windows;
using System.Windows.Media;

namespace Luma.Island;

public partial class MainWindow
{
    public double CollapsedWindowWidth => Appearance.Compact ? 352 : 408;
    private double CollapsedSurfaceWidth => Appearance.Compact ? 320 : 376;

    public void SetCompactMode(bool enabled)
    {
        VerifyAccess();
        if (Appearance.Compact == enabled) return;
        CompleteTransition();
        Appearance = Appearance with { Compact = enabled };
        ApplyComponentVisibility();
        OnMediaChanged(_lastMedia ?? _media.Current);
        SaveAppearance();
    }

    private void ApplyCompactDensity()
    {
        bool compact = Appearance.Compact;
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
        CompactArrowColumn.Width = new GridLength(compact ? 14 : 16);
        foreach (var button in new[] { CompactPreviousButton, CompactPlayButton, CompactNextButton })
        {
            button.Width = compact ? 22 : 24;
            button.Height = compact ? 26 : 28;
        }
        CompactPerformance.Margin = compact ? new Thickness(0, 4, 0, 4) : new Thickness(0, 8, 0, 8);
        CompactCpu.FontSize = CompactGpu.FontSize = CompactMemory.FontSize = CompactFps.FontSize = compact ? 11 : 12;
        CompactCpu.LineHeight = CompactGpu.LineHeight = CompactMemory.LineHeight = compact ? 14 : 16;
        UpdateSurfaceRounding();
    }

    private void UpdateSurfaceRounding()
    {
        bool compact = Appearance.Compact && !_expanded;
        IslandSurface.CornerRadius = new CornerRadius(compact ? 22 : 28);
        GlassSheen.CornerRadius = new CornerRadius(compact ? 21 : 27);
    }
}
