using System;
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
        CapsuleMoveGrip.Visibility = Show(IsCapsuleLayoutEditing);
        CapsuleMoveGrip.IsEnabled = !IsPositionLocked;
        CapsuleEditBar.Visibility = Show(CapsuleEditBarHeight > 0);
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

    private void OnCapsuleEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsCapsuleLayoutEditing || e.Key != Key.Escape) return;
        var owner = _islandOwner ?? this;
        if (owner.IsCapsuleGestureActive) owner.CancelCapsuleGesture();
        else owner.EndCapsuleEdit(save: false);
        e.Handled = true;
    }

    private void OnCapsuleEditDone(object sender, RoutedEventArgs e) => EndCapsuleEdit(save: true);
    private void OnCapsuleEditCancel(object sender, RoutedEventArgs e) => EndCapsuleEdit(save: false);

    private void EndCapsuleEdit(bool save)
    {
        if (_capsuleEdit is not { } edit || _placement is null) return;
        CancelCapsuleGesture();
        bool changed = CapsuleDraft != edit.InitialDraft || _placement.Settings != edit.Original.Placement;
        if (save && changed)
        {
            edit.SaveAttempted = true;
            if (!SaveCapsuleEdit(CaptureHudSettings(), edit.Original.Placement))
            {
                ShowProfileSaveWarning();
                return;
            }
            _savedAppearance = Appearance;
        }

        _capsuleEdit = null;
        if (!save || !changed)
        {
            var draftPlacement = _placement.Settings;
            Appearance = Appearance with { Layout = edit.Original.Appearance.Layout };
            _placement.ApplySettings(edit.Original.Placement);
            if (edit.SaveAttempted && !SaveCapsuleEdit(edit.Original, draftPlacement)) ShowProfileSaveWarning();
        }
        ApplyCapsuleDraft(Appearance.Layout.Capsules);
        if (edit.RestoreClickThrough && InputRecoveryAvailable) SetClickThroughCore(true, persist: false);
        ProfileInteractionEnded?.Invoke();
    }

    private bool SaveCapsuleEdit(HudProfileSettings snapshot, PlacementSettings oldPlacement)
    {
        bool saved = _profiles is not null
            ? _profiles.CaptureCurrent(snapshot)
            : _appearanceStore.Save(snapshot.Appearance) &
              (oldPlacement == snapshot.Placement || _placementStore.Save(snapshot.Placement));
        RecordProfileSaveResult(saved);
        return saved;
    }
}
