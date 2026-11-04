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
    public bool HasPerformance => Components.Cpu || Components.Gpu || Components.Memory || Components.Fps || Components.GpuTemperature;
    private double TemperatureRowHeight => Components.Gpu && Components.GpuTemperature ? 16 : 0;
    public double CollapsedWindowHeight => (Appearance.Compact
        ? 44 + (HasPerformance ? 28 + TemperatureRowHeight : 0)
        : HasPerformance ? 100 + TemperatureRowHeight : 56) + 24;
    public double ExpandedWindowHeight => 25 + 29 + (Components.Music ? 130 : 0) + (Components.Volume ? 34 : 0)
        + (HasPerformance ? 32 + TemperatureRowHeight : 0) + ChartsHeight + (Components.Date || _batteryVisible ? 26 : 0) + 24;

    private void InitializeComponents(ComponentStore store)
    {
        _componentStore = store;
        Components = store.Load();
        CompactCpu.Text = ExpandedCpu.Text = "CPU —";
        CompactGpu.Text = ExpandedGpu.Text = "GPU —";
        CompactMemory.Text = ExpandedMemory.Text = "RAM —";
        CompactFps.Text = ExpandedFps.Text = "FPS —";
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
        CompactPerformance.Visibility = ExpandedPerformance.Visibility = Show(HasPerformance);
        CompactPerformance.Height = (Appearance.Compact ? 20 : 28) + TemperatureRowHeight;
        PerformanceRow.Height = new GridLength(HasPerformance ? 32 + TemperatureRowHeight : 0);
        ChartsRow.Height = new GridLength(ChartsHeight);
        CompactPerformance.Columns = Math.Max(1, (Components.Cpu ? 1 : 0) + (Components.Gpu || Components.GpuTemperature ? 1 : 0)
            + (Components.Memory ? 1 : 0) + (Components.Fps ? 1 : 0));
        CompactCpu.Visibility = ExpandedCpu.Visibility = Show(Components.Cpu);
        CompactGpu.Visibility = ExpandedGpu.Visibility = Show(Components.Gpu || Components.GpuTemperature);
        CompactMemory.Visibility = ExpandedMemory.Visibility = Show(Components.Memory);
        CompactFps.Visibility = ExpandedFps.Visibility = Show(Components.Fps);
        FooterRow.Height = new GridLength(Components.Date || _batteryVisible ? 26 : 0);
        RefreshGpuMetric();
        RefreshChartsVisibility();
        RefreshComponentSize();
    }

    private void RefreshComponentSize()
    {
        CompleteTransition();
        IslandSurface.Height = (_expanded ? ExpandedWindowHeight : CollapsedWindowHeight) - 24;
        IslandSurface.Width = _expanded ? 384 : CollapsedSurfaceWidth;
        Height = _expanded ? ExpandedWindowHeight : CollapsedWindowHeight;
        Width = _expanded ? 408 : CollapsedWindowWidth;
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
        FooterRow.Height = new GridLength(Components.Date || visible ? 26 : 0);
        if (changed) RefreshComponentSize();
    }

    public void ApplyPerformance(PerformanceSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(() => ApplyPerformance(snapshot))); return; }
        if (_closed) return;
        _lastPerformance = snapshot;
        CompactCpu.Text = ExpandedCpu.Text = Percent("CPU", snapshot.CpuPercent);
        RefreshGpuMetric();
        CompactMemory.Text = ExpandedMemory.Text = Percent("RAM", snapshot.MemoryPercent);
        CompactMemory.ToolTip = ExpandedMemory.ToolTip = snapshot.TotalMemoryBytes > 0
            ? $"Память: {snapshot.UsedMemoryBytes / 1073741824d:0.0} / {snapshot.TotalMemoryBytes / 1073741824d:0.0} ГБ" : "Нет данных о памяти";
        RecordPerformanceHistory(snapshot);
    }

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
        CompactFps.Text = ExpandedFps.Text = snapshot.Fps is >= 0 && double.IsFinite(snapshot.Fps.Value)
            ? $"{snapshot.Fps.Value:0} FPS" : "FPS —";
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
            Add("Нагрузка GPU", s => s.Gpu, (s, v) => s with { Gpu = v });
            Add("Температура GPU", s => s.GpuTemperature, (s, v) => s with { GpuTemperature = v }).ToolTip =
                "Температура ядра через установленный драйвер NVIDIA. Не требует отдельной программы; при отсутствии данных — прочерк.";
            Add("Память RAM", s => s.Memory, (s, v) => s with { Memory = v });
            Add("FPS активной игры", s => s.Fps, (s, v) => s with { Fps = v }).ToolTip =
                "Источник: установленный NVIDIA FrameView. Включение запускает его счётчик; без данных отображается прочерк.";
            menu.Items.Add(new Separator());
            Add("Расширенный режим: мини-графики", s => s.Charts, (s, v) => s with { Charts = v }).ToolTip =
                "Графики выбранных CPU, GPU, RAM и FPS в раскрытой панели; нагрузка показана в процентах.";
        }
        Populate();
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) Populate(); };
        return menu;
    }
}
