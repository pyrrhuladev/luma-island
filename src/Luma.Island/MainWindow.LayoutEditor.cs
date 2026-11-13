using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private LayoutEditorWindow? _layoutPalette;
    private LayoutEditorWindow? _layoutInspector;
    private readonly List<HudProfileSettings> _capsuleUndo = new();
    private readonly List<HudProfileSettings> _capsuleRedo = new();
    private string? _selectedCapsuleNodeId;
    private HudProfileSettings? _propertyEditOriginal;
    private string? SelectedCapsuleNodeId => (_islandOwner ?? this)._selectedCapsuleNodeId;
    private FrameworkElement? CapsuleEditorCaptureSurface => (_islandOwner ?? this)._layoutPalette?.CaptureSurface;

    private bool IsLayoutEditorWindowHandle(IntPtr handle) => handle != IntPtr.Zero &&
        (_layoutPalette is not null && new WindowInteropHelper(_layoutPalette).Handle == handle ||
         _layoutInspector is not null && new WindowInteropHelper(_layoutInspector).Handle == handle);

    private void SetLayoutEditorStatus(string? message)
    {
        _layoutPalette?.SetStatus(message);
        _layoutInspector?.SetStatus(message);
    }

    private void OpenLayoutEditorPanels()
    {
        _capsuleUndo.Clear();
        _capsuleRedo.Clear();
        _selectedCapsuleNodeId = null;
        _layoutPalette = CreateLayoutEditorPanel(inspector: false);
        _layoutInspector = CreateLayoutEditorPanel(inspector: true);
        _layoutPalette.PaletteDragRequested += BeginPaletteDrag;
        _layoutPalette.PaletteKeyboardRequested += AddCapsuleWidget;
        _layoutInspector.DuplicateRequested += DuplicateCapsuleSelection;
        _layoutInspector.RemoveRequested += RemoveCapsuleSelection;
        _layoutInspector.PropertiesChanged += UpdateCapsuleNodeProperties;
        _layoutInspector.PropertyGestureStarted += () => _propertyEditOriginal ??= CaptureHudSettings();
        _layoutInspector.PropertyGestureCompleted += CompleteCapsulePropertyEdit;
        PositionLayoutEditorPanels();
        SelectCapsuleNode(CapsuleDraft.Islands[0].Root?.Id);
        // Non-visible fixture and preview windows keep their editors non-visible too.
        if (IsVisible && Opacity > 0)
        {
            _layoutPalette.Show();
            _layoutInspector.Show();
        }
    }

    private LayoutEditorWindow CreateLayoutEditorPanel(bool inspector)
    {
        var panel = new LayoutEditorWindow(inspector)
        {
            Owner = this, Topmost = Topmost, Opacity = Opacity, ShowActivated = false
        };
        panel.UndoRequested += UndoCapsuleEdit;
        panel.RedoRequested += RedoCapsuleEdit;
        panel.FinishRequested += () => EndCapsuleEdit(save: true);
        panel.CancelRequested += () => EndCapsuleEdit(save: false);
        panel.EditorKeyPressed += e => OnCapsuleEditorKeyDown(panel, e);
        return panel;
    }

    private void PositionLayoutEditorPanels()
    {
        if (_placement is null || _layoutPalette is null || _layoutInspector is null) return;
        var area = _placement.CurrentDisplay.WorkArea;
        double scale = DisplayService.GetWindowScale(this);
        double left = area.Left / scale, top = area.Top / scale;
        double width = area.Width / scale, height = area.Height / scale;
        foreach (var panel in new[] { _layoutPalette, _layoutInspector })
        {
            panel.Height = Math.Max(240, Math.Min(720, height - 32));
            panel.MinHeight = Math.Min(360, panel.Height);
            panel.Top = top + Math.Max(8, (height - panel.Height) / 2);
        }
        _layoutPalette.Left = left + 16;
        _layoutInspector.Left = left + Math.Max(16, width - _layoutInspector.Width - 16);
    }

    private void CloseLayoutEditorPanels()
    {
        var palette = _layoutPalette;
        var inspector = _layoutInspector;
        _layoutPalette = _layoutInspector = null;
        palette?.CloseFromEditor();
        inspector?.CloseFromEditor();
        _selectedCapsuleNodeId = null;
        _propertyEditOriginal = null;
        _capsuleUndo.Clear();
        _capsuleRedo.Clear();
    }

    private void SelectCapsuleNode(string? id)
    {
        var owner = _islandOwner ?? this;
        if (!ReferenceEquals(owner, this)) { owner.SelectCapsuleNode(id); return; }
        if (_capsuleEdit is null) return;
        if (_selectedCapsuleNodeId != id) CompleteCapsulePropertyEdit();
        _selectedCapsuleNodeId = id is not null && CapsuleDraft.GetNode(id) is not null ? id : null;
        RefreshLayoutEditorState();
    }

    private void RefreshLayoutEditorState()
    {
        if (_capsuleEdit is null) return;
        var node = _selectedCapsuleNodeId is null ? null : CapsuleDraft.GetNode(_selectedCapsuleNodeId);
        if (node is null) _selectedCapsuleNodeId = null;
        var source = node is null ? null : CapsuleDraft.FindNodeCapsule(node.Id);
        var path = new List<string>();
        for (var ancestor = node; ancestor is not null; ancestor = CapsuleDraft.FindNodeParent(ancestor.Id))
            path.Insert(0, LayoutEditorWindow.KindLabel(ancestor.Kind));
        path.Insert(0, source?.Id == HudCapsuleLayout.MainId ? "Основная капсула" : source is null ? "Выберите объект на HUD" : "Капсула");
        _layoutInspector?.SetSelection(node, string.Join(" / ", path), source?.Root?.Id == node?.Id);
        _layoutPalette?.SetHistory(_capsuleUndo.Count > 0, _capsuleRedo.Count > 0);
        _layoutInspector?.SetHistory(_capsuleUndo.Count > 0, _capsuleRedo.Count > 0);
        foreach (var (_, window) in LayoutWindows()) window.RefreshCapsuleNodeSelection();
    }

    private void PreviewCapsuleLayout(HudCapsuleLayout layout)
    {
        if (_capsuleEdit is null || CapsuleDraft == layout) return;
        var previous = Components;
        ApplyCapsuleDraft(layout);
        NotifyCapsuleComponentsChanged(previous);
        foreach (var (_, window) in LayoutWindows()) if (window.IsVisible) window.UpdateLayout();
        RefreshLayoutEditorState();
    }

    private void CommitCapsuleGesture(HudCapsuleLayout before)
    {
        if (_capsuleEdit is null || before == CapsuleDraft) return;
        var original = CaptureHudSettings() with
        {
            Appearance = Appearance with { Layout = Appearance.Layout with { Capsules = before } },
            Components = ResolveWidgetComponents(Components, before)
        };
        RecordCapsuleHistory(original);
    }

    private void ApplyEditorLayout(HudCapsuleLayout layout)
    {
        if (_capsuleEdit is null || CapsuleDraft == layout) return;
        var original = CaptureHudSettings();
        PreviewCapsuleLayout(layout);
        RecordCapsuleHistory(original);
    }

    private void RecordCapsuleHistory(HudProfileSettings original)
    {
        if (CaptureHudSettings() == original) return;
        _capsuleUndo.Add(original);
        if (_capsuleUndo.Count > 100) _capsuleUndo.RemoveAt(0);
        _capsuleRedo.Clear();
        RefreshLayoutEditorState();
    }

    private void UndoCapsuleEdit() => RestoreCapsuleHistory(_capsuleUndo, _capsuleRedo);
    private void RedoCapsuleEdit() => RestoreCapsuleHistory(_capsuleRedo, _capsuleUndo);

    private void RestoreCapsuleHistory(List<HudProfileSettings> source, List<HudProfileSettings> destination)
    {
        if (_capsuleEdit is null) return;
        CompleteCapsulePropertyEdit();
        CancelCapsuleGesture();
        if (source.Count == 0) return;
        var previous = Components;
        var restored = source[^1];
        source.RemoveAt(source.Count - 1);
        destination.Add(CaptureHudSettings());
        Components = restored.Components;
        _placement?.ApplySettings(restored.Placement);
        ApplyCapsuleDraft(restored.Appearance.Layout.Capsules);
        NotifyCapsuleComponentsChanged(previous);
        RefreshLayoutEditorState();
    }

    private void CompleteCapsulePropertyEdit()
    {
        if (_propertyEditOriginal is not { } original) return;
        _propertyEditOriginal = null;
        RecordCapsuleHistory(original);
    }

    private void UpdateCapsuleNodeProperties(HudLayoutNode values)
    {
        if (_capsuleEdit is null) return;
        CancelCapsuleGesture();
        var updated = CapsuleDraft.UpdateNode(values.Id, node => node with
        {
            Columns = values.Columns, Gap = values.Gap, Padding = values.Padding,
            Alignment = values.Alignment, Sizing = values.Sizing
        });
        if (_propertyEditOriginal is null) ApplyEditorLayout(updated);
        else PreviewCapsuleLayout(updated);
    }

    private void DuplicateCapsuleSelection()
    {
        if (_capsuleEdit is null || SelectedCapsuleNodeId is not { } id) return;
        CancelCapsuleGesture();
        ApplyEditorLayout(CapsuleDraft.DuplicateNode(id));
    }

    private void RemoveCapsuleSelection()
    {
        if (_capsuleEdit is null || SelectedCapsuleNodeId is not { } id) return;
        RemoveCapsuleWidget(id);
    }

    private bool HandleLayoutEditorKey(KeyEventArgs e)
    {
        if (_capsuleEdit is null || e.OriginalSource is TextBox) return false;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.Z) UndoCapsuleEdit();
        else if (modifiers == ModifierKeys.Control && e.Key == Key.Y) RedoCapsuleEdit();
        else if (modifiers == ModifierKeys.Control && e.Key == Key.D) DuplicateCapsuleSelection();
        else if (modifiers == ModifierKeys.None && e.Key == Key.Delete) RemoveCapsuleSelection();
        else return false;
        e.Handled = true;
        return true;
    }
}
