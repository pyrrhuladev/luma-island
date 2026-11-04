using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Luma.Island;

public partial class MainWindow
{
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetExtendedWindowStyle(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetExtendedWindowStyle(IntPtr window, int index, IntPtr style);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RefreshWindowFrame(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
