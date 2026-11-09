using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private CapsuleEdit? _capsuleEdit;
    private bool IsCapsuleLayoutEditing => (_islandOwner ?? this)._capsuleEdit is not null;
    private double CapsuleEditBarHeight => _islandOwner is null && IsCapsuleLayoutEditing ? 38 : 0;
    private HudCapsuleLayout CapsuleDraft => Appearance.Layout.Capsules!;

    private static readonly Dictionary<string, (Func<ComponentSettings, bool> Read, Func<ComponentSettings, bool, ComponentSettings> Write)> CapsuleWidgets = new(StringComparer.Ordinal)
    {
        ["cpu"] = (s => s.Cpu, (s, v) => s with { Cpu = v, CpuChart = s.CpuChart ?? (s.Charts ? s.ShowCpuChart : null) }),
        ["gpu"] = (s => s.Gpu, (s, v) => s with { Gpu = v, GpuChart = s.GpuChart ?? (s.Charts ? s.ShowGpuChart : null) }),
        ["memory"] = (s => s.Memory, (s, v) => s with { Memory = v, MemoryChart = s.MemoryChart ?? (s.Charts ? s.ShowMemoryChart : null) }),
        ["fps"] = (s => s.Fps, (s, v) => s with { Fps = v, FpsChart = s.FpsChart ?? (s.Charts ? s.ShowFpsChart : null) }),
        ["cpu-chart"] = (s => s.ShowCpuChart, (s, v) => s with { CpuChart = v }),
        ["gpu-chart"] = (s => s.ShowGpuChart, (s, v) => s with { GpuChart = v }),
        ["memory-chart"] = (s => s.ShowMemoryChart, (s, v) => s with { MemoryChart = v }),
        ["fps-chart"] = (s => s.ShowFpsChart, (s, v) => s with { FpsChart = v }),
        ["cpu-temp"] = (s => s.CpuTemperature, (s, v) => s with { CpuTemperature = v }),
        ["gpu-temp"] = (s => s.GpuTemperature, (s, v) => s with { GpuTemperature = v }),
        ["memory-temp"] = (s => s.MemoryTemperature, (s, v) => s with { MemoryTemperature = v }),
        ["low"] = (s => s.OnePercentLow, (s, v) => s with { OnePercentLow = v }),
        ["frame-time"] = (s => s.FrameTime, (s, v) => s with { FrameTime = v }),
        ["time"] = (s => s.Time, (s, v) => s with { Time = v }),
        ["battery"] = (s => s.Battery, (s, v) => s with { Battery = v }),
        ["date"] = (s => s.Date, (s, v) => s with { Date = v }),
        ["music"] = (s => s.Music, (s, v) => s with { Music = v }),
        ["volume"] = (s => s.Volume, (s, v) => s with { Volume = v })
    };

    private sealed record CapsuleEdit(HudProfileSettings Original, HudCapsuleLayout InitialDraft, bool RestoreClickThrough)
    {
        public bool SaveAttempted { get; set; }
    }

    public MenuItem CreateCapsuleEditMenuItem()
    {
        var item = new MenuItem { Header = "Редактировать HUD…" };
        QuickMenuTheme.SetIcon(item, QuickMenuIcon.Edit);
        item.Click += (_, _) => BeginCapsuleEdit();
        return item;
    }

    private void BeginCapsuleEdit()
    {
        VerifyAccess();
        if (_islandOwner is not null) { _islandOwner.BeginCapsuleEdit(); return; }
        if (_closed || _placement is null || IsProfileEditorOpen || IsHudDragging) return;
        bool restoreClickThrough = IsClickThrough;
        if (restoreClickThrough && !SetClickThroughCore(false, persist: false)) return;
        if (!IsVisible) RestoreFromTray();
        IslandSurface.ContextMenu.IsOpen = false;
        var original = CaptureHudSettings();
        var draft = original.Appearance.Layout.Capsules ?? HudCapsuleLayout.FromLegacy(original.Appearance.Layout);
        _capsuleEdit = new(original, draft, restoreClickThrough);
        ApplyCapsuleDraft(draft);
    }

    private void RefreshCapsuleEditChrome()
    {
        bool empty = IslandSurface.Visibility != Visibility.Visible;
        CapsuleMoveGrip.Visibility = Show(IsCapsuleLayoutEditing && (!empty || _islandOwner is null));
        CapsuleMoveGrip.IsEnabled = !IsPositionLocked;
        CapsuleMoveGrip.VerticalAlignment = empty ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        CapsuleMoveGrip.Height = empty ? CapsuleEditBar.Height : 36;
        CapsuleMoveGrip.Margin = empty ? new Thickness(1, 0, 0, 6) : new Thickness(1, 22, 0, 0);
        CapsuleEditBar.Visibility = Show(CapsuleEditBarHeight > 0);
        CapsuleEditHint.Text = empty ? "Вернуть блок сюда" : "Перетащите блок";
    }

    private void OnCapsuleGripDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsCapsuleLayoutEditing || _placement is null || IsPositionLocked || IsClickThrough) return;
        var owner = _islandOwner ?? this;
        owner.CancelCapsuleGesture();
        var before = _placement.Settings;
        e.Handled = true;
        _placement.BeginDrag();
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { _placement.EndDrag(persist: false); }
        if (owner.IsCapsuleLayoutEditing && _capsuleId is not null && before != _placement.Settings)
        {
            var after = _placement.Settings;
            owner.ApplyCapsuleDraft(owner.CapsuleDraft.WithPosition(_capsuleId,
                new HudIslandPosition(after.MonitorId, after.XFraction, after.YFraction)));
        }
    }

    private void MoveCapsuleItem(string item, string? targetId, int insertIndex, Point screenPoint)
    {
        if (_capsuleEdit is null || _placement is null) return;
        var previous = CapsuleDraft;
        if (targetId is not null)
        {
            var moved = previous.Move(item, targetId, insertIndex);
            if (moved != previous) ApplyCapsuleDraft(moved);
            return;
        }
        if (!double.IsFinite(screenPoint.X) || !double.IsFinite(screenPoint.Y)) return;

        // The provisional anchor selects the target monitor. Once the window exists,
        // capture its real size on that monitor so the drop respects DPI and HUD scale.
        var display = PlacementMath.NearestDisplay(new Rect(screenPoint, new Size(1, 1)), _placement.Displays);
        var initial = PositionAt(display, screenPoint, new Size(1, 1), 0);
        var detached = previous.Move(item, null, 0, initial);
        if (detached == previous) return;
        ApplyCapsuleDraft(detached);
        var island = detached.Islands.First(capsule => capsule.GetItems().Contains(item));
        var window = LayoutWindows().FirstOrDefault(entry => entry.Id == island.Id).Window;
        if (window is null || !window.IsVisible) return;
        var bounds = DisplayService.GetWindowBounds(window);
        var position = PositionAt(display, screenPoint, bounds.Size, 8 * DisplayService.GetWindowScale(window));
        var anchored = detached.WithPosition(island.Id, position);
        if (anchored != detached) ApplyCapsuleDraft(anchored);
    }

    private static HudIslandPosition PositionAt(DisplayInfo display, Point point, Size size, double margin)
    {
        var target = new Rect(point.X - size.Width / 2, point.Y - size.Height / 2, size.Width, size.Height);
        var fractions = PlacementMath.Capture(display.WorkArea, target, margin);
        return new(display.Id, fractions.X, fractions.Y);
    }

    private void MoveCapsuleItemToRow(string item, string targetId, int rowIndex, int columnIndex, bool newRow)
    {
        if (_capsuleEdit is null) return;
        var previous = CapsuleDraft;
        var moved = previous.MoveToRow(item, targetId, rowIndex, columnIndex, newRow);
        if (moved != previous) ApplyCapsuleDraft(moved);
    }

    private void OnCapsuleEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsCapsuleLayoutEditing || e.Key != Key.Escape) return;
        var owner = _islandOwner ?? this;
        if (owner.IsCapsuleWidgetPickerOpen) owner.CloseCapsuleWidgetPicker();
        else if (owner.IsCapsuleGestureActive) owner.CancelCapsuleGesture();
        else owner.EndCapsuleEdit(save: false);
        e.Handled = true;
    }

    private static bool CapsuleWidgetEnabled(ComponentSettings settings, string id) =>
        CapsuleWidgets.TryGetValue(id, out var widget) && widget.Read(settings);

    private void SetCapsuleWidget(string id, bool enabled)
    {
        if (_capsuleEdit is null || !CapsuleWidgets.TryGetValue(id, out var widget) || widget.Read(Components) == enabled) return;
        CancelCapsuleGesture();
        var previous = Components;
        Components = widget.Write(previous, enabled);
        ApplyCapsuleDraft(CapsuleDraft);
        NotifyCapsuleComponentsChanged(previous);
    }

    private void NotifyCapsuleComponentsChanged(ComponentSettings previous)
    {
        if (previous == Components) return;
        if (previous.Time != Components.Time || previous.Date != Components.Date || previous.Battery != Components.Battery)
            UpdateClockAndBattery();
        ComponentsChanged?.Invoke(Components);
    }

    private void OnCapsuleEditDone(object sender, RoutedEventArgs e) => EndCapsuleEdit(save: true);
    private void OnCapsuleEditCancel(object sender, RoutedEventArgs e) => EndCapsuleEdit(save: false);
    private void OnCapsuleEditReset(object sender, RoutedEventArgs e) => ResetCapsuleDraft();

    private void ResetCapsuleDraft()
    {
        if (_capsuleEdit is null) return;
        CancelCapsuleGesture();
        ApplyCapsuleDraft(HudCapsuleLayout.FromLegacy(HudLayoutSettings.Default));
    }

    private void EndCapsuleEdit(bool save)
    {
        if (_capsuleEdit is not { } edit || _placement is null) return;
        CloseCapsuleWidgetPicker();
        CancelCapsuleGesture();
        var draft = CaptureHudSettings();
        if (CapsuleDraft == edit.InitialDraft)
            draft = draft with { Appearance = draft.Appearance with { Layout = edit.Original.Appearance.Layout } };
        if (save && (draft != edit.Original || edit.SaveAttempted))
        {
            var previous = edit.SaveAttempted ? null : edit.Original;
            bool saved = SaveCapsuleEdit(draft, previous);
            edit.SaveAttempted = !saved && !SaveCapsuleEdit(edit.Original, previous is null ? null : draft);
            RecordProfileSaveResult(saved);
            if (!saved)
            {
                ShowProfileSaveWarning();
                return;
            }
            _savedAppearance = draft.Appearance;
        }
        else if (!save && edit.SaveAttempted)
        {
            bool restored = SaveCapsuleEdit(edit.Original, previous: null);
            RecordProfileSaveResult(restored);
            if (!restored) ShowProfileSaveWarning();
        }

        var next = save ? draft : edit.Original;
        var previousComponents = Components;
        _capsuleEdit = null;
        Appearance = next.Appearance;
        Components = next.Components;
        if (_placement.Settings != next.Placement) _placement.ApplySettings(next.Placement);
        ApplyCapsuleDraft(Appearance.Layout.Capsules);
        if (edit.RestoreClickThrough && InputRecoveryAvailable) SetClickThroughCore(true, persist: false);
        NotifyCapsuleComponentsChanged(previousComponents);
        ProfileInteractionEnded?.Invoke();
    }

    private bool SaveCapsuleEdit(HudProfileSettings snapshot, HudProfileSettings? previous) => _profiles is not null
        ? _profiles.CaptureCurrent(snapshot)
        : (previous?.Appearance == snapshot.Appearance || _appearanceStore.Save(snapshot.Appearance)) &
          (previous?.Components == snapshot.Components || _componentStore.Save(snapshot.Components)) &
          (previous?.Placement == snapshot.Placement || _placementStore.Save(snapshot.Placement));
}
