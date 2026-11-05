using System;
using System.Windows;
using System.Windows.Media;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private double _effectiveHudScale = 1;
    private bool _hudScaleInstalled;
    private DisplayInfo? _hudDisplay;
    private double _hudMonitorScale = 1;
    public double HudScale => Appearance.Scale;
    public double EffectiveHudScale => _effectiveHudScale;
    public double ExpandedWindowWidth => ScaledHudDimension(408);

    public void SetHudScale(double scale)
    {
        VerifyAccess();
        if (_closed) return;
        CompleteTransition();
        Appearance = AppearanceStore.Normalize(Appearance with { Scale = scale });
        ApplyHudScale();
        SynchronizeMusicIsland();
        SaveAppearance();
    }

    private void ApplyHudScale() => RefreshComponentSize();

    private void FitHudToDisplay(DisplayInfo display, double monitorScale)
    {
        bool displayChanged = _hudDisplay?.WorkArea != display.WorkArea || Math.Abs(_hudMonitorScale - monitorScale) > .000001;
        _hudDisplay = display;
        _hudMonitorScale = monitorScale;
        if (UpdateHudScaleTransform() || displayChanged) RefreshComponentSize();
    }

    private double ScaledHudDimension(double baseSize)
    {
        if (Math.Abs(EffectiveHudScale - 1) < .000001) return baseSize;
        // Native HWND dimensions are whole monitor pixels. Round the transparent host outward.
        return Math.Ceiling(baseSize * EffectiveHudScale * _hudMonitorScale - .000001) / _hudMonitorScale;
    }

    private bool UpdateHudScaleTransform()
    {
        double effective = HudScale;
        if (_hudDisplay is not null)
            effective = FitScale(HudScale, new Size(Math.Max(408, BaseCollapsedWindowWidth),
                Math.Max(BaseExpandedWindowHeight, BaseCollapsedWindowHeight)), _hudDisplay.WorkArea, _hudMonitorScale);
        if (_hudScaleInstalled && Math.Abs(effective - _effectiveHudScale) < .000001) return false;
        _effectiveHudScale = effective;
        _hudScaleInstalled = true;
        if (Content is FrameworkElement root)
        {
            if (Math.Abs(effective - 1) < .000001) root.LayoutTransform = Transform.Identity;
            else
            {
                var transform = new ScaleTransform(effective, effective);
                transform.Freeze();
                root.LayoutTransform = transform;
            }
        }
        return true;
    }

    private static double FitScale(double requested, Size maximumBaseSize, Rect workArea, double monitorScale)
    {
        if (!double.IsFinite(monitorScale) || monitorScale <= 0) monitorScale = 1;
        double widthLimit = Math.Max(1, workArea.Width / monitorScale - 16) / maximumBaseSize.Width;
        double heightLimit = Math.Max(1, workArea.Height / monitorScale - 16) / maximumBaseSize.Height;
        // Keep the saved preference; the same effective scale fits collapsed and expanded states.
        return Math.Max(.01, Math.Min(requested, Math.Min(widthLimit, heightLimit)));
    }
}
