using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace Luma.Island.Services;

/// <summary>Native notification icon; create after the owner window has been shown.</summary>
public sealed class TrayService : IDisposable
{
    private const int CallbackMessage = 0x8000 + 42;
    private readonly HwndSource _source;
    private readonly ContextMenu _menu;
    private readonly Action _toggleVisible;
    private readonly Action? _menuOpening;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData _data;
    private bool _ownsIcon;
    private bool _disposed;
    public bool IsRegistered { get; private set; }

    public TrayService(Window window, Action toggleVisible, Action toggleExpanded, Action exit,
        Func<MenuItem>? positionMenuFactory = null, Func<MenuItem>? appearanceMenuFactory = null, Func<MenuItem>? componentsMenuFactory = null,
        Func<MenuItem>? autostartMenuFactory = null, Action? menuOpening = null)
    {
        window.Dispatcher.VerifyAccess();
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("Сначала покажите окно приложения.");
        _toggleVisible = toggleVisible;
        _menuOpening = menuOpening;
        _menu = new ContextMenu
        {
            PlacementTarget = window, Placement = PlacementMode.MousePoint,
            Background = new SolidColorBrush(Color.FromRgb(28, 32, 38)),
            Foreground = Brushes.WhiteSmoke, BorderBrush = new SolidColorBrush(Color.FromRgb(58, 66, 75))
        };
        AddItem("Показать / скрыть", toggleVisible);
        AddItem("Развернуть / свернуть", toggleExpanded);
        if (positionMenuFactory is not null) _menu.Items.Add(positionMenuFactory());
        if (appearanceMenuFactory is not null) _menu.Items.Add(appearanceMenuFactory());
        if (componentsMenuFactory is not null) _menu.Items.Add(componentsMenuFactory());
        if (autostartMenuFactory is not null) _menu.Items.Add(autostartMenuFactory());
        _menu.Items.Add(new Separator());
        AddItem("Выход", exit);
        _menu.Closed += OnMenuClosed;
        _menu.Opened += OnMenuOpened;
        var icon = CreateTrayIcon();
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _source.Handle,
            Id = 42, Flags = 1 | 2 | 4 | 0x80, CallbackMessage = CallbackMessage, Icon = icon,
            Tip = "Luma Island · Ctrl+Alt+Space", Info = string.Empty, InfoTitle = string.Empty
        };
        _source.AddHook(WindowMessage);
        AddIcon();
    }

    private void AddIcon()
    {
        IsRegistered = ShellNotifyIcon(0, ref _data);
        if (!IsRegistered) return; // NIM_ADD; Explorer may still be starting.
        _data.TimeoutOrVersion = 4;
        ShellNotifyIcon(4, ref _data); // NIM_SETVERSION, repeated after Explorer restarts.
    }

    private void AddItem(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (_taskbarCreated != 0 && unchecked((uint)msg) == _taskbarCreated) AddIcon();
        if (msg != CallbackMessage) return IntPtr.Zero;
        int notification = unchecked((int)lParam.ToInt64()) & 0xFFFF;
        if (notification is 0x0203 or 0x0401) _toggleVisible(); // WM_LBUTTONDBLCLK / NIN_KEYSELECT
        else if (notification is 0x0205 or 0x007B) // WM_RBUTTONUP / WM_CONTEXTMENU
        {
            SetForegroundWindow(hwnd);
            _menu.IsOpen = true;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void OnMenuOpened(object sender, RoutedEventArgs args) => _menuOpening?.Invoke();

    private void OnMenuClosed(object sender, RoutedEventArgs args)
    {
        if (!_disposed) ShellNotifyIcon(3, ref _data); // NIM_SETFOCUS
        PostMessage(_source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr CreateTrayIcon()
    {
        var mask = new byte[128];
        var pixels = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            double dx = x < 10 ? x - 10 : x > 22 ? x - 22 : 0;
            bool inside = dx * dx + (y - 16) * (y - 16) <= 49;
            if (!inside) { mask[y * 4 + x / 8] |= (byte)(0x80 >> (x % 8)); continue; }
            bool dot = ((x - 11) * (x - 11) + (y - 16) * (y - 16) <= 3)
                || ((x - 21) * (x - 21) + (y - 16) * (y - 16) <= 3);
            int offset = (y * 32 + x) * 4;
            pixels[offset] = dot ? (byte)24 : (byte)207;
            pixels[offset + 1] = dot ? (byte)25 : (byte)232;
            pixels[offset + 2] = dot ? (byte)21 : (byte)166;
            pixels[offset + 3] = 255;
        }
        IntPtr icon = CreateIcon(GetModuleHandle(null), 32, 32, 1, 32, mask, pixels);
        _ownsIcon = icon != IntPtr.Zero;
        return _ownsIcon ? icon : LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    public void Dispose()
    {
        _source.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _menu.IsOpen = false;
        _menu.Closed -= OnMenuClosed;
        _menu.Opened -= OnMenuOpened;
        ShellNotifyIcon(2, ref _data); // NIM_DELETE
        IsRegistered = false;
        if (!_source.IsDisposed) _source.RemoveHook(WindowMessage);
        if (_ownsIcon && _data.Icon != IntPtr.Zero) DestroyIcon(_data.Icon);
        _data.Icon = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint command, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr CreateIcon(IntPtr instance, int width, int height, byte planes, byte bitsPixel, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
