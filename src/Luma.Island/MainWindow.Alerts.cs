using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly HudAlertEngine _alertEngine = new();
    private HudAlertView _alertView = HudAlertView.None;
    private DispatcherTimer? _alertExpiryTimer;
    private static readonly Brush QuietWarningBrush = AlertBrush("#F4CA7A");
    private static readonly Brush QuietHighBrush = AlertBrush("#F18A85");
    public HudAlertView AlertView => _alertView;

    public void SetAlertSettings(AlertSettings settings)
    {
        VerifyAccess();
        if (_closed) return;
        Appearance = AppearanceStore.Normalize(Appearance with { Alerts = AlertSettings.Normalize(settings) });
        RefreshAlerts(reset: true);
        SaveAppearance();
        ComponentsChanged?.Invoke(Components);
    }

    public void ApplyAlertPerformance(PerformanceSnapshot snapshot, double? nowSeconds = null)
    {
        VerifyAccess();
        double now = nowSeconds ?? AlertClock();
        if (!PrepareAlerts(now)) return;
        _alertEngine.ObserveTemperatures(snapshot.CpuTemperatureCelsius, snapshot.GpuTemperatureCelsius,
            snapshot.MemoryTemperatureCelsius, now);
        RenderAlerts(_alertEngine.View(now), now, nowSeconds is null);
    }

    public void ApplyAlertFrames(FpsSnapshot snapshot, double? nowSeconds = null)
    {
        VerifyAccess();
        double now = nowSeconds ?? AlertClock();
        if (!PrepareAlerts(now)) return;
        bool available = snapshot.FrameTimeMilliseconds is > 0 and <= 60000 && double.IsFinite(snapshot.FrameTimeMilliseconds.Value);
        _alertEngine.BeginFrameBatch(snapshot.StreamId ?? snapshot.ProcessName, snapshot.HasFrameGap, available, now);
        if (snapshot.Frames is { } frames)
            foreach (var frame in frames) _alertEngine.ObserveFrame(frame.TimeSeconds, frame.Milliseconds, now);
        RenderAlerts(_alertEngine.View(now), now, nowSeconds is null);
    }

    public void RefreshAlerts(bool reset = false, double? nowSeconds = null)
    {
        VerifyAccess();
        if (reset) _alertEngine.Reset();
        double now = nowSeconds ?? AlertClock();
        if (PrepareAlerts(now)) RenderAlerts(_alertEngine.View(now), now, nowSeconds is null);
    }

    public void ResetAlerts()
    {
        VerifyAccess();
        _alertExpiryTimer?.Stop();
        _alertEngine.Reset();
        RenderAlerts(HudAlertView.None, 0, false);
    }

    private bool PrepareAlerts(double now)
    {
        _alertEngine.Configure(Appearance.Alerts, now);
        // Visibility is owned by App's collector gate. The same view can also render an
        // unshown editor preview without creating a collector or a native window.
        if (_closed || !Appearance.Alerts.Enabled)
        {
            ResetAlerts();
            return false;
        }
        return true;
    }

    private void RenderAlerts(HudAlertView view, double now, bool scheduleExpiry)
    {
        // A refreshed sensor reading may retain exactly the same visible text. It still
        // extends freshness, so update the one-shot timeout before the view equality check.
        ScheduleAlertExpiry(view, now, scheduleExpiry);
        if (FindName("QuietAlertIndicator") is not Shape indicator) { _alertView = view; return; }
        if (_alertView == view && indicator.Visibility == (view.Active ? Visibility.Visible : Visibility.Collapsed)) return;
        _alertView = view;
        indicator.Visibility = view.Active ? Visibility.Visible : Visibility.Collapsed;
        indicator.Fill = view.Severity == HudAlertSeverity.High ? QuietHighBrush : QuietWarningBrush;
        indicator.ToolTip = view.Message;
        System.Windows.Automation.AutomationProperties.SetName(indicator,
            view.Active ? "Предупреждение: " + view.Message : "Нет активных предупреждений");
    }

    private void ScheduleAlertExpiry(HudAlertView view, double now, bool scheduleExpiry)
    {
        _alertExpiryTimer?.Stop();
        if (!scheduleExpiry || !view.Active || _closed) return;
        double? expiry = _alertEngine.NextExpirySeconds(now);
        if (!expiry.HasValue) return;
        if (_alertExpiryTimer is null)
        {
            _alertExpiryTimer = new DispatcherTimer(DispatcherPriority.Background);
            _alertExpiryTimer.Tick += (_, _) =>
            {
                _alertExpiryTimer.Stop();
                if (!_closed) RefreshAlerts();
            };
        }
        _alertExpiryTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(expiry.Value - now + .01, .01, 86400));
        _alertExpiryTimer.Start();
    }

    private static double AlertClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private static Brush AlertBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
