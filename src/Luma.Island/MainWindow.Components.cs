using System;
using System.Windows;
using System.Windows.Controls;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private ComponentStore _componentStore = null!;
    private bool _batteryVisible;
    private PerformanceSnapshot _lastPerformance = PerformanceSnapshot.Empty;
    public ComponentSettings Components { get; private set; } = ComponentSettings.Default;
    public event Action<ComponentSettings>? ComponentsChanged;
    public bool HasPerformance => Components.Cpu || Components.Gpu || Components.Memory || Components.Fps
        || Components.GpuTemperature || Components.CpuTemperature || Components.MemoryTemperature || Components.OnePercentLow || Components.FrameTime;
    private bool HasInlinePerformance => HasHardwareMetrics || Components.Fps;
    private bool HasHardwareMetrics => Components.Cpu || Components.Gpu || Components.Memory
        || Components.GpuTemperature || Components.CpuTemperature || Components.MemoryTemperature;
    private double TemperatureRowHeight => (Components.Gpu && Components.GpuTemperature)
        || (Components.Cpu && Components.CpuTemperature) || (Components.Memory && Components.MemoryTemperature) ? 16 : 0;
    private double HudHeaderContentHeight => (Components.Fps || Components.OnePercentLow ? Appearance.Compact ? 42 : 50
        : Components.Time || _batteryVisible || Components.FrameTime || (!Components.Music && !HasHardwareMetrics) ? Appearance.Compact ? 20 : 24 : 0) * LayoutGrowth;
    private double HudMetricsContentHeight => HasHardwareMetrics ? CompactMetricGridHeight + 2 * (Appearance.Compact ? 4 : 6) * LayoutSpacing / 6 : 0;
    private double HudMusicContentHeight => Components.Music ? (Appearance.Compact ? 34 : 44) * LayoutGrowth : 0;
    private double HudHeaderHeight => HudHeaderContentHeight > 0 ? HudHeaderContentHeight + LayoutBlockGap("header") : 0;
    private double HudMetricsHeight => HudMetricsContentHeight > 0 ? HudMetricsContentHeight + LayoutBlockGap("metrics") : 0;
    private double HudMusicHeight => HudMusicContentHeight > 0 ? HudMusicContentHeight + LayoutBlockGap("music") : 0;
    private double BaseCollapsedWindowHeight => 24 + (Appearance.Compact ? 10 : 12) + HudHeaderHeight + HudMetricsHeight + HudMusicHeight;
    public double CollapsedWindowHeight => ScaledHudDimension(BaseCollapsedWindowHeight);
    private double BaseExpandedWindowHeight => 25 + 29 + (Components.Music ? 78 * LayoutGrowth + 52 : 0) + (Components.Volume ? 34 : 0)
        + ExpandedMetricBandHeight + LayoutChartsHeight + LayoutFrameTimeHeight + (Components.Date || _batteryVisible ? 26 * LayoutGrowth : 0) + 24;
    public double ExpandedWindowHeight => ScaledHudDimension(BaseExpandedWindowHeight);

    private void InitializeComponents(ComponentStore store)
    {
        _componentStore = store;
        Components = store.Load();
        CompactCpu.Text = ExpandedCpu.Text = "CPU —";
        CompactGpu.Text = ExpandedGpu.Text = "GPU —";
        CompactMemory.Text = ExpandedMemory.Text = "RAM —";
        CompactFps.Text = "—";
        ExpandedFps.Text = "FPS —";
        ApplyComponentVisibility();
        OnMediaChanged(_media.Current);
    }

    public void SetComponents(ComponentSettings settings)
    {
        VerifyAccess();
        CompleteTransition();
        Components = settings;
        UpdateClockAndBattery();
        ApplyComponentVisibility();
        OnMediaChanged(_media.Current);
        if (!_componentStore.Save(settings)) System.Windows.MessageBox.Show(
            "Компоненты переключены, но сохранить выбор не удалось.", "Luma Island", MessageBoxButton.OK, MessageBoxImage.Warning);
        ComponentsChanged?.Invoke(settings);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    private void ApplyComponentVisibility()
    {
        ApplyCompactDensity();
        CompactMusicRow.Visibility = Show(Components.Music);
        CompactArtworkFrame.Visibility = Show(Components.Music);
        CompactArtworkColumn.Width = new GridLength(Components.Music ? Appearance.Compact ? 25 : 30 : 0);
        CompactClock.Visibility = ExpandedClock.Visibility = Show(Components.Time);
        DateLabel.Visibility = Show(Components.Date);
        CompactArtist.Visibility = Show(Components.Music && !Appearance.Compact);
        ExpandedMusicInfo.Visibility = ExpandedPlaybackControls.Visibility = Show(Components.Music);
        ExpandedVolume.Visibility = Show(Components.Volume);
        MusicInfoRow.Height = new GridLength(Components.Music ? 78 : 0);
        MusicControlsRow.Height = new GridLength(Components.Music ? 52 : 0);
        VolumeRow.Height = new GridLength(Components.Volume ? 34 : 0);
        CompactPerformance.Visibility = Show(HasHardwareMetrics);
        ExpandedPerformance.Visibility = Show(HasInlinePerformance);
        CompactPerformance.Height = (Appearance.Compact ? 20 : 28) + TemperatureRowHeight;
        PerformanceRow.Height = new GridLength(HasInlinePerformance ? 32 + TemperatureRowHeight : 0);
        ChartsRow.Height = new GridLength(ChartsHeight);
        CompactPerformance.Columns = Math.Max(1, (Components.Cpu || Components.CpuTemperature ? 1 : 0) + (Components.Gpu || Components.GpuTemperature ? 1 : 0)
            + (Components.Memory || Components.MemoryTemperature ? 1 : 0));
        CompactCpu.Visibility = ExpandedCpu.Visibility = Show(Components.Cpu || Components.CpuTemperature);
        CompactGpu.Visibility = ExpandedGpu.Visibility = Show(Components.Gpu || Components.GpuTemperature);
        CompactMemory.Visibility = ExpandedMemory.Visibility = Show(Components.Memory || Components.MemoryTemperature);
        CompactFps.Visibility = ExpandedFps.Visibility = Show(Components.Fps);
        CompactFpsPanel.Visibility = Show(Components.Fps);
        CompactLowPanel.Visibility = ExpandedOnePercentLow.Visibility = Show(Components.OnePercentLow);
        ExpandedSectionLabel.Visibility = Show(!Components.OnePercentLow);
        CompactFrameTime.Visibility = Show(Components.FrameTime && !Components.Fps && !Components.OnePercentLow);
        FrameTimeCard.Visibility = Show(Components.FrameTime);
        FrameTimeRow.Height = new GridLength(Components.FrameTime ? 96 : 0);
        RefreshFrameMetricVisibility();
        FooterRow.Height = new GridLength(Components.Date || _batteryVisible ? 26 : 0);
        RefreshGpuMetric();
        RefreshCpuMemoryMetrics();
        RefreshChartsVisibility();
        RefreshComponentSize();
    }

    private void RefreshComponentSize()
    {
        CompleteTransition();
        ApplyHudLayout();
        UpdateHudScaleTransform();
        HudHeaderRow.Height = new GridLength(HudHeaderHeight);
        HudHeader.Visibility = Show(HudHeaderHeight > 0);
        HudMetricsRow.Height = new GridLength(HudMetricsHeight);
        HudMusicRow.Height = new GridLength(HudMusicHeight);
        IslandSurface.Height = (_expanded ? BaseExpandedWindowHeight : BaseCollapsedWindowHeight) - 24;
        IslandSurface.Width = _expanded ? 384 : CollapsedSurfaceWidth;
        Height = _expanded ? ExpandedWindowHeight : CollapsedWindowHeight;
        Width = _expanded ? ExpandedWindowWidth : CollapsedWindowWidth;
        _surfaceOffset.X = _surfaceOffset.Y = 0;
        UpdateSurfaceRounding();
        _placement?.Apply();
    }

    internal void ApplyBattery(BatterySnapshot battery)
    {
        bool visible = Components.Battery && battery.HasBattery && !battery.IsOnAcPower;
        bool changed = _batteryVisible != visible;
        _batteryVisible = visible;
        CompactBattery.Visibility = BatteryLabel.Visibility = Show(visible);
        string percent = battery.Percent >= 0 ? $"{battery.Percent}%" : "—";
        CompactBattery.Text = percent;
        BatteryLabel.Text = battery.IsCharging ? $"Заряжается · {percent}" : $"Батарея · {percent}";
        FooterRow.Height = new GridLength(Components.Date || visible ? 26 * LayoutGrowth : 0);
        if (changed) RefreshComponentSize();
    }

    public void ApplyPerformance(PerformanceSnapshot snapshot)
        => ApplyPerformanceCore(snapshot, observeAlerts: true);

    public void ApplyPerformanceDisplay(PerformanceSnapshot snapshot)
        => ApplyPerformanceCore(snapshot, observeAlerts: false);

    private void ApplyPerformanceCore(PerformanceSnapshot snapshot, bool observeAlerts)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(() => ApplyPerformanceCore(snapshot, observeAlerts))); return; }
        if (_closed) return;
        _lastPerformance = snapshot;
        if (observeAlerts) ApplyAlertPerformance(snapshot);
        RefreshGpuMetric();
        RefreshCpuMemoryMetrics();
        RecordPerformanceHistory(snapshot);
    }

    private void RefreshCpuMemoryMetrics()
    {
        string cpuLabel = Components.Cpu ? Percent("CPU", _lastPerformance.CpuPercent) : "CPU";
        string memoryLabel = Components.Memory ? Percent("RAM", _lastPerformance.MemoryPercent) : "RAM";
        string cpuTip = Components.Cpu ? CpuUsageDescription(_lastPerformance.CpuUsageSource) : string.Empty;
        string memoryTip = Components.Memory && _lastPerformance.TotalMemoryBytes > 0
            ? $"Память: {_lastPerformance.UsedMemoryBytes / 1073741824d:0.0} / {_lastPerformance.TotalMemoryBytes / 1073741824d:0.0} ГБ" : string.Empty;
        if (Components.CpuTemperature)
        {
            cpuLabel += (Components.Cpu ? "\n" : " ") + Degrees(_lastPerformance.CpuTemperatureCelsius);
            cpuTip += (cpuTip.Length > 0 ? "\n" : "") + "Температура CPU Package / Tctl/Tdie.\n"
                + SensorStatusText(_lastPerformance.CpuTemperatureStatus);
        }
        if (Components.MemoryTemperature)
        {
            memoryLabel += (Components.Memory ? "\n" : " ") + Degrees(_lastPerformance.MemoryTemperatureCelsius, memory: true);
            memoryTip += (memoryTip.Length > 0 ? "\n" : "") + "Максимальная температура доступных модулей RAM (датчик DIMM / SPD hub).\n"
                + SensorStatusText(_lastPerformance.MemoryTemperatureStatus);
        }
        CompactCpu.Text = ExpandedCpu.Text = cpuLabel;
        CompactMemory.Text = ExpandedMemory.Text = memoryLabel;
        CompactCpu.ToolTip = ExpandedCpu.ToolTip = cpuTip;
        CompactMemory.ToolTip = ExpandedMemory.ToolTip = memoryTip;
    }

    private string Degrees(double? temperature, bool memory = false) => (SensorProtocol.IsValidCelsius(temperature, memory)
        ? temperature.GetValueOrDefault().ToString("0", _culture) : "—") + "\u00a0°C";

    private static string SensorStatusText(string? status) => status switch
    {
        "ok" => "Данные получены от модуля датчиков Luma.",
        "driver_missing" => "Не найден драйвер PawnIO. Откройте настройку датчиков в меню компонентов.",
        "not_installed" => "Модуль Luma не установлен или не запущен. Откройте настройку датчиков в меню компонентов.",
        "service_stopped" => "Служба датчиков остановлена. Обновите модуль в меню «Настроить датчики CPU / RAM…».",
        "service_starting" => "Служба датчиков запускается. Показания появятся после подключения.",
        "access_denied" => "Нет доступа к модулю датчиков для этого пользователя Windows.",
        "timeout" => "Модуль датчиков не ответил вовремя.",
        "protocol_error" => "Версии модуля и Luma несовместимы. Обновите модуль в настройке датчиков.",
        "provider_error" => "Ошибка чтения аппаратных датчиков.",
        "unavailable" => "Совместимый датчик недоступен на этом компьютере.",
        "disabled" => "Опрос выключен.",
        _ => "Ожидание датчиков. Настройка — в меню компонентов."
    };

    private static string CpuUsageDescription(string? source) => source?.StartsWith(CpuUsageReader.BusySource, StringComparison.Ordinal) == true
        ? "Резервный расчёт: доля занятого времени CPU без учёта Turbo Boost. Счётчик Processor Utility недоступен."
        : "Средняя нагрузка CPU по Windows Processor Utility с учётом частоты и Turbo Boost. Значение может превышать 100%.";

    private void RefreshGpuMetric()
    {
        string label = Components.Gpu ? Percent("GPU", _lastPerformance.GpuPercent) : "GPU";
        string tip = Components.Gpu ? (_lastPerformance.GpuPercent.HasValue
            ? "Загрузка самого занятого графического движка" : "Нет данных GPU от системного счётчика Windows") : string.Empty;
        if (Components.GpuTemperature)
        {
            double? temperature = _lastPerformance.GpuTemperatureCelsius;
            string degrees = temperature is >= 0 and <= 150 && double.IsFinite(temperature.Value)
                ? temperature.Value.ToString("0", _culture) : "—";
            label += (Components.Gpu ? "\n" : " ") + degrees + "\u00a0°C";
            if (tip.Length > 0) tip += "\n";
            tip += "Температура ядра GPU: максимум среди доступных видеокарт NVIDIA.\n"
                + (_lastPerformance.GpuTemperatureStatus ?? "Данных пока нет.");
        }
        CompactGpu.Text = ExpandedGpu.Text = label;
        CompactGpu.ToolTip = ExpandedGpu.ToolTip = tip;
    }

    public void ApplyFps(FpsSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(() => ApplyFps(snapshot))); return; }
        if (_closed) return;
        RecordFpsHistory(snapshot);
        ApplyAlertFrames(snapshot);
        ApplyFrameMetrics(snapshot);
        bool valid = snapshot.Fps is >= 0 && double.IsFinite(snapshot.Fps.Value);
        CompactFps.Text = valid ? snapshot.Fps!.Value.ToString("0", _culture) : "—";
        ExpandedFps.Text = valid ? $"{snapshot.Fps!.Value:0} FPS" : "FPS —";
        FitHeaderText();
        System.Windows.Automation.AutomationProperties.SetName(CompactFpsPanel, valid ? CompactFps.Text + " FPS" : "FPS: нет данных");
        CompactFps.ToolTip = ExpandedFps.ToolTip = string.IsNullOrEmpty(snapshot.ProcessName)
            ? snapshot.Status : $"{snapshot.ProcessName} · {snapshot.Status}";
    }

    private static string Percent(string name, double? percent) => percent is >= 0 && double.IsFinite(percent.Value)
        ? $"{name} {percent.Value:0}%" : $"{name} —";

    public MenuItem CreateComponentsMenu()
    {
        var menu = new MenuItem { Header = "Компоненты" };
        void Populate()
        {
            menu.Items.Clear();
            MenuItem Add(string label, Func<ComponentSettings, bool> read, Func<ComponentSettings, bool, ComponentSettings> write)
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = read(Components), StaysOpenOnClick = true };
                item.Click += (_, _) => SetComponents(write(Components, item.IsChecked));
                menu.Items.Add(item);
                return item;
            }
            Add("Музыка", s => s.Music, (s, v) => s with { Music = v });
            Add("Громкость", s => s.Volume, (s, v) => s with { Volume = v });
            Add("Время", s => s.Time, (s, v) => s with { Time = v });
            Add("Дата", s => s.Date, (s, v) => s with { Date = v });
            Add("Батарея (при работе от батареи)", s => s.Battery, (s, v) => s with { Battery = v });
            menu.Items.Add(new Separator());
            Add("Нагрузка CPU", s => s.Cpu, (s, v) => s with { Cpu = v });
            Add("Температура CPU", s => s.CpuTemperature, (s, v) => s with { CpuTemperature = v }).ToolTip =
                "Требует модуля датчиков Luma и драйвера PawnIO. При отсутствии датчика — прочерк.";
            Add("Нагрузка GPU", s => s.Gpu, (s, v) => s with { Gpu = v });
            Add("Температура GPU", s => s.GpuTemperature, (s, v) => s with { GpuTemperature = v }).ToolTip =
                "Температура ядра через установленный драйвер NVIDIA. Не требует отдельной программы; при отсутствии данных — прочерк.";
            Add("Память RAM", s => s.Memory, (s, v) => s with { Memory = v });
            Add("Температура RAM", s => s.MemoryTemperature, (s, v) => s with { MemoryTemperature = v }).ToolTip =
                "Датчик DIMM / SPD hub: максимум среди доступных модулей. Доступность зависит от памяти и платы.";
            Add("FPS активной игры", s => s.Fps, (s, v) => s with { Fps = v }).ToolTip =
                "Источник: установленный NVIDIA FrameView. Включение запускает его счётчик; без данных отображается прочерк.";
            Add("1% Low", s => s.OnePercentLow, (s, v) => s with { OnePercentLow = v }).ToolTip =
                "FPS наиболее медленных кадров: метрика 1% Low установленного NVIDIA FrameView. Можно включить отдельно от обычного FPS.";
            Add("График времени кадра", s => s.FrameTime, (s, v) => s with { FrameTime = v }).ToolTip =
                "Последние 10 секунд реальных интервалов Present игры в миллисекундах. График доступен в раскрытой панели независимо от остальных графиков.";
            menu.Items.Add(new Separator());
            var setup = new MenuItem { Header = "Настроить датчики CPU / RAM…" };
            setup.Click += (_, _) => new SensorSetupWindow { Owner = this }.ShowDialog();
            menu.Items.Add(setup);
            Add("Расширенный режим: мини-графики", s => s.Charts, (s, v) => s with { Charts = v }).ToolTip =
                "Графики выбранных CPU, GPU, RAM и FPS в раскрытой панели; нагрузка показана в процентах.";
        }
        Populate();
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) Populate(); };
        return menu;
    }
}
