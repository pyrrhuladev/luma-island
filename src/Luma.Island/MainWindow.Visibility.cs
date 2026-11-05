using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Luma.Island;

public partial class MainWindow
{
    private static readonly uint RestoreInstanceMessage = RegisterWindowMessage("LumaIsland.Restore.v1");
    private MenuItem? _trayVisibilityMenu;
    private HwndSource? _visibilitySource;
    public bool TrayHotkeyAvailable { get; private set; } = true;

    private void InitializeTrayVisibility()
    {
        if (_trayVisibilityMenu is not null) return;
        _trayVisibilityMenu = new MenuItem { InputGestureText = "Ctrl+Alt+Space" };
        _trayVisibilityMenu.Click += (_, _) => ToggleTrayVisibility();
        if (IslandSurface.ContextMenu.Items[0] is MenuItem expanded) expanded.Header = "Развернуть / свернуть";
        IslandSurface.ContextMenu.Items.Insert(0, _trayVisibilityMenu);
        IsVisibleChanged += OnTrayVisibilityChanged;
        Closed += OnVisibilityClosed;
        _visibilitySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _visibilitySource?.AddHook(VisibilityWindowMessage);
        RefreshTrayVisibilityMenu();
    }

    public void ToggleTrayVisibility()
    {
        VerifyAccess();
        if (_islandOwner is not null) { _islandOwner.ToggleTrayVisibility(); return; }
        if (_closed) return;
        if (IsVisible) HideToTray(); else RestoreFromTray();
    }

    public void SetTrayHotkeyStatus(bool available)
    {
        VerifyAccess();
        TrayHotkeyAvailable = available;
        RefreshTrayVisibilityMenu();
    }

    public void HideToTray()
    {
        VerifyAccess();
        if (_islandOwner is not null) { _islandOwner.HideToTray(); return; }
        if (_closed) return;
        CompleteTransition();
        IslandSurface.ContextMenu.IsOpen = false;
        Hide(); // Keep the HWND, hotkey registration and native notification icon alive.
        RefreshTrayVisibilityMenu();
    }

    public void RestoreFromTray()
    {
        VerifyAccess();
        if (_islandOwner is not null) { _islandOwner.RestoreFromTray(); return; }
        if (_closed) return;
        CompleteTransition();
        IslandSurface.ContextMenu.IsOpen = false;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        _placement?.Apply(); // Reapply the saved monitor anchor after changes while hidden.
        RefreshTrayVisibilityMenu();
    }

    /// <summary>Ask an existing instance on the current desktop to show its capsule.</summary>
    public static bool RequestRestoreExistingInstance() => RestoreInstanceMessage != 0 &&
        PostMessage(new IntPtr(0xFFFF), RestoreInstanceMessage, IntPtr.Zero, IntPtr.Zero);

    private IntPtr VisibilityWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (RestoreInstanceMessage != 0 && unchecked((uint)message) == RestoreInstanceMessage)
        {
            RestoreForInteraction();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnTrayVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => RefreshTrayVisibilityMenu();

    private void RefreshTrayVisibilityMenu()
    {
        if (_trayVisibilityMenu is null) return;
        _trayVisibilityMenu.Header = IsVisible ? "Скрыть в трей" : "Показать капсулу";
        _trayVisibilityMenu.InputGestureText = TrayHotkeyAvailable ? "Ctrl+Alt+Space" : string.Empty;
        _trayVisibilityMenu.ToolTip = TrayHotkeyAvailable
            ? "Показать или скрыть капсулу: Ctrl+Alt+Space. Значок Luma останется в трее."
            : "Ctrl+Alt+Space недоступна: сочетание занято другой программой или заблокировано Windows. Используйте значок Luma в трее.";
    }

    private void OnVisibilityClosed(object? sender, EventArgs args)
    {
        IsVisibleChanged -= OnTrayVisibilityChanged;
        Closed -= OnVisibilityClosed;
        if (_visibilitySource is { IsDisposed: false }) _visibilitySource.RemoveHook(VisibilityWindowMessage);
        _visibilitySource = null;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
