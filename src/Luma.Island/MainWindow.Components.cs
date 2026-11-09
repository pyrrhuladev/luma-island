using System;
using System.Windows;
using System.Windows.Controls;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private ComponentStore _componentStore = null!;
    private bool _batteryVisible;
    private BatterySnapshot _lastBattery = new(false, -1, false);
    private bool BatteryAvailable => _lastBattery.HasBattery && !_lastBattery.IsOnAcPower;
    private PerformanceSnapshot _lastPerformance = PerformanceSnapshot.Empty;
    public ComponentSettings Components { get; private set; } = ComponentSettings.Default;
    public event Action<ComponentSettings>? ComponentsChanged;
    public bool HasPerformance => Components.Cpu || Components.Gpu || Components.Memory || Components.Fps
        || Components.GpuTemperature || Components.CpuTemperature || Components.MemoryTemperature || Components.OnePercentLow || Components.FrameTime
        || Components.HasCharts;
    private bool HasHardwareMetrics => PresentedComponents.Cpu || PresentedComponents.Gpu || PresentedComponents.Memory
        || PresentedComponents.GpuTemperature || PresentedComponents.CpuTemperature || PresentedComponents.MemoryTemperature;
    private double TemperatureRowHeight => (PresentedComponents.Gpu && PresentedComponents.GpuTemperature)
        || (PresentedComponents.Cpu && PresentedComponents.CpuTemperature) || (PresentedComponents.Memory && PresentedComponents.MemoryTemperature) ? 16 : 0;
    private double HudHeaderContentHeight => (PresentedComponents.Fps || PresentedComponents.OnePercentLow ? 50
        : PresentedComponents.Time || _batteryVisible ? 24 : 0) * LayoutGrowth;
    private double HudMetricsContentHeight => HasHardwareMetrics ? CompactMetricGridHeight + 2 * LayoutSpacing : 0;
    private double HudMusicContentHeight => MusicInThisIsland ? 44 * LayoutGrowth : 0;
    private double HudHeaderHeight => HudHeaderContentHeight > 0 ? HudHeaderContentHeight + LayoutBlockGap("header") : 0;
    private double HudMetricsHeight => HudMetricsContentHeight > 0 ? HudMetricsContentHeight + LayoutBlockGap("metrics") : 0;
    private double HudMusicHeight => HudMusicContentHeight > 0 ? HudMusicContentHeight + LayoutBlockGap("music") : 0;
    private double HudVolumeHeight => VolumeInThisIsland ? 28 * LayoutGrowth : 0;
    private double HudDateHeight => PresentedComponents.Date ? 19 * LayoutGrowth : 0;
    private double BaseCollapsedWindowWidth
    {
        get
        {
            if (_containerContentSize is { } content) return content.Width + 60;
            if (_capsuleId is null || MusicInThisIsland) return 408;
            double header = (PresentedComponents.Fps ? 108 : 0) + (PresentedComponents.OnePercentLow ? 56 : 0)
                + (PresentedComponents.Time ? 52 : 0) + (_batteryVisible ? 48 : 0);
            double data = Math.Max(VisibleHardwareCount > 0 ? LayoutMetricColumns * 100 : 0, Math.Min(2, EnabledChartCount) * 126);
            double footer = PresentedComponents.FrameTime || VolumeInThisIsland ? 208 : PresentedComponents.Date ? 126 : 0;
            return Math.Min(408, 68 + LayoutGrowth * Math.Max(60, Math.Max(header, Math.Max(data, footer))));
        }
    }
    private double CollapsedSurfaceWidth => BaseCollapsedWindowWidth - 32;
    private double BaseCollapsedWindowHeight => 38 + Math.Max(20,
        _containerContentSize?.Height ??
        (HudHeaderHeight + HudMetricsHeight + HudMusicHeight + ChartsHeight + LayoutFrameTimeHeight + HudVolumeHeight + HudDateHeight));
    public double CollapsedWindowHeight => ScaledHudDimension(BaseCollapsedWindowHeight);
    public double CollapsedWindowWidth => ScaledHudDimension(BaseCollapsedWindowWidth);

    private void InitializeComponents(ComponentStore store)
    {
        _componentStore = store;
        Components = store.Load();
        CompactCpu.Text = "CPU —";
        CompactGpu.Text = "GPU —";
        CompactMemory.Text = "RAM —";
        CompactFps.Text = "—";
        ApplyComponentVisibility();
        OnMediaChanged(_media.Current);
    }

    public void SetComponents(ComponentSettings settings)
    {
        VerifyAccess();
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
        RefreshCapsuleProjection();
        CompactMusicRow.Visibility = Show(MusicInThisIsland);
        CompactArtworkFrame.Visibility = Show(MusicInThisIsland);
        CompactArtworkColumn.Width = new GridLength(MusicInThisIsland ? 30 : 0);
        CompactClock.Visibility = Show(PresentedComponents.Time);
        DateLabel.Visibility = Show(PresentedComponents.Date);
        CompactArtist.Visibility = Show(MusicInThisIsland);
        CompactPlaybackControls.Visibility = Show(MusicInThisIsland);
        CompactVolume.Visibility = Show(VolumeInThisIsland);
        CompactPerformance.Visibility = Show(HasHardwareMetrics);
        CompactCpu.Visibility = Show(PresentedComponents.Cpu || PresentedComponents.CpuTemperature);
        CompactGpu.Visibility = Show(PresentedComponents.Gpu || PresentedComponents.GpuTemperature);
        CompactMemory.Visibility = Show(PresentedComponents.Memory || PresentedComponents.MemoryTemperature);
        CompactFps.Visibility = Show(PresentedComponents.Fps);
        CompactFpsPanel.Visibility = Show(PresentedComponents.Fps);
        CompactLowPanel.Visibility = Show(PresentedComponents.OnePercentLow);
        FrameTimeCard.Visibility = Show(PresentedComponents.FrameTime);
        RefreshBatteryWidget();
        RefreshFrameMetricVisibility();
        RefreshGpuMetric();
        RefreshCpuMemoryMetrics();
        RefreshChartsVisibility();
        RefreshComponentSize();
        SynchronizeMusicIsland();
    }

    private void RefreshComponentSize()
    {
        ApplyHudLayout();
        UpdateHudScaleTransform();
        HudHeaderRow.Height = new GridLength(HudHeaderHeight);
        HudHeader.Visibility = Show(HudHeaderHeight > 0);
        HudMetricsRow.Height = new GridLength(HudMetricsHeight);
        HudMusicRow.Height = new GridLength(HudMusicHeight);
        IslandSurface.Height = BaseCollapsedWindowHeight - 24;
        IslandSurface.Width = CollapsedSurfaceWidth;
        IslandSurface.Visibility = !IsCapsuleLayoutActive || HasCapsuleContent
            ? Visibility.Visible : Visibility.Hidden;
        Height = ScaledHudDimension(BaseCollapsedWindowHeight + CapsuleEditBarHeight);
        Width = CollapsedWindowWidth;
        RefreshCapsuleEditChrome();
        _placement?.Apply();
    }

    internal void ApplyBattery(BatterySnapshot battery)
    {
        bool wasAvailable = BatteryAvailable;
        _lastBattery = battery;
        bool changed = RefreshBatteryWidget();
        if (changed) RefreshComponentSize();
        foreach (var child in _capsuleWindows.Values)
        {
            if (!child.PresentedComponents.Battery) continue;
            child._lastBattery = battery;
            if (!child.RefreshBatteryWidget()) continue;
            child.RefreshComponentSize();
            changed = true;
        }
        if (!IsCapsuleLayoutActive || !IsLoaded) return;
        if (wasAvailable != BatteryAvailable) SynchronizeMusicIsland();
        else if (changed) NotifyPresentationVisibilityChanged();
    }

    private bool RefreshBatteryWidget()
    {
        bool visible = PresentedComponents.Battery && (BatteryAvailable || IsCapsuleLayoutEditing);
        bool changed = _batteryVisible != visible;
        _batteryVisible = visible;
        CompactBattery.Visibility = Show(visible);
        string percent = _lastBattery.Percent >= 0 ? $"{_lastBattery.Percent}%" : "—";
        CompactBattery.Text = !BatteryAvailable && IsCapsuleLayoutEditing ? "Бат. " + percent : percent;
        CompactBattery.ToolTip = _lastBattery.IsCharging ? $"Заряжается · {percent}" : $"Батарея · {percent}";
        return changed;
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
        if (!IsCapsuleLayoutActive || HasHardwareMetrics)
        {
            RefreshGpuMetric();
            RefreshCpuMemoryMetrics();
        }
        RecordPerformanceHistory(snapshot);
        foreach (var child in _capsuleWindows.Values)
            if (child.IsVisible && (child.HasHardwareMetrics || child.PresentsChart("cpu") || child.PresentsChart("gpu") || child.PresentsChart("memory")))
                child.ApplyPerformanceCore(snapshot, observeAlerts: false);
    }

    private void RefreshCpuMemoryMetrics()
    {
        string cpuLabel = PresentedComponents.Cpu ? Percent("CPU", _lastPerformance.CpuPercent) : "CPU";
        string memoryLabel = PresentedComponents.Memory ? Percent("RAM", _lastPerformance.MemoryPercent) : "RAM";
        string cpuTip = PresentedComponents.Cpu ? CpuUsageDescription(_lastPerformance.CpuUsageSource) : string.Empty;
        string memoryTip = PresentedComponents.Memory && _lastPerformance.TotalMemoryBytes > 0
            ? $"Память: {_lastPerformance.UsedMemoryBytes / 1073741824d:0.0} / {_lastPerformance.TotalMemoryBytes / 1073741824d:0.0} ГБ" : string.Empty;
        if (PresentedComponents.CpuTemperature)
        {
            cpuLabel += (PresentedComponents.Cpu ? "\n" : " ") + Degrees(_lastPerformance.CpuTemperatureCelsius);
            cpuTip += (cpuTip.Length > 0 ? "\n" : "") + "Температура CPU Package / Tctl/Tdie.\n"
                + SensorStatusText(_lastPerformance.CpuTemperatureStatus);
        }
        if (PresentedComponents.MemoryTemperature)
        {
            memoryLabel += (PresentedComponents.Memory ? "\n" : " ") + Degrees(_lastPerformance.MemoryTemperatureCelsius, memory: true);
            memoryTip += (memoryTip.Length > 0 ? "\n" : "") + "Максимальная температура доступных модулей RAM (датчик DIMM / SPD hub).\n"
                + SensorStatusText(_lastPerformance.MemoryTemperatureStatus);
        }
        CompactCpu.Text = cpuLabel;
        CompactMemory.Text = memoryLabel;
        CompactCpu.ToolTip = cpuTip;
        CompactMemory.ToolTip = memoryTip;
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
        string label = PresentedComponents.Gpu ? Percent("GPU", _lastPerformance.GpuPercent) : "GPU";
        string tip = PresentedComponents.Gpu ? (_lastPerformance.GpuPercent.HasValue
            ? "Загрузка самого занятого графического движка" : "Нет данных GPU от системного счётчика Windows") : string.Empty;
        if (PresentedComponents.GpuTemperature)
        {
            double? temperature = _lastPerformance.GpuTemperatureCelsius;
            string degrees = temperature is >= 0 and <= 150 && double.IsFinite(temperature.Value)
                ? temperature.Value.ToString("0", _culture) : "—";
            label += (PresentedComponents.Gpu ? "\n" : " ") + degrees + "\u00a0°C";
            if (tip.Length > 0) tip += "\n";
            tip += "Температура ядра GPU: максимум среди доступных видеокарт NVIDIA.\n"
                + (_lastPerformance.GpuTemperatureStatus ?? "Данных пока нет.");
        }
        CompactGpu.Text = label;
        CompactGpu.ToolTip = tip;
    }

    public void ApplyFps(FpsSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(() => ApplyFps(snapshot))); return; }
        if (_closed) return;
        ApplyAlertFrames(snapshot);
        ApplyFpsPresentation(snapshot);
        foreach (var child in _capsuleWindows.Values)
            if (child.IsVisible && (child.PresentedComponents.Fps || child.PresentedComponents.OnePercentLow ||
                child.PresentedComponents.FrameTime || child.PresentsChart("fps"))) child.ApplyFpsPresentation(_lastFrameMetrics);
    }

    private void ApplyFpsPresentation(FpsSnapshot snapshot)
    {
        ApplyFrameMetrics(snapshot);
        bool valid = snapshot.Fps is >= 0 && double.IsFinite(snapshot.Fps.Value);
        CompactFps.Text = valid ? snapshot.Fps!.Value.ToString("0", _culture) : "—";
        FitHeaderText();
        System.Windows.Automation.AutomationProperties.SetName(CompactFpsPanel, valid ? CompactFps.Text + " FPS" : "FPS: нет данных");
        CompactFps.ToolTip = string.IsNullOrEmpty(snapshot.ProcessName)
            ? snapshot.Status : $"{snapshot.ProcessName} · {snapshot.Status}";
        RecordFpsHistory(snapshot);
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
                "Последние 10 секунд реальных интервалов Present игры в миллисекундах. График занимает отдельную строку HUD.";
            menu.Items.Add(new Separator());
            var setup = new MenuItem { Header = "Настроить датчики CPU / RAM…" };
            setup.Click += (_, _) => new SensorSetupWindow { Owner = this }.ShowDialog();
            menu.Items.Add(setup);
            Add("Мини-графики", s => s.Charts, (s, v) => s with { Charts = v }).ToolTip =
                "Графики выбранных CPU, GPU, RAM и FPS. Нагрузка показана в процентах.";
        }
        Populate();
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) Populate(); };
        return menu;
    }
}
