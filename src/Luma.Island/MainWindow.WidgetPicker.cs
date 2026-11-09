using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Luma.Island.Services;

namespace Luma.Island;

public partial class MainWindow
{
    private CapsuleWidgetPicker? _capsuleWidgetPicker;
    private Popup? _capsuleWidgetPickerPopup;
    private bool IsCapsuleWidgetPickerOpen => _capsuleWidgetPickerPopup?.IsOpen == true;

    private void OnCapsuleWidgetPickerClick(object sender, RoutedEventArgs e)
    {
        if (!IsCapsuleLayoutEditing || _placement is null) return;
        if (IsCapsuleWidgetPickerOpen) { CloseCapsuleWidgetPicker(); return; }
        var picker = GetCapsuleWidgetPicker();
        picker.MaxHeight = Math.Max(80, Math.Min(410,
            _placement.CurrentDisplay.WorkArea.Height / DisplayService.GetWindowScale(this) - 24));
        picker.UpdateSelection(id => CapsuleWidgetEnabled(Components, id));
        _capsuleWidgetPickerPopup!.IsOpen = true;
    }

    private CapsuleWidgetPicker GetCapsuleWidgetPicker()
    {
        if (_capsuleWidgetPicker is { } current) return current;
        var picker = new CapsuleWidgetPicker();
        picker.WidgetChanged += (id, enabled) =>
        {
            SetCapsuleWidget(id, enabled);
            picker.UpdateSelection(widget => CapsuleWidgetEnabled(Components, widget));
        };
        picker.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            CloseCapsuleWidgetPicker();
            if (IsActive) CapsuleAddWidgetButton.Focus();
        };
        _capsuleWidgetPicker = picker;
        _capsuleWidgetPickerPopup = new Popup
        {
            Child = picker, PlacementTarget = CapsuleAddWidgetButton, Placement = PlacementMode.Top,
            VerticalOffset = -6, AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.None
        };
        _capsuleWidgetPickerPopup.Opened += (_, _) => { if (IsActive) picker.FocusFirstOption(); };
        return picker;
    }

    private void CloseCapsuleWidgetPicker()
    {
        if (_capsuleWidgetPickerPopup is { } popup) popup.IsOpen = false;
    }
}
