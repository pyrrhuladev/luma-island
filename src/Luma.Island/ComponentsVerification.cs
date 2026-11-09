using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island.Services;

namespace Luma.Island;

/// <summary>Exercises the real component controls and records native measurement snapshots.</summary>
public static class ComponentsVerification
{
    public sealed record Result(bool Passed, bool AcHidden, bool BatteryShownOnDc,
        bool StoppedWhenDisabled, PerformanceSnapshot Load, FpsSnapshot Fps, int MenuItems);

    public static async Task<Result> RunAsync(MainWindow window, PerformanceService performance,
        FpsService fps, MediaService media, string dir, List<string> errors)
    {
        window.VerifyAccess();
        Directory.CreateDirectory(dir);
        int initialErrors = errors.Count, menuItems = 0;
        bool acHidden = false, batteryShownOnDc = false, stopped = false;
        var load = PerformanceSnapshot.Empty;
        var frames = fps.Current;
        var allOff = new ComponentSettings(false, false, false, false, false, false, false, false, false);
        void Check(bool condition, string message) { if (!condition) errors.Add(message); }
        FrameworkElement Element(string name) => window.FindName(name) as FrameworkElement
            ?? throw new InvalidOperationException("Отсутствует элемент компонентов: " + name);
        bool Hidden(params string[] names) => names.All(name => Element(name).Visibility == Visibility.Collapsed);
        try
        {
            window.SetComponents(ComponentSettings.Default);
            var menu = window.CreateComponentsMenu();
            menuItems = menu.Items.Count;
            Check(menu.Items.OfType<MenuItem>().Count(item => item.IsCheckable) == 15 && menu.Items[5] is Separator,
                "В настройках отсутствуют переключатели компонентов.");
            void ClickOff(int index)
            {
                var item = (MenuItem)menu.Items[index];
                item.IsChecked = false;
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            }
            ClickOff(2);
            Check(Hidden("CompactClock"), "Переключатель времени не скрыл часы.");
            ClickOff(3);
            Check(Hidden("DateLabel"), "Переключатель даты не скрыл дату.");
            ClickOff(1);
            Check(Hidden("CompactVolume"), "Переключатель громкости не скрыл регулятор.");
            window.ApplyBattery(new BatterySnapshot(false, -1, false));
            acHidden = Hidden("CompactBattery");
            window.ApplyBattery(new BatterySnapshot(true, 80, false, true));
            acHidden &= Hidden("CompactBattery");
            Check(acHidden, "Батарея показана без аккумулятора или при питании от сети.");
            window.ApplyBattery(new BatterySnapshot(true, 80, false, false));
            batteryShownOnDc = Element("CompactBattery").Visibility == Visibility.Visible;
            Check(batteryShownOnDc, "Аккумулятор не показан при питании от батареи.");
            ClickOff(4);
            window.ApplyBattery(new BatterySnapshot(true, 80, false, false));
            Check(Hidden("CompactBattery"), "Выключенный компонент батареи остался видимым.");
            window.SetComponents(allOff);
            await media.SetEnabledAsync(false);
            await fps.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!performance.IsRunning && !fps.IsRunning && !media.IsEnabled && media.Current == MediaSnapshot.Empty,
                "Выключенные компоненты сохранили активные источники данных.");

            window.SetComponents(ComponentSettings.Default with { Cpu = true, Gpu = true, Memory = true, Fps = true });
            await media.SetEnabledAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
            var sampling = Stopwatch.StartNew();
            do { await Task.Delay(250); load = performance.Current; frames = fps.Current; }
            while (sampling.Elapsed < TimeSpan.FromSeconds(5));
            Check(load.CpuPercent is double cpuLoad && double.IsFinite(cpuLoad) && cpuLoad >= 0,
                "Реальный источник CPU не вернул допустимую загрузку.");
            Check(ValidPercent(load.MemoryPercent) && load.TotalMemoryBytes > 0 && load.UsedMemoryBytes <= load.TotalMemoryBytes,
                "Реальный источник RAM не вернул допустимые данные.");
            Check(load.GpuPercent is null || ValidPercent(load.GpuPercent), "Источник GPU вернул недопустимую загрузку.");
            Check(frames.Fps is null ? !string.IsNullOrWhiteSpace(frames.Status) : double.IsFinite(frames.Fps.Value) && frames.Fps.Value > 0,
                "Источник FPS вернул недопустимое значение или пустой статус.");
            Check(Element("CompactCpu") is TextBlock cpu && !cpu.Text.Contains('—') &&
                Element("CompactMemory") is TextBlock ram && !ram.Text.Contains('—'), "Измерения CPU/RAM не поступили в интерфейс.");
            await CaptureAsync(window, Path.Combine(dir, "components-compact.png"), errors);
            var compactBand = Element("CompactPerformance");
            Check(compactBand.IsVisible && compactBand.ActualHeight > 0 &&
                Element("CompactVolume").IsVisible && Element("DateLabel").IsVisible,
                "Компактный HUD не показал показатели, громкость или дату.");
            window.SetComponents(window.Components with { Charts = true });
            await Task.Delay(2500);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Check(Element("ChartsPanel").IsVisible && window.ChartsHeight > 0 &&
                new[] { "CpuChartCanvas", "GpuChartCanvas", "MemoryChartCanvas", "FpsChartCanvas" }
                    .Select(Element).All(canvas => canvas.IsVisible && canvas.ActualHeight > 4),
                "Компактный HUD не разместил четыре мини-графика.");
            Check(window.FindName("CpuChartLine") is System.Windows.Shapes.Path cpuLine && cpuLine.Data is not null && !cpuLine.Data.IsEmpty(),
                "График CPU не получил реальные отсчёты.");
            Check(window.FindName("MemoryChartLine") is System.Windows.Shapes.Path memoryLine && memoryLine.Data is not null && !memoryLine.Data.IsEmpty(),
                "График памяти не получил реальные отсчёты.");
            await CaptureAsync(window, Path.Combine(dir, "charts-compact.png"), errors);
            var appearance = window.Appearance;
            window.SetAppearance(IslandAppearance.Glass, .25);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            await CaptureAsync(window, Path.Combine(dir, "charts-transparent.png"), errors);
            window.SetAppearance(appearance.Mode, appearance.GlassOpacity);
            window.SetComponents(allOff);
            await fps.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(250);
            stopped = !performance.IsRunning && !fps.IsRunning && performance.Current == PerformanceSnapshot.Empty && fps.Current.Fps is null;
            Check(stopped, "После отключения счётчики продолжают работать или публиковать данные.");
        }
        catch (Exception ex) { errors.Add("Проверка компонентов: " + ex); }
        finally
        {
            try
            {
                window.SetComponents(ComponentSettings.Default);
                await fps.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                window.ApplyBattery(SystemService.ReadBattery());
                await media.SetEnabledAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex) { errors.Add("Восстановление компонентов: " + ex); }
        }
        return new Result(errors.Count == initialErrors, acHidden, batteryShownOnDc, stopped, load, frames, menuItems);
    }

    private static bool ValidPercent(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value);

    private static async Task CaptureAsync(MainWindow window, string path, List<string> errors)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Rect bounds = DisplayService.GetWindowBounds(window);
        double scale = DisplayService.GetWindowScale(window);
        if (Math.Abs(bounds.Width - window.CollapsedWindowWidth * scale) > 2 ||
            Math.Abs(bounds.Height - window.CollapsedWindowHeight * scale) > 2)
            errors.Add("Размер окна не соответствует размерам компактного HUD.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(window.CapturePreview()));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
