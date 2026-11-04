using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Luma.Island;

/// <summary>Explicit, optional setup. Opening this window never starts an installer or a hardware scan.</summary>
public sealed class SensorSetupWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 16) };
    private readonly Button _install = new() { Content = "Установить / обновить модуль Luma", Padding = new Thickness(12, 8, 12, 8) };
    private readonly Button _remove = new() { Content = "Удалить модуль Luma", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 0, 0) };
    private bool _working;
    private static string HostPath => Path.Combine(AppContext.BaseDirectory, "sensors", "Luma.SensorHost.exe");

    public SensorSetupWindow()
    {
        Title = "Датчики CPU / RAM · Luma Island";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(22, 26, 31));
        Foreground = Brushes.WhiteSmoke;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Температуры CPU и памяти", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Нужны драйвер PawnIO и необязательный модуль датчиков Luma. Установщики запросят права администратора. Оверлей продолжит работать с обычными правами.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
        });
        panel.Children.Add(_status);
        var driver = new Button { Content = "1. Открыть официальный сайт PawnIO", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
        driver.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://pawnio.eu/") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { _status.Text = "Не удалось открыть сайт: https://pawnio.eu/"; }
        };
        panel.Children.Add(driver);
        panel.Children.Add(new TextBlock
        {
            Text = "Установите подписанную Official edition, затем модуль Luma. Драйвер не скачивается и не устанавливается автоматически.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(_install);
        panel.Children.Add(_remove);
        _install.Click += async (_, _) => await RunSetupAsync(install: true);
        _remove.Click += async (_, _) => await RunSetupAsync(install: false);
        panel.Children.Add(new TextBlock
        {
            Text = "После установки включите температуры в меню «Компоненты». RAM появится только при наличии доступных датчиков на планках. Модуль читает выбранные температуры; отключение прекращает опрос. Драйвер PawnIO удаляется отдельно через приложения Windows.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 16, 0, 16)
        });
        var close = new Button { Content = "Готово", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(20, 7, 20, 7) };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;
        Activated += (_, _) => { if (!_working) RefreshStatus(); };
        Closing += (_, args) => { if (_working) args.Cancel = true; };
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var pawn = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LumaIslandSensors");
            _status.Text = "PawnIO: " + (pawn is null ? "не найден" : "установлен") + "\nМодуль Luma: "
                + (service is null ? "не установлен" : "установлен") + ".";
            _remove.IsEnabled = service is not null;
            _install.IsEnabled = File.Exists(HostPath);
            if (!_install.IsEnabled) _status.Text += "\nФайлы модуля отсутствуют. Распакуйте весь архив Luma.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _status.Text = "Не удалось проверить установку. " + ex.Message;
        }
    }

    private async Task RunSetupAsync(bool install)
    {
        if (_working) return;
        _working = true;
        _install.IsEnabled = _remove.IsEnabled = false;
        _status.Text = "Ожидание установщика Windows…";
        string result;
        try
        {
            var start = new ProcessStartInfo(HostPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(install ? "--install" : "--remove");
            if (install)
            {
                start.ArgumentList.Add("--owner-sid");
                start.ArgumentList.Add(WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Не найден пользователь Windows."));
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить установщик.");
            await process.WaitForExitAsync();
            result = process.ExitCode == 0 ? (install ? "Модуль готов. Включите нужные температуры в компонентах." : "Модуль Luma удалён.")
                : $"Установщик завершился с ошибкой {process.ExitCode}.";
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LumaIsland", "Sensors", "setup.log");
            if (process.ExitCode != 0 && File.Exists(log)) result += " Журнал: " + log;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { result = "Установка отменена в запросе Windows."; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        { App.Log(ex); result = "Не удалось запустить установщик модуля."; }
        finally { _working = false; }
        RefreshStatus();
        _status.Text += "\n\n" + result;
    }
}
