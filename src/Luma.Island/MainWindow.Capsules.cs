using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private readonly string? _capsuleId;
    private string? _capsuleItems;
    private string? _capsuleRows;
    private readonly Dictionary<string, MainWindow> _capsuleWindows = new(StringComparer.Ordinal);
    private ComponentSettings? _presentedComponents;
    private HudLayoutSettings? _presentationLayout;
    private string? _presentationItemsKey;
    private string? _presentationRowsKey;
    private string[]? PresentationCapsuleItems { get; set; }
    private string[][]? PresentationRows { get; set; }

    private ComponentSettings PresentedComponents => _presentedComponents ?? Components;
    private HudLayoutSettings PresentationLayout => _presentationLayout ?? LayoutSettings;
    private bool IsCapsuleLayoutActive => _capsuleId is not null || (_islandOwner is null && LayoutSettings.Capsules is not null);
    private bool CapsuleWindowsVisible => _capsuleWindows.Values.Any(window => window.IsVisible && window.HasCapsuleContent);
    private bool AnyCapsuleIsDragging => _capsuleWindows.Values.Any(window => window._placement?.IsDragging == true);
    private bool HasCapsuleContent => HudHeaderContentHeight > 0 || HasHardwareMetrics || MusicInThisIsland ||
        VolumeInThisIsland || PresentedComponents.Date || PresentedComponents.FrameTime || EnabledChartCount > 0 || Appearance.Alerts.NeedsCpuTemperature ||
        Appearance.Alerts.NeedsGpuTemperature || Appearance.Alerts.NeedsMemoryTemperature || Appearance.Alerts.NeedsFrames;

    internal IEnumerable<(string Id, MainWindow Window)> LayoutWindows()
    {
        if (_islandOwner is not null)
        {
            foreach (var capsule in _islandOwner.LayoutWindows()) yield return capsule;
            yield break;
        }
        yield return (HudCapsuleLayout.MainId, this);
        if (_musicIsland is not null) yield return ("music", _musicIsland);
        foreach (var capsule in _capsuleWindows) yield return (capsule.Key, capsule.Value);
    }

    private void ApplyCapsuleDraft(HudCapsuleLayout? layout)
    {
        Appearance = Appearance with
        {
            Layout = LayoutSettings with { Capsules = layout is null ? null : HudCapsuleLayout.Normalize(layout) }
        };
        ApplyComponentVisibility();
        OnMediaChanged(_lastMedia ?? _media.Current);
    }

    private void RefreshCapsuleProjection()
    {
        if (_capsuleId is not null || _islandOwner is not null || LayoutSettings.Capsules is not { } layout)
        {
            _presentedComponents = null;
            _presentationLayout = null;
            SetPresentationOrder(_capsuleItems, _capsuleRows);
            return;
        }
        var projection = ProjectCapsule(Appearance, Components, layout.Islands[0]);
        _presentedComponents = projection.Components;
        _presentationLayout = projection.Appearance.Layout;
        SetPresentationOrder(projection.Items, projection.Rows);
    }

    private void SetPresentationOrder(string? items, string? rows)
    {
        if (_presentationItemsKey != items)
        {
            _presentationItemsKey = items;
            PresentationCapsuleItems = items?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        }
        if (_presentationRowsKey != rows)
        {
            _presentationRowsKey = rows;
            PresentationRows = rows?.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(row => row.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        }
    }

    private bool PresentsChart(string metric) => PresentedComponents.Charts &&
        (PresentationCapsuleItems is { } items
            ? items.Contains(ChartId(metric))
            : MetricEnabled(PresentedComponents, metric));

    private static string ChartId(string metric) => metric switch
    {
        "cpu" => "cpu-chart", "gpu" => "gpu-chart", "memory" => "memory-chart", "fps" => "fps-chart", _ => string.Empty
    };

    private static bool MetricEnabled(ComponentSettings components, string metric) => metric switch
    {
        "cpu" => components.Cpu, "gpu" => components.Gpu, "memory" => components.Memory, "fps" => components.Fps, _ => false
    };

    internal static (AppearanceSettings Appearance, ComponentSettings Components, string Items, string? Rows) ProjectCapsule(
        AppearanceSettings appearance, ComponentSettings components, HudCapsule capsule)
    {
        string[] items = capsule.GetItems();
        bool primary = capsule.Id == HudCapsuleLayout.MainId;
        bool cpu = items.Contains("cpu");
        bool gpu = items.Contains("gpu");
        bool memory = items.Contains("memory");
        var projected = components with
        {
            Music = items.Contains("music") && components.Music,
            Volume = items.Contains("volume") && components.Volume,
            Time = items.Contains("time") && components.Time,
            Date = items.Contains("date") && components.Date,
            Battery = items.Contains("battery") && components.Battery,
            Cpu = cpu && components.Cpu,
            CpuTemperature = cpu && components.CpuTemperature,
            Gpu = gpu && components.Gpu,
            GpuTemperature = gpu && components.GpuTemperature,
            Memory = memory && components.Memory,
            MemoryTemperature = memory && components.MemoryTemperature,
            Fps = items.Contains("fps") && components.Fps,
            OnePercentLow = items.Contains("low") && components.OnePercentLow,
            FrameTime = items.Contains("frame-time") && components.FrameTime
        };
        var layout = HudLayoutSettings.Normalize(appearance.Layout with
        {
            BlockOrder = string.Join(',', items.Select(HudCapsuleLayout.Group).Where(item => item is "header" or "metrics" or "music").Distinct()),
            MetricOrder = string.Join(',', items.Where(HudCapsuleLayout.IsMetric)),
            DetachedMusic = false,
            MusicPosition = null,
            Capsules = null
        });
        string presentedItems = string.Join(',', items.Where(item => !HudCapsuleLayout.IsChart(item) ||
            components.Charts && MetricEnabled(components, item[..^"-chart".Length])));
        return (appearance with { Layout = layout, Alerts = primary ? appearance.Alerts : AlertSettings.Default },
            projected, presentedItems, capsule.Rows);
    }

    private void SynchronizeCapsules(HudCapsuleLayout layout)
    {
        if (!IsVisible)
        {
            foreach (var child in _capsuleWindows.Values) child.Hide();
            return;
        }

        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capsule in layout.Islands)
        {
            if (capsule.Id == HudCapsuleLayout.MainId) continue;
            var (appearance, components, items, rows) = ProjectCapsule(Appearance with { ClickThrough = IsClickThrough }, Components, capsule);
            if (!(components.Music || components.Volume || components.Cpu || components.Gpu || components.Memory ||
                components.CpuTemperature || components.GpuTemperature || components.MemoryTemperature ||
                components.Fps || components.OnePercentLow || components.FrameTime || components.Time || components.Date ||
                components.Battery && (BatteryAvailable || IsCapsuleLayoutEditing) || items.Split(',').Any(HudCapsuleLayout.IsChart))) continue;

            retained.Add(capsule.Id);
            if (!_capsuleWindows.TryGetValue(capsule.Id, out var child))
            {
                child = new MainWindow(_media, _system, new CapsulePlacementStore(this, capsule.Id),
                    new CapsuleAppearanceStore(appearance), new CapsuleComponentStore(components),
                    new AutostartService(readOnly: true), islandOwner: this, capsuleId: capsule.Id, capsuleItems: items, capsuleRows: rows)
                {
                    Owner = this,
                    Title = "Luma Island · Капсула",
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Topmost = Topmost,
                    Opacity = Opacity,
                    IsEnabled = !IsProfileEditorOpen
                };
                _capsuleWindows.Add(capsule.Id, child);
                child.Closed += OnCapsuleClosed;
            }
            bool appearanceChanged = child.Appearance != appearance;
            if (appearanceChanged || child.Components != components || child._capsuleItems != items || child._capsuleRows != rows || !child.IsLoaded)
            {
                child.Appearance = appearance;
                child.Components = components;
                child._capsuleItems = items;
                child._capsuleRows = rows;
                child._lastPerformance = _lastPerformance;
                child._lastBattery = _lastBattery;
                child._lastFrameMetrics = _lastFrameMetrics;
                child.ApplyComponentVisibility();
                if (appearanceChanged) child.ApplyAppearance();
                child.OnMediaChanged(_lastMedia ?? _media.Current);
                if (components.Fps || components.OnePercentLow || components.FrameTime || child.PresentsChart("fps"))
                    child.ApplyFpsPresentation(_lastFrameMetrics);
                child._savedAppearance = appearance;
            }
            else if (components.Battery && child.RefreshBatteryWidget()) child.RefreshComponentSize();
            child.RefreshCapsuleEditChrome();
            if (!child.IsVisible) child.Show();
            if (child._placement is { } placement)
            {
                var position = CapsulePlacement(capsule.Id);
                if (placement.Settings != position) placement.ApplySettings(position);
            }
            if (child.IsClickThrough != IsClickThrough)
            {
                if (IsClickThrough) child.PrepareForIslandClickThrough();
                if (child.TrySetClickThroughWindowStyle(IsClickThrough)) child.IsClickThrough = IsClickThrough;
                else SetClickThroughCore(false, persist: false);
            }
        }

        foreach (string id in _capsuleWindows.Keys.Where(id => !retained.Contains(id)).ToArray()) CloseCapsule(id);
    }

    private PlacementSettings CapsulePlacement(string id)
    {
        var capsule = LayoutSettings.Capsules?.Islands.FirstOrDefault(item => item.Id == id);
        var position = capsule?.Position;
        if (position is null && _capsuleWindows.TryGetValue(id, out var window))
            return CreateAutomaticMusicPlacement(window) with { Locked = IsPositionLocked };
        return new PlacementSettings(position?.MonitorId ?? _placement?.Settings.MonitorId, IslandPlacement.Custom,
            position?.XFraction ?? .5, position?.YFraction ?? .22, Locked: IsPositionLocked);
    }

    private bool SaveCapsulePlacement(string id, PlacementSettings settings)
    {
        if (_closed || IsProfileEditorOpen || LayoutSettings.Capsules is not { } layout) return false;
        var fractions = PlacementMath.Fractions(PlacementStore.Normalize(settings));
        var position = new HudIslandPosition(settings.MonitorId, fractions.X, fractions.Y);
        Appearance = Appearance with { Layout = LayoutSettings with { Capsules = layout.WithPosition(id, position) } };
        if (IsCapsuleLayoutEditing) return true;
        SaveAppearance();
        return Appearance == _savedAppearance;
    }

    private void CloseCapsule(string id)
    {
        if (!_capsuleWindows.Remove(id, out var child)) return;
        child.Closed -= OnCapsuleClosed;
        child.Close();
    }

    private void CloseCapsules()
    {
        foreach (string id in _capsuleWindows.Keys.ToArray()) CloseCapsule(id);
    }

    private void OnCapsuleClosed(object? sender, EventArgs args)
    {
        if (sender is not MainWindow { _capsuleId: { } id } child) return;
        child.Closed -= OnCapsuleClosed;
        _capsuleWindows.Remove(id);
        NotifyPresentationVisibilityChanged();
    }

    private sealed class CapsuleAppearanceStore(AppearanceSettings appearance) : AppearanceStore
    {
        public override AppearanceSettings Load() => appearance;
        public override bool Save(AppearanceSettings settings) => true;
    }

    private sealed class CapsuleComponentStore(ComponentSettings components) : ComponentStore
    {
        public override ComponentSettings Load() => components;
        public override bool Save(ComponentSettings settings) => true;
    }

    private sealed class CapsulePlacementStore(MainWindow owner, string id) : PlacementStore
    {
        public override PlacementSettings Load() => owner.CapsulePlacement(id);
        public override bool Save(PlacementSettings settings) => owner.SaveCapsulePlacement(id, settings);
    }
}
