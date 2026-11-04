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
    private readonly Window _window;
    private readonly MenuItem _visibilityItem;
    private readonly Action _showVisible;
    private readonly Action? _menuOpening;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData _data;
    private AppIcon _icon;
    private bool _usingVersion4;
    private bool _hotkeyAvailable = true;
    private bool _disposed;
    public bool IsRegistered { get; private set; }

    public TrayService(Window window, Action toggleVisible, Action toggleExpanded, Action exit,
        Func<MenuItem>? positionMenuFactory = null, Func<MenuItem>? appearanceMenuFactory = null, Func<MenuItem>? componentsMenuFactory = null,
        Func<MenuItem>? autostartMenuFactory = null, Action? menuOpening = null, Action? showVisible = null)
    {
        window.Dispatcher.VerifyAccess();
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("Сначала покажите окно приложения.");
        _window = window;
        _showVisible = showVisible ?? toggleVisible;
        _menuOpening = menuOpening;
        _menu = new ContextMenu
        {
            PlacementTarget = window, Placement = PlacementMode.MousePoint,
            Background = new SolidColorBrush(Color.FromRgb(28, 32, 38)),
            Foreground = Brushes.WhiteSmoke, BorderBrush = new SolidColorBrush(Color.FromRgb(58, 66, 75))
        };
        _visibilityItem = AddItem(string.Empty, toggleVisible);
        _visibilityItem.InputGestureText = "Ctrl+Alt+Space";
        AddItem("Развернуть / свернуть", toggleExpanded);
        if (positionMenuFactory is not null) _menu.Items.Add(positionMenuFactory());
        if (appearanceMenuFactory is not null) _menu.Items.Add(appearanceMenuFactory());
        if (componentsMenuFactory is not null) _menu.Items.Add(componentsMenuFactory());
        if (autostartMenuFactory is not null) _menu.Items.Add(autostartMenuFactory());
        _menu.Items.Add(new Separator());
        AddItem("Выход", exit);
        _menu.Closed += OnMenuClosed;
        _menu.Opened += OnMenuOpened;
        _window.IsVisibleChanged += OnOwnerVisibilityChanged;
        RefreshVisibilityItem();
        _icon = AppIcon.LoadTrayForWindow(_source.Handle);
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _source.Handle,
            Id = 42, Flags = 1 | 2 | 4 | 0x80, CallbackMessage = CallbackMessage, Icon = _icon.DangerousGetHandle(),
            Tip = "Luma Island · Ctrl+Alt+Space", Info = string.Empty, InfoTitle = string.Empty
        };
        _source.AddHook(WindowMessage);
        AddIcon();
    }

    private void AddIcon()
    {
        RefreshIcon(false);
        IsRegistered = ShellNotifyIcon(0, ref _data);
        _usingVersion4 = false;
        if (!IsRegistered) return; // NIM_ADD; Explorer may still be starting.
        _data.TimeoutOrVersion = 4;
        _usingVersion4 = ShellNotifyIcon(4, ref _data); // NIM_SETVERSION, repeated after Explorer restarts.
    }

    private MenuItem AddItem(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
        return item;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (_taskbarCreated != 0 && unchecked((uint)msg) == _taskbarCreated) AddIcon();
        if (msg is 0x02E0 or 0x007E or 0x001A) RefreshIcon(true); // DPI, display and taskbar/settings changes.
        if (msg != CallbackMessage) return IntPtr.Zero;
        int notification = unchecked((int)lParam.ToInt64()) & 0xFFFF;
        // Version 4 delivers NIN_SELECT for a mouse click and NIN_KEYSELECT
        // for the keyboard. Showing is idempotent, including a double click.
        if ((_usingVersion4 && notification is 0x0400 or 0x0401) ||
            (!_usingVersion4 && notification is 0x0202 or 0x0401)) _showVisible();
        else if (notification is 0x0205 or 0x007B) // WM_RBUTTONUP / WM_CONTEXTMENU
        {
            SetForegroundWindow(hwnd);
            _menu.IsOpen = true;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void OnMenuOpened(object sender, RoutedEventArgs args)
    {
        RefreshVisibilityItem();
        _menuOpening?.Invoke();
    }

    private void OnOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => RefreshVisibilityItem();

    private void RefreshVisibilityItem()
    {
        _visibilityItem.Header = _window.IsVisible ? "Скрыть в трей" : "Показать капсулу";
        _visibilityItem.InputGestureText = _hotkeyAvailable ? "Ctrl+Alt+Space" : string.Empty;
        _visibilityItem.ToolTip = _hotkeyAvailable
            ? "Левый щелчок по значку показывает капсулу. Ctrl+Alt+Space показывает или скрывает её."
            : "Ctrl+Alt+Space недоступна: сочетание занято другой программой или заблокировано Windows. Левый щелчок по значку показывает капсулу.";
    }

    public void SetHotkeyStatus(bool available)
    {
        _source.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _hotkeyAvailable = available;
        RefreshVisibilityItem();
    }

    private void OnMenuClosed(object sender, RoutedEventArgs args)
    {
        if (!_disposed) ShellNotifyIcon(3, ref _data); // NIM_SETFOCUS
        PostMessage(_source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private void RefreshIcon(bool notifyShell)
    {
        int pixels = AppIcon.TrayPixelSize(_source.Handle);
        if (_icon.PixelSize == pixels && !_icon.IsClosed) return;
        AppIcon next = AppIcon.LoadTray(pixels);
        AppIcon previous = _icon;
        _icon = next;
        _data.Icon = next.DangerousGetHandle();
        if (notifyShell && IsRegistered) ShellNotifyIcon(1, ref _data); // NIM_MODIFY copies the new icon.
        previous.Dispose();
    }

    public void Dispose()
    {
        _source.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _menu.IsOpen = false;
        _menu.Closed -= OnMenuClosed;
        _menu.Opened -= OnMenuOpened;
        _window.IsVisibleChanged -= OnOwnerVisibilityChanged;
        ShellNotifyIcon(2, ref _data); // NIM_DELETE
        IsRegistered = false;
        if (!_source.IsDisposed) _source.RemoveHook(WindowMessage);
        _icon.Dispose();
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
