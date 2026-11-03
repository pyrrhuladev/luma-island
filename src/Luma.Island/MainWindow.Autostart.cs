using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly List<MenuItem> _autostartItems = new();

    public MenuItem CreateAutostartMenu()
    {
        var item = new MenuItem { Header = "Запускать вместе с Windows", IsCheckable = true };
        _autostartItems.Add(item);
        item.Click += (_, _) => ToggleAutostart();
        RefreshAutostartMenus();
        return item;
    }

    public void RefreshAutostartMenus()
    {
        var state = _autostart.Read();
        foreach (var item in _autostartItems)
        {
            item.IsChecked = state.Enabled;
            item.IsEnabled = state.Error is null;
            item.ToolTip = state.Error ?? (state.DisabledByWindows
                ? "Отключено в настройках Windows. Нажмите, чтобы открыть автозагрузку."
                : "Запускать капсулу после входа в Windows");
        }
    }

    private void ToggleAutostart()
    {
        try
        {
            var before = _autostart.Read();
            if (before.Error is not null) throw new InvalidOperationException(before.Error);
            _autostart.SetEnabled(!before.Enabled);
            var after = _autostart.Read();
            if (after.Error is not null) throw new InvalidOperationException(after.Error);
            if (!before.Enabled && after.DisabledByWindows)
            {
                MessageBox.Show(this, "Windows отключила автозапуск Luma Island. Включите его в открывшемся разделе «Автозагрузка».",
                    "Luma Island", MessageBoxButton.OK, MessageBoxImage.Information);
                Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Не удалось изменить автозапуск. " + ex.Message,
                "Luma Island", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshAutostartMenus(); }
    }
}
