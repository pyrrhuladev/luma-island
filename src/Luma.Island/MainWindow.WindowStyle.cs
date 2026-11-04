using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Luma.Island;

public partial class MainWindow
{
    private const long ClickThroughStyleMask = 0x20L | 0x08000000L; // TRANSPARENT, NOACTIVATE.
    private const uint PreserveWindowFrameFlags = 0x01 | 0x02 | 0x04 | 0x10 | 0x20;

    protected override void OnSourceInitialized(EventArgs args)
    {
        base.OnSourceInitialized(args);
        if (ShowInTaskbar) return; // Explicit --ui-check windows keep normal task-switcher behavior.
        IntPtr window = new WindowInteropHelper(this).Handle;
        IntPtr previous = GetExtendedWindowStyle(window, -20); // GWL_EXSTYLE
        IntPtr style = new((previous.ToInt64() | 0x80L) & ~0x40000L); // TOOLWINDOW, without APPWINDOW.
        if (style == previous) return;
        Marshal.SetLastPInvokeError(0);
        if (SetExtendedWindowStyle(window, -20, style) == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            App.Log(new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось убрать капсулу из Alt+Tab."));
            return;
        }
        // Apply before the first Show. Keep placement, topmost order, activation and input behavior.
        if (!RefreshWindowFrame(window, IntPtr.Zero, 0, 0, 0, 0, 0x01 | 0x02 | 0x04 | 0x10 | 0x20))
            App.Log(new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось обновить стиль капсулы."));
    }

    private bool TrySetClickThroughWindowStyle(bool enabled)
    {
        VerifyAccess();
        IntPtr window = new WindowInteropHelper(this).Handle;
        if (window == IntPtr.Zero)
        {
            App.Log(new InvalidOperationException("Нельзя изменить пропуск кликов до создания окна Luma."));
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        IntPtr previous = GetExtendedWindowStyle(window, -20);
        int error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0)
        {
            App.Log(new Win32Exception(error, "Не удалось прочитать режим ввода Luma."));
            return false;
        }
        if (enabled && (previous.ToInt64() & 0x80000L) == 0)
        {
            App.Log(new InvalidOperationException("Для пропуска кликов требуется слоистое окно Luma."));
            return false;
        }

        long bits = previous.ToInt64();
        IntPtr desired = new(enabled ? bits | ClickThroughStyleMask : bits & ~ClickThroughStyleMask);
        if (desired == previous) return true;
        Marshal.SetLastPInvokeError(0);
        if (SetExtendedWindowStyle(window, -20, desired) == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            App.Log(new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось изменить пропуск кликов Luma."));
            return false;
        }

        // Layered-window hit testing uses TRANSPARENT across processes; changing only
        // HTTRANSPARENT would continue the search within this UI thread instead.
        if (RefreshWindowFrame(window, IntPtr.Zero, 0, 0, 0, 0, PreserveWindowFrameFlags)) return true;

        error = Marshal.GetLastPInvokeError();
        App.Log(new Win32Exception(error, "Не удалось применить пропуск кликов Luma; возвращаем прежний режим."));
        Marshal.SetLastPInvokeError(0);
        if (SetExtendedWindowStyle(window, -20, previous) == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
            App.Log(new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось вернуть прежний стиль ввода Luma."));
        else if (!RefreshWindowFrame(window, IntPtr.Zero, 0, 0, 0, 0, PreserveWindowFrameFlags))
            App.Log(new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось обновить прежний стиль ввода Luma."));
        return false;
    }

    private bool TryReadClickThroughWindowStyle(out bool enabled)
    {
        enabled = false;
        IntPtr window = new WindowInteropHelper(this).Handle;
        if (window == IntPtr.Zero) return false;
        Marshal.SetLastPInvokeError(0);
        IntPtr style = GetExtendedWindowStyle(window, -20);
        if (style == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0) return false;
        enabled = (style.ToInt64() & 0x20L) != 0;
        return true;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetExtendedWindowStyle(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetExtendedWindowStyle(IntPtr window, int index, IntPtr style);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RefreshWindowFrame(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
