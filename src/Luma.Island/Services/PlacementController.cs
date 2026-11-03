using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Luma.Island.Services;

public sealed class PlacementController : IDisposable
{
    private readonly Window _window;
    private readonly PlacementStore _store;
    private readonly HwndSource? _source;
    private volatile bool _disposed;
    private bool _applying;
    private volatile bool _dragging;
    private Rect _dragStart;
    private int _queued;
    public PlacementSettings Settings { get; private set; }
    public IReadOnlyList<DisplayInfo> Displays { get; private set; }
    public DisplayInfo CurrentDisplay => DisplayService.Resolve(Settings.MonitorId, Displays);

    public PlacementController(Window window, PlacementStore store)
    {
        _window = window;
        _store = store;
        Settings = store.Load();
        Displays = DisplayService.GetDisplays();
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source?.AddHook(WindowMessage);
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
        Apply();
    }

    public Rect DesiredBounds(double logicalHeight)
    {
        double scale = DisplayService.GetWindowScale(_window);
        return PlacementMath.Bounds(CurrentDisplay.WorkArea,
            new Size(_window.Width * scale, logicalHeight * scale), PlacementMath.Fractions(Settings), 8 * scale);
    }

    public void Apply(double? logicalHeight = null)
    {
        if (_disposed || _applying || _dragging) return;
        _applying = true;
        try
        {
            DisplayService.MoveToDisplay(_window, CurrentDisplay);
            var bounds = DesiredBounds(logicalHeight ?? _window.Height);
            DisplayService.MoveWindow(_window, bounds.X, bounds.Y);
        }
        finally { _applying = false; }
    }

    public void SetPreset(IslandPlacement preset) => Change(Settings with { Placement = preset });
    public void SetMonitor(string id) => Change(Settings with { MonitorId = id });
    public void SetLocked(bool locked) => Change(Settings with { Locked = locked });

    public void BeginDrag()
    {
        _dragStart = DisplayService.GetWindowBounds(_window);
        _dragging = true;
    }
    public void EndDrag()
    {
        _dragging = false;
        if (_disposed) return;
        Rect end = DisplayService.GetWindowBounds(_window);
        if (Math.Abs(end.X - _dragStart.X) > .5 || Math.Abs(end.Y - _dragStart.Y) > .5) CaptureDrag();
    }

    public void CaptureDrag()
    {
        if (Settings.Locked) return;
        Displays = DisplayService.GetDisplays();
        Rect window = DisplayService.GetWindowBounds(_window);
        var display = PlacementMath.NearestDisplay(window, Displays);
        var fractions = PlacementMath.Capture(display.WorkArea, window, 8 * DisplayService.GetWindowScale(_window));
        Change(Settings with { MonitorId = display.Id, Placement = IslandPlacement.Custom,
            XFraction = fractions.X, YFraction = fractions.Y });
    }

    private void Change(PlacementSettings settings)
    {
        Settings = settings;
        Apply();
        if (!_store.Save(settings)) System.Windows.MessageBox.Show(
            "Положение изменено, но сохранить его не удалось. После перезапуска оно может сброситься.",
            "Luma Island", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void QueueRefresh()
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _queued, 1) != 0) return;
        _window.Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (_disposed || _dragging) return;
            try { Displays = DisplayService.GetDisplays(); Apply(); }
            catch (Exception ex) { App.Log(ex); }
        }), DispatcherPriority.Loaded);
    }
    private void DisplaySettingsChanged(object? sender, EventArgs e) => QueueRefresh();
    private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General or UserPreferenceCategory.Window)
            QueueRefresh();
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x02E0 && !_applying) QueueRefresh(); // WM_DPICHANGED
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
        if (_source is { IsDisposed: false }) _source.RemoveHook(WindowMessage);
    }
}
