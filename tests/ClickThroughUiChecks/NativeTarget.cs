using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

/// <summary>A separate eight-pixel HWND used only to check OS hit selection, with no global input.</summary>
internal static class NativeTarget
{
    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private static readonly WindowProcedure Procedure = WndProc;

    public static int Run(int x, int y)
    {
        string name = "LumaClickThroughTarget-" + Guid.NewGuid().ToString("N");
        var descriptor = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = GetModuleHandle(null), Background = GetStockObject(4), Name = name
        };
        if (RegisterClassEx(ref descriptor) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            hwnd = CreateWindowEx(0x08000080 | 8, name, name, 0x90000000, x, y, 8, 8,
                IntPtr.Zero, IntPtr.Zero, descriptor.Instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Console.WriteLine(hwnd.ToInt64());
            Console.Out.Flush();
            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            { TranslateMessage(ref message); DispatchMessage(ref message); }
            return result == 0 ? 0 : 1;
        }
        finally
        {
            if (hwnd != IntPtr.Zero && IsWindow(hwnd)) DestroyWindow(hwnd);
            UnregisterClass(name, descriptor.Instance);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == 0x0002) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string Name;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
}
