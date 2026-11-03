using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Luma.Island.Services;

/// <param name="WorkArea">Working rectangle in physical virtual-desktop pixels, including negative monitor origins.</param>
public sealed record DisplayInfo(string Id, string Label, Rect WorkArea, bool IsPrimary);

/// <summary>Monitor geometry for the application's PerMonitorV2-aware window; all positions are physical pixels.</summary>
public static class DisplayService
{
    private const uint MonitorInfoPrimary = 1;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        MonitorEnumProc callback = Enumerate;
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            Trace.WriteLine("Display enumeration: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        GC.KeepAlive(callback);

        if (displays.Count == 0) displays.Add(GetPrimaryFallback());
        return displays.AsReadOnly();

        bool Enumerate(IntPtr monitor, IntPtr deviceContext, ref NativeRect monitorRectangle, IntPtr data)
        {
            var info = CreateMonitorInfo();
            if (GetMonitorInfo(monitor, ref info) && IsUsable(info.WorkArea))
            {
                bool primary = (info.Flags & MonitorInfoPrimary) != 0;
                displays.Add(ToDisplay(info, displays.Count + 1, primary));
            }
            else
            {
                Trace.WriteLine("Monitor information: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
            return true;
        }
    }

    public static DisplayInfo Resolve(string? id, IReadOnlyList<DisplayInfo>? displays = null)
    {
        displays ??= GetDisplays();
        DisplayInfo? primary = null;
        foreach (var display in displays)
        {
            if (id is not null && string.Equals(display.Id, id, StringComparison.Ordinal)) return display;
            if (display.IsPrimary) primary ??= display;
        }
        return primary ?? (displays.Count > 0 ? displays[0] : GetPrimaryFallback());
    }

    public static Rect GetWindowBounds(Window window)
    {
        IntPtr handle = GetWindowHandle(window);
        if (!GetWindowRect(handle, out var rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the island window's bounds.");
        return ToRect(rectangle);
    }

    public static double GetWindowScale(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            uint dpi = GetDpiForWindow(handle);
            if (dpi > 0) return dpi / 96d;
        }
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        return double.IsFinite(scale) && scale > 0 ? scale : 1d;
    }

    public static void MoveWindow(Window window, double physicalX, double physicalY)
    {
        int x = ToNativeCoordinate(physicalX, nameof(physicalX));
        int y = ToNativeCoordinate(physicalY, nameof(physicalY));
        IntPtr handle = GetWindowHandle(window);
        if (!SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot position the island window.");
    }

    public static void MoveToDisplay(Window window, DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        IntPtr handle = GetWindowHandle(window);
        IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var current = CreateMonitorInfo();
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref current) &&
            string.Equals(current.DeviceName, display.Id, StringComparison.Ordinal)) return;

        // Crossing the monitor boundary lets WPF handle WM_DPICHANGED before final placement.
        MoveWindow(window, display.WorkArea.Left, display.WorkArea.Top);
    }

    private static IntPtr GetWindowHandle(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        return new WindowInteropHelper(window).EnsureHandle();
    }

    private static int ToNativeCoordinate(double value, string parameterName)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(parameterName, "The coordinate must be finite.");
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < int.MinValue || rounded > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "The coordinate exceeds the Win32 coordinate range.");
        return checked((int)rounded);
    }

    private static DisplayInfo GetPrimaryFallback()
    {
        IntPtr monitor = MonitorFromPoint(default, MonitorDefaultToPrimary);
        var info = CreateMonitorInfo();
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) && IsUsable(info.WorkArea))
            return ToDisplay(info, 1, true);

        // Last resort: a physical primary-screen rectangle, even if work-area discovery fails.
        int width = Math.Max(1, GetSystemMetrics(0));
        int height = Math.Max(1, GetSystemMetrics(1));
        return new DisplayInfo(@"\\.\DISPLAY1", "Экран 1 (основной)", new Rect(0, 0, width, height), true);
    }

    private static DisplayInfo ToDisplay(MonitorInfoEx info, int fallbackNumber, bool primary)
    {
        string id = string.IsNullOrWhiteSpace(info.DeviceName) ? @"\\.\DISPLAY" + fallbackNumber : info.DeviceName;
        const string prefix = @"\\.\DISPLAY";
        string number = id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : fallbackNumber.ToString();
        if (number.Length == 0) number = fallbackNumber.ToString();
        return new DisplayInfo(id, "Экран " + number + (primary ? " (основной)" : ""), ToRect(info.WorkArea), primary);
    }

    private static bool IsUsable(NativeRect rectangle) => rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top;

    private static Rect ToRect(NativeRect rectangle) => new(rectangle.Left, rectangle.Top,
        Math.Max(0d, (double)rectangle.Right - rectangle.Left), Math.Max(0d, (double)rectangle.Bottom - rectangle.Top));

    private static MonitorInfoEx CreateMonitorInfo() => new()
    {
        Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
        DeviceName = string.Empty
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, ref NativeRect monitorRectangle, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clipRectangle, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);
}
