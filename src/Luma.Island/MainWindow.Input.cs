using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly List<WeakReference<MenuItem>> _inputMenuItems = new();
    private bool _inputRecoveryInitialized;
    private bool _inputTrayAvailable;
    private string? _inputModeError;
    public bool IsClickThrough { get; private set; }
    public bool InputHotkeyAvailable { get; private set; }
    public bool InputRecoveryAvailable => _inputRecoveryInitialized && (InputHotkeyAvailable || _inputTrayAvailable);
    public event Action<bool>? InputModeChanged;

    /// <summary>Apply a saved input mode only after a real recovery route is available.</summary>
    public void InitializeInputRecovery(bool hotkeyAvailable, bool trayAvailable)
    {
        VerifyAccess();
        if (_closed) return;
        UpdateInputRecovery(hotkeyAvailable, trayAvailable);
        bool requested = Appearance.ClickThrough && InputRecoveryAvailable;
        SetClickThroughCore(requested, persist: false);
        RefreshInputMenus();
    }

    /// <summary>A lost recovery route unlocks the HUD; a returning tray never locks it again.</summary>
    public void UpdateInputRecovery(bool hotkeyAvailable, bool trayAvailable)
    {
        VerifyAccess();
        if (_closed) return;
        InputHotkeyAvailable = hotkeyAvailable;
        _inputTrayAvailable = trayAvailable;
        _inputRecoveryInitialized = true;
        if (IsClickThrough && !InputRecoveryAvailable) SetClickThroughCore(false, persist: false);
        RefreshInputMenus();
    }

    public bool SetClickThrough(bool enabled)
    {
        VerifyAccess();
        if (_islandOwner is not null) return _islandOwner.SetClickThrough(enabled);
        if (_closed) return false;
        if (enabled && !InputRecoveryAvailable)
        {
            _inputModeError = "Не удалось подключить горячую клавишу и значок трея. Управление HUD оставлено доступным.";
            RefreshInputMenus();
            return false;
        }
        return SetClickThroughCore(enabled, persist: true);
    }

    public void ToggleClickThrough() => SetClickThrough(!IsClickThrough);

    /// <summary>An explicit tray selection or second launch always returns interactive controls.</summary>
    public void RestoreForInteraction()
    {
        VerifyAccess();
        if (_islandOwner is not null) { _islandOwner.RestoreForInteraction(); return; }
        if (_closed) return;
        SetClickThrough(false);
        RestoreFromTray();
    }

    private bool SetClickThroughCore(bool enabled, bool persist)
    {
        bool changed = enabled != IsClickThrough;
        // Explicit recovery clears native bits even if an earlier native rollback failed.
        if (changed || !enabled)
        {
            CompleteTransition();
            if (enabled)
            {
                PrepareMusicIslandForClickThrough();
                IslandSurface.ContextMenu.IsOpen = false;
                if (_expanded) { ToggleExpanded(); CompleteTransition(); }
                // Release only this UI thread's captures; never activate or synthesize input to the game.
                if (Mouse.Captured is DependencyObject captured && Window.GetWindow(captured) == this) Mouse.Capture(null);
                if (IsKeyboardFocusWithin) Keyboard.ClearFocus();
            }
            if (!TrySetAllIslandClickThroughStyles(enabled))
            {
                if (TryReadClickThroughWindowStyle(out bool actual) && actual != IsClickThrough)
                {
                    IsClickThrough = actual;
                    InputModeChanged?.Invoke(actual);
                }
                _inputModeError = "Windows не удалось изменить обработку мыши. Повторите переключение через значок трея.";
                RefreshInputMenus();
                return false;
            }
            IsClickThrough = enabled;
        }
        _inputModeError = null;
        if (persist)
        {
            Appearance = Appearance with { ClickThrough = enabled };
            SaveAppearance();
        }
        RefreshInputMenus();
        if (changed) InputModeChanged?.Invoke(IsClickThrough);
        return true;
    }

    public MenuItem CreateInputMenuItem()
    {
        var item = new MenuItem { Header = "Пропускать клики в игру", IsCheckable = true };
        ToolTipService.SetShowOnDisabled(item, true);
        item.Click += (_, _) => SetClickThrough(item.IsChecked);
        _inputMenuItems.Add(new WeakReference<MenuItem>(item));
        RefreshInputMenus();
        return item;
    }

    private void RefreshInputMenus()
    {
        string recovery = InputHotkeyAvailable
            ? "Ctrl+Alt+I переключает режим. Левый щелчок по значку в трее возвращает управление HUD."
            : _inputTrayAvailable
                ? "Ctrl+Alt+I недоступна: сочетание занято или заблокировано Windows. Левый щелчок по значку в трее возвращает управление HUD."
                : "Пока нет доступной горячей клавиши или значка трея. Пропуск кликов выключен.";
        string description = IsClickThrough
            ? "HUD виден, а мышь проходит в игру. Кнопки, перетаскивание и меню панели доступны после разблокировки."
            : "Сворачивает панель и пропускает мышь в окно под ней. Показатели и музыка продолжают обновляться.";
        for (int index = _inputMenuItems.Count - 1; index >= 0; index--)
        {
            if (!_inputMenuItems[index].TryGetTarget(out MenuItem? item)) { _inputMenuItems.RemoveAt(index); continue; }
            item.IsChecked = IsClickThrough;
            item.IsEnabled = IsClickThrough || InputRecoveryAvailable;
            item.InputGestureText = InputHotkeyAvailable ? "Ctrl+Alt+I" : string.Empty;
            item.ToolTip = (_inputModeError is null ? string.Empty : _inputModeError + "\n") + description + "\n" + recovery;
        }
    }
}
