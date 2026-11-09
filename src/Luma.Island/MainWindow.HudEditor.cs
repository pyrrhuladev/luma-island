using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Luma.Island.Services;
using Luma.Island.Settings;

namespace Luma.Island;

public partial class MainWindow
{
    private string? _lastSettingsSection;

    public bool IsHudEditorOpen { get; private set; }
    public bool IsSettingsOpen => IsHudEditorOpen;
    public MenuItem CreateSettingsMenuItem() => CreateHudEditorMenuItem();
    public void OpenSettings() => OpenHudEditor();

    public MenuItem CreateHudEditorMenuItem()
    {
        var item = new MenuItem { Header = "Настройки…" };
        QuickMenuTheme.SetIcon(item, QuickMenuIcon.Settings);
        item.Click += (_, _) => OpenHudEditor();
        return item;
    }

    public void OpenHudEditor()
    {
        VerifyAccess();
        if (_closed || _placement is null || IsProfileEditorOpen || IsHudDragging) return;
        SaveCurrentProfileEdits();
        IsHudEditorOpen = IsProfileEditorOpen = true;
        ProfileEditorVisibilityChanged?.Invoke(true);
        try
        {
            using var preview = new HudPreviewSession();
            var context = new SettingsEditorContext(_placement.Settings,
                _profiles?.BaseSettings ?? CaptureHudSettings(), GameProfileStore.Normalize(GameProfiles), ActiveGameProfileId,
                _placement.Displays.ToArray(), _autostart.Read(),
                owner => new SensorSetupWindow { Owner = owner }.ShowDialog(),
                () => Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }),
                DisplayService.GetDisplays);
            var editor = new HudEditorWindow(Appearance, Components,
                _profiles?.ActiveProfile?.Name ?? "Общий HUD", preview.Render, context) { Owner = this };
            double availableHeight = Placement.CurrentDisplay.WorkArea.Height / DisplayService.GetWindowScale(this) - 16;
            editor.MaxHeight = Math.Max(editor.MinHeight, availableHeight);
            editor.Height = Math.Min(editor.Height, editor.MaxHeight);
            double availableWidth = Placement.CurrentDisplay.WorkArea.Width / DisplayService.GetWindowScale(this) - 16;
            editor.MaxWidth = Math.Max(editor.MinWidth, availableWidth);
            editor.Width = Math.Min(editor.Width, editor.MaxWidth);
            if (_lastSettingsSection is { } section) editor.SelectSection(section);
            bool accepted = editor.ShowDialog() == true;
            _lastSettingsSection = editor.SelectedSectionId;
            if (accepted)
            {
                bool saved = editor.DraftApplication is { } application
                    ? ApplySettingsDraft(editor.DraftAppearance, editor.DraftComponents, application, context.ActiveProfileId)
                    : ApplyHudEditorSettings(editor.DraftAppearance, editor.DraftComponents);
                if (!saved) ShowProfileSaveWarning();
            }
        }
        finally
        {
            IsHudEditorOpen = IsProfileEditorOpen = false;
            ProfileEditorVisibilityChanged?.Invoke(false);
        }
    }

    public bool ApplySettingsDraft(AppearanceSettings appearance, ComponentSettings components,
        SettingsApplicationDraft application, string? expectedActiveProfileId)
    {
        VerifyAccess();
        if (_closed || _placement is null) return false;
        if (_profiles is not null && _profiles.ActiveProfileId != expectedActiveProfileId)
        {
            App.Log(new InvalidOperationException("The settings target changed while its draft was open."));
            return false;
        }
        var edited = new HudProfileSettings(AppearanceStore.Normalize(appearance with { ClickThrough = Appearance.ClickThrough }),
            components, PlacementStore.Normalize(application.Placement));
        bool saved;
        if (_profiles is not null)
        {
            var plan = SettingsCommitPlan.Create(_profiles.BaseSettings, expectedActiveProfileId, edited, application.Profiles);
            saved = _profiles.ReplaceSettings(plan.Profiles);
            _profiles.Activate(plan.ActiveProfileId);
            using (_profiles.SuppressWrites()) ApplySettingsView(plan.VisibleHud);
            if (plan.SaveHudAsGlobal || plan.ActiveProfileId is not null)
                saved &= _profiles.CaptureCurrent(CaptureHudSettings());
        }
        else
        {
            ApplySettingsView(edited);
            saved = _appearanceStore.Save(Appearance) & _componentStore.Save(Components) & _placementStore.Save(_placement.Settings);
        }
        if (saved) _savedAppearance = Appearance;
        if (application.AutostartChanged)
        {
            try
            {
                var startup = _autostart.Read();
                if (startup.Error is not null) throw new InvalidOperationException(startup.Error);
                if (startup.Registered != application.AutostartRegistered)
                    _autostart.SetEnabled(application.AutostartRegistered);
            }
            catch (Exception error) { App.Log(error); saved = false; }
        }
        RecordProfileSaveResult(saved);
        ComponentsChanged?.Invoke(Components);
        ProfileConfigurationChanged?.Invoke(GameProfiles);
        return saved;
    }

    private void ApplySettingsView(HudProfileSettings settings)
    {
        Appearance = AppearanceStore.Normalize(settings.Appearance with { ClickThrough = Appearance.ClickThrough });
        Components = settings.Components;
        ResetAlerts();
        _placement?.ApplySettings(settings.Placement);
        UpdateClockAndBattery();
        ApplyComponentVisibility();
        ApplyAppearance();
        OnMediaChanged(_lastMedia ?? _media.Current);
    }

    public bool ApplyHudEditorSettings(AppearanceSettings appearance, ComponentSettings components)
    {
        VerifyAccess();
        if (_closed) return false;
        // Input recovery remains a separate global preference, outside the editor's draft.
        Appearance = AppearanceStore.Normalize(appearance with { ClickThrough = Appearance.ClickThrough });
        Components = components ?? ComponentSettings.Default;
        ResetAlerts();
        UpdateClockAndBattery();
        ApplyComponentVisibility();
        ApplyAppearance();
        OnMediaChanged(_lastMedia ?? _media.Current);
        bool saved = _appearanceStore.Save(Appearance) & _componentStore.Save(Components);
        if (saved) _savedAppearance = Appearance;
        saved &= SaveCurrentProfileEdits();
        ComponentsChanged?.Invoke(Components);
        return saved;
    }

    public static BitmapSource RenderHudPreview(AppearanceSettings appearance, ComponentSettings components)
    {
        using var preview = new HudPreviewSession();
        return preview.Render(appearance, components);
    }

    private sealed class HudPreviewSession : IDisposable
    {
        private readonly MediaService _media = new();
        private readonly SystemService _system = new();
        private readonly MainWindow _window;
        private readonly FrameworkElement _content;
        private HudPreviewSession? _musicPreview;
        private HudPreviewSession? _capsulePreview;

        public HudPreviewSession(string? capsuleId = null)
        {
            // A never-shown visual tree needs no media session, SDK, collector, tray or native HWND.
            _system.Dispose();
            _window = new MainWindow(_media, _system, new PreviewPlacementStore(),
                new PreviewAppearanceStore(), new PreviewComponentStore(), new AutostartService(readOnly: true), capsuleId: capsuleId);
            _content = (FrameworkElement)_window.Content;
            _window.Content = null;
            _content.Resources = _window.Resources;
            NameScope.SetNameScope(_content, NameScope.GetNameScope(_window));
        }

        public BitmapSource Render(AppearanceSettings appearance, ComponentSettings components) =>
            Render(appearance, components, capsuleItems: null);

        private BitmapSource Render(AppearanceSettings appearance, ComponentSettings components, string? capsuleItems)
        {
            _window._capsuleItems = capsuleItems;
            _window.Appearance = AppearanceStore.Normalize(appearance);
            _window.Components = components;
            _window.ApplyComponentVisibility();
            _window.ApplyAppearance();
            _window.OnMediaChanged(new MediaSnapshot("Вечерняя сессия", "Пример · предпросмотр", true, true, true, true, true, null));
            _window.ApplyPerformance(new PerformanceSnapshot(43, 76, 48, 16UL << 30, 32UL << 30,
                GpuTemperatureCelsius: 68, CpuTemperatureCelsius: 62, MemoryTemperatureCelsius: 51));
            _window.ApplyFps(new FpsSnapshot(144, "Предпросмотр", "Пример данных", 120, 6.9));
            _window.CompactClock.Text = "21:48";
            _window.ResetAlerts();
            var alerts = _window.Appearance.Alerts;
            if (alerts.NeedsCpuTemperature || alerts.NeedsGpuTemperature || alerts.NeedsMemoryTemperature || alerts.NeedsFrames)
            {
                _window.QuietAlertIndicator.Visibility = Visibility.Visible;
                _window.QuietAlertIndicator.Fill = Brushes.Goldenrod;
            }
            var transform = new ScaleTransform(_window.EffectiveHudScale, _window.EffectiveHudScale);
            transform.Freeze();
            _content.LayoutTransform = transform;
            var size = new Size(_window.Width, _window.Height);
            _content.Measure(size);
            _content.Arrange(new Rect(size));
            _content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(_content);
            bitmap.Freeze();
            if (_window.Appearance.Layout.Capsules is { } capsules)
            {
                var pictures = new List<BitmapSource>();
                if (_window.HasCapsuleContent) pictures.Add(bitmap);
                foreach (var capsule in capsules.Islands.Skip(1))
                {
                    var projected = ProjectCapsule(_window.Appearance, components, capsule);
                    _capsulePreview ??= new HudPreviewSession(capsuleId: "preview");
                    BitmapSource picture = _capsulePreview.Render(projected.Appearance, projected.Components, projected.Items);
                    if (_capsulePreview._window.HasCapsuleContent) pictures.Add(picture);
                }
                return pictures.Count == 0 ? bitmap : Compose(pictures);
            }
            if (_window.Appearance.Layout.DetachedMusic && (components.Music || components.Volume))
            {
                // Composite another never-shown tree. A settings preview must not create
                // a real floating window or connect another media/monitoring source.
                _musicPreview ??= new HudPreviewSession();
                var musicAppearance = _window.Appearance with
                {
                    Layout = _window.Appearance.Layout with { DetachedMusic = false },
                    Alerts = AlertSettings.Default
                };
                var musicComponents = new ComponentSettings(Music: components.Music, Volume: components.Volume,
                    Time: false, Date: false, Battery: false);
                BitmapSource music = _musicPreview.Render(musicAppearance, musicComponents);
                return Compose([bitmap, music]);
            }
            return bitmap;
        }

        private static BitmapSource Compose(IReadOnlyList<BitmapSource> pictures)
        {
            if (pictures.Count == 1) return pictures[0];
            const int gap = 12;
            int width = pictures.Max(picture => picture.PixelWidth);
            int height = pictures.Sum(picture => picture.PixelHeight) + gap * (pictures.Count - 1);
            var drawing = new DrawingVisual();
            using (DrawingContext context = drawing.RenderOpen())
            {
                int top = 0;
                foreach (BitmapSource picture in pictures)
                {
                    context.DrawImage(picture, new Rect((width - picture.PixelWidth) / 2d, top,
                        picture.PixelWidth, picture.PixelHeight));
                    top += picture.PixelHeight + gap;
                }
            }
            var composite = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            composite.Render(drawing);
            composite.Freeze();
            return composite;
        }

        public void Dispose()
        {
            _musicPreview?.Dispose();
            _musicPreview = null;
            _capsulePreview?.Dispose();
            _capsulePreview = null;
            _window.Close();
            _media.Dispose();
            _system.Dispose();
        }
    }

    private sealed class PreviewAppearanceStore : AppearanceStore
    {
        public override AppearanceSettings Load() => AppearanceSettings.Default;
        public override bool Save(AppearanceSettings settings) => true;
    }
    private sealed class PreviewComponentStore : ComponentStore
    {
        public override ComponentSettings Load() => ComponentSettings.Default;
        public override bool Save(ComponentSettings settings) => true;
    }
    private sealed class PreviewPlacementStore : PlacementStore
    {
        public override PlacementSettings Load() => PlacementSettings.Default;
        public override bool Save(PlacementSettings settings) => true;
    }
}
