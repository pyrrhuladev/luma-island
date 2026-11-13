using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private CapsuleEdit? _capsuleEdit;
    private ContextMenu? _capsuleRemovalMenu;
    private bool IsCapsuleLayoutEditing => (_islandOwner ?? this)._capsuleEdit is not null;
    private double CapsuleEditBarHeight => _islandOwner is null && IsCapsuleLayoutEditing ? 38 : 0;
    private HudCapsuleLayout CapsuleDraft => Appearance.Layout.Capsules!;

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
        var draft = (original.Appearance.Layout.Capsules ?? HudCapsuleLayout.FromLegacy(original.Appearance.Layout))
            .ToTree(original.Components.HasWidget, LayoutSettings.MetricColumns, LayoutSpacing);
        _capsuleEdit = new(original, draft, restoreClickThrough);
        ApplyCapsuleDraft(draft);
        OpenLayoutEditorPanels();
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
        CapsuleEditHint.Text = empty ? "Перетащите сюда" : "Выберите компонент";
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
            owner.ApplyEditorLayout(owner.CapsuleDraft.WithPosition(_capsuleId,
                new HudIslandPosition(after.MonitorId, after.XFraction, after.YFraction)));
        }
        else if (before != _placement.Settings)
            owner.RecordCapsuleHistory(owner.CaptureHudSettings() with { Placement = before });
    }

    private void MoveCapsuleItem(string item, string? targetId, int insertIndex, Point screenPoint)
    {
        if (_capsuleEdit is null || _placement is null) return;
        var previous = CapsuleDraft;
        if (targetId is not null)
        {
            var moved = previous.Move(item, targetId, insertIndex);
            if (moved != previous) ApplyEditorLayout(moved);
            return;
        }
        if (!double.IsFinite(screenPoint.X) || !double.IsFinite(screenPoint.Y)) return;

        // The provisional anchor selects the target monitor. Once the window exists,
        // capture its real size on that monitor so the drop respects DPI and HUD scale.
        var display = PlacementMath.NearestDisplay(new Rect(screenPoint, new Size(1, 1)), _placement.Displays);
        var initial = PositionAt(display, screenPoint, new Size(1, 1), 0);
        var detached = previous.Move(item, null, 0, initial);
        if (detached == previous) return;
        PreviewCapsuleLayout(detached);
        var island = detached.Islands.First(capsule => capsule.GetItems().Contains(item));
        var window = LayoutWindows().FirstOrDefault(entry => entry.Id == island.Id).Window;
        if (window is null || !window.IsVisible) return;
        var bounds = DisplayService.GetWindowBounds(window);
        var position = PositionAt(display, screenPoint, bounds.Size, 8 * DisplayService.GetWindowScale(window));
        var anchored = detached.WithPosition(island.Id, position);
        if (anchored != detached) PreviewCapsuleLayout(anchored);
        CommitCapsuleGesture(previous);
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
        if (moved != previous) ApplyEditorLayout(moved);
    }

    private void OnCapsuleEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsCapsuleLayoutEditing) return;
        var owner = _islandOwner ?? this;
        if (owner.HandleLayoutEditorKey(e) || e.Key != Key.Escape) return;
        if (owner.IsCapsuleGestureActive) owner.CancelCapsuleGesture();
        else owner.EndCapsuleEdit(save: false);
        e.Handled = true;
    }

    private void AddCapsuleWidget(string kind)
    {
        if (_capsuleEdit is null) return;
        CancelCapsuleGesture();
        var parent = SelectedCapsuleNodeId is { } id ? CapsuleDraft.GetNode(id) : null;
        if (parent?.IsContainer != true) parent = parent is null ? null : CapsuleDraft.FindNodeParent(parent.Id);
        var capsule = parent is null ? CapsuleDraft.Islands[0] : CapsuleDraft.FindNodeCapsule(parent.Id)!;
        var updated = CapsuleDraft.AddNode(kind, capsule.Id, parent?.Id, int.MaxValue);
        if (updated != CapsuleDraft) ApplyCapsuleInstanceChange(updated);
        else _layoutPalette?.SetStatus("Достигнут предел компонентов или вложенности.");
    }

    private void RemoveCapsuleWidget(string id)
    {
        if (_capsuleEdit is null) return;
        CancelCapsuleGesture();
        var updated = CapsuleDraft.RemoveNode(id);
        if (updated != CapsuleDraft) ApplyCapsuleInstanceChange(updated);
    }

    private void ApplyCapsuleInstanceChange(HudCapsuleLayout layout)
    {
        CancelCapsuleGesture();
        ApplyEditorLayout(layout);
    }

    private static ComponentSettings ResolveWidgetComponents(ComponentSettings settings, HudCapsuleLayout layout)
    {
        var kinds = layout.Islands.SelectMany(capsule => capsule.GetItems()).Select(HudCapsuleLayout.KindOf).ToHashSet(StringComparer.Ordinal);
        return settings with
        {
            Music = kinds.Contains("music"), Volume = kinds.Contains("volume"), Time = kinds.Contains("time"),
            Date = kinds.Contains("date"), Battery = kinds.Contains("battery"), Cpu = kinds.Contains("cpu"),
            Gpu = kinds.Contains("gpu"), Memory = kinds.Contains("memory"), Fps = kinds.Contains("fps"),
            CpuTemperature = kinds.Contains("cpu-temp"), GpuTemperature = kinds.Contains("gpu-temp"),
            MemoryTemperature = kinds.Contains("memory-temp"), OnePercentLow = kinds.Contains("low"),
            FrameTime = kinds.Contains("frame-time"), CpuChart = kinds.Contains("cpu-chart"), GpuChart = kinds.Contains("gpu-chart"),
            MemoryChart = kinds.Contains("memory-chart"), FpsChart = kinds.Contains("fps-chart")
        };
    }

    private void OnCapsuleContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!IsCapsuleLayoutEditing || e.CursorLeft < 0 || CapsuleItemAt(Mouse.GetPosition(IslandSurface)) is not { } id) return;
        e.Handled = true;
        var owner = _islandOwner ?? this;
        owner.CloseCapsuleRemovalMenu();
        var menu = new ContextMenu { PlacementTarget = IslandSurface, Placement = PlacementMode.MousePoint };
        owner.SelectCapsuleNode(id);
        bool isRoot = CapsuleDraft.FindNodeParent(id) is null;
        var duplicate = new MenuItem { Header = "Дублировать", InputGestureText = "Ctrl+D", IsEnabled = !isRoot };
        duplicate.Click += (_, _) => { owner.CloseCapsuleRemovalMenu(); owner.DuplicateCapsuleSelection(); };
        var remove = new MenuItem { Header = "Удалить компонент", IsEnabled = !isRoot };
        remove.Click += (_, _) =>
        {
            owner.CloseCapsuleRemovalMenu();
            owner.RemoveCapsuleWidget(id);
        };
        menu.Items.Add(duplicate);
        menu.Items.Add(remove);
        QuickMenuTheme.Apply(menu);
        owner._capsuleRemovalMenu = menu;
        menu.IsOpen = true;
    }

    private void CloseCapsuleRemovalMenu()
    {
        if (_capsuleRemovalMenu is not { } menu) return;
        _capsuleRemovalMenu = null;
        menu.IsOpen = false;
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
        ApplyEditorLayout(CapsuleDraft.ResetInstances());
    }

    private void EndCapsuleEdit(bool save)
    {
        if (_capsuleEdit is not { } edit || _placement is null) return;
        CompleteCapsulePropertyEdit();
        CloseCapsuleRemovalMenu();
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
        CloseLayoutEditorPanels();
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
