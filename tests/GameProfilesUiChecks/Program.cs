using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Luma.Island;
using Luma.Island.Services;

internal static partial class Program
{
    private static readonly List<string> Checks = new();
    private static string Output = string.Empty;
    private static readonly string FirstId = "0bcb72c8-3a2d-4e49-8752-e71caab4f3dc";
    private static readonly string SecondId = "5d3a83ad-82ba-43e4-a502-91650ee2b19d";
    private static GameProfileSettings Fixture => new(false, new[]
    {
        new GameProfile(FirstId, "Counter-Strike 2", @"D:\Games\Counter-Strike 2\cs2.exe", HudProfileSettings.Default),
        new GameProfile(SecondId, "Cyberpunk 2077", @"D:\Games\Cyberpunk 2077\Cyberpunk2077.exe", HudProfileSettings.Default)
    });
    private static HudProfileSettings Current => new(
        new AppearanceSettings(IslandAppearance.Glass, .35, true, true, 1.25),
        ComponentSettings.Default with { Cpu = true, Gpu = true, Memory = true, Fps = true, CpuTemperature = true,
            GpuTemperature = true, MemoryTemperature = true, Charts = true },
        new PlacementSettings(Placement: IslandPlacement.TopLeft, Locked: true));

    [STAThread]
    private static int Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(FindRepository(), "work", "checks", "GameProfilesUiChecks"));
        Directory.CreateDirectory(Output);
        string? failure = null;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(); }
            catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(exception); }
            finally
            {
                File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, checks = Checks.Count, sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GameProfilesWindow).Assembly.Location))),
                    checksPassed = Checks, demonstrationData = true,
                    isolation = "Invisible nonactivating HWNDs; dialog rendering is offscreen. Synthetic game paths and isolated stores; no file picker, real user input, process monitor, media session, hardware or service operations. Real preference hashes are checked.",
                    failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failure is null ? 0 : 1);
            }
        }));
        app.Run();
        Console.WriteLine(failure is null ? $"PASS: {Checks.Count} game-profile manager UI checks." : "FAIL: game-profile manager UI checks.");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        Dictionary<string, string?> preferences = RealPreferences();
        var original = Fixture;
        var cancelled = NewWindow(original, Current, FirstId);
        bool? cancelResult = await RunDialog(cancelled, async window =>
        {
            var auto = Named<CheckBox>(window, "AutoSwitchCheckBox");
            var list = Named<ListBox>(window, "ProfileList");
            var name = Named<TextBox>(window, "ProfileNameBox");
            Check(auto.IsChecked == false && list.Items.Count == 2, "Manager opens with the persisted automatic choice and profile list");
            Check(list.SelectedIndex == 0 && name.Text == "Counter-Strike 2", "The active profile is initially selected");
            Check(Named<TextBox>(window, "ProfilePathBox").IsReadOnly, "Executable bindings are selected through the file picker rather than typed accidentally");
            Check(Named<Button>(window, "CancelButton").IsCancel && Named<Button>(window, "SaveButton").IsDefault,
                "The dialog retains native Escape/cancel and Enter/save semantics");
            name.Text = "Counter-Strike · changed draft";
            await Settle();
            list.SelectedIndex = 1;
            await Settle();
            list.SelectedIndex = 0;
            await Settle();
            Check(name.Text == "Counter-Strike · changed draft", "A rename survives selection changes without a lost edit");
            Check(original.Profiles[0].Name == "Counter-Strike 2", "Editing the draft does not mutate the supplied immutable model");
            auto.IsChecked = true;
            Click(Named<Button>(window, "CopyCurrentHudButton"));
            await Settle();
            Check(Named<TextBlock>(window, "ProfileSummary").Text.Contains("125%", StringComparison.Ordinal), "Copy-current previews the requested scale in the draft summary");
            Check(window.Settings.Enabled == false && window.Settings.Profiles[0].Settings.Appearance.Scale == 1,
                "An unaccepted draft does not become the result settings");
            name.Text = "Counter-Strike 2 · HUD";
            await Settle();
            await CaptureLayout(window, 720, 570, "profiles-manager.png");
            await CaptureLayout(window, 620, 420, "profiles-manager-small.png");
            window.Close();
        });
        Check(cancelResult != true && cancelled.Settings.Enabled == false && cancelled.Settings.Profiles[0].Name == original.Profiles[0].Name,
            "Closing the dialog discards checkbox, rename and copied-HUD draft changes");

        var accepted = NewWindow(Fixture, Current, SecondId);
        bool? saveResult = await RunDialog(accepted, async window =>
        {
            var list = Named<ListBox>(window, "ProfileList");
            var name = Named<TextBox>(window, "ProfileNameBox");
            var save = Named<Button>(window, "SaveButton");
            Check(list.SelectedIndex == 1 && name.Text == "Cyberpunk 2077", "A different active profile selects the corresponding editor");
            name.Text = string.Empty;
            await Settle();
            Click(save);
            Check(window.IsVisible && Named<TextBlock>(window, "StatusText").Text.Contains("название", StringComparison.OrdinalIgnoreCase),
                "An empty name is reported inline without accepting the dialog");
            name.Text = "  Night City  ";
            await Settle();
            object draft = list.SelectedItem;
            string oldPath = GetDraft<string>(draft, "ExecutablePath");
            SetDraft(draft, "ExecutablePath", Fixture.Profiles[0].ExecutablePath.ToUpperInvariant());
            Click(save);
            Check(window.IsVisible && window.Settings.Profiles[1].Name == "Cyberpunk 2077", "Duplicate executable paths, including different casing, cannot be saved");
            SetDraft(draft, "ExecutablePath", "relative.exe");
            Click(save);
            Check(window.IsVisible, "A nonabsolute executable binding cannot be accepted");
            SetDraft(draft, "ExecutablePath", oldPath);
            Named<CheckBox>(window, "AutoSwitchCheckBox").IsChecked = true;
            Click(Named<Button>(window, "CopyCurrentHudButton"));
            await Settle();
            Click(save);
        });
        Check(saveResult == true && accepted.Settings.Enabled, "Saving accepts the complete local automatic-profile choice");
        GameProfile saved = accepted.Settings.Profiles.Single(profile => profile.Id == SecondId);
        Check(saved.Name == "Night City" && saved.ExecutablePath == Fixture.Profiles[1].ExecutablePath, "Accepted renames are trimmed and keep the selected executable binding");
        Check(saved.Settings.Appearance.Scale == 1.25 && saved.Settings.Appearance.Compact && saved.Settings.Appearance.GlassOpacity == .35,
            "Copy-current retains scale, compact density and transparency together");
        Check(saved.Settings.Components == Current.Components && saved.Settings.Placement == Current.Placement,
            "Copy-current retains all components and monitor-aware placement");
        Check(!saved.Settings.Appearance.ClickThrough, "Game profiles do not capture the application's global click-through recovery mode");
        Check(accepted.Settings.Profiles.Single(profile => profile.Id == FirstId).Settings == HudProfileSettings.Default,
            "Updating one profile leaves the other snapshot unchanged");

        var removal = NewWindow(Fixture, Current, FirstId);
        await RunDialog(removal, async window =>
        {
            Click(Named<Button>(window, "RemoveProfileButton"));
            await Settle();
            Check(Named<ListBox>(window, "ProfileList").Items.Count == 1 && window.Settings.Profiles.Length == 2,
                "Removing a profile is a local draft operation before Save");
            Click(Named<Button>(window, "SaveButton"));
        });
        Check(removal.Settings.Profiles.Length == 1 && removal.Settings.Profiles[0].Id == SecondId, "Save commits the deliberate profile removal to the returned model");

        var empty = NewWindow(GameProfileSettings.Default, Current, null);
        await RunDialog(empty, window =>
        {
            Check(Named<TextBlock>(window, "NoProfilesMessage").Visibility == Visibility.Visible &&
                Named<StackPanel>(window, "ProfileEditor").Visibility == Visibility.Collapsed &&
                !Named<Button>(window, "RemoveProfileButton").IsEnabled,
                "An empty list exposes a clear empty state without an unusable editor or delete action");
            window.Close();
            return Task.CompletedTask;
        });
        await RunRoutingChecks();
        Check(preferences.OrderBy(item => item.Key).SequenceEqual(RealPreferences().OrderBy(item => item.Key)),
            "All dialog operations leave real appearance, placement, component and profile preference files unchanged");
    }

    private static GameProfilesWindow NewWindow(GameProfileSettings settings, HudProfileSettings current, string? active) =>
        new(settings, current, active)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
            Opacity = 0, ShowActivated = false, Topmost = false
        };

    private static async Task<bool?> RunDialog(GameProfilesWindow window, Func<GameProfilesWindow, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Loaded += async (_, _) =>
        {
            try
            {
                DisplayService.MoveWindow(window, -30000, -30000);
                await Settle();
                Check(DisplayService.GetWindowBounds(window).Left < -10000 && !window.ShowActivated && window.Opacity == 0,
                    "The manager fixture remains offscreen and does not request activation");
                await action(window);
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { if (window.IsVisible) window.Close(); }
        };
        bool? result = window.ShowDialog();
        await completion.Task;
        return result;
    }

    private static async Task CaptureLayout(GameProfilesWindow window, double width, double height, string name)
    {
        window.Width = width; window.Height = height;
        await Settle();
        foreach (string controlName in new[] { "SaveButton", "CancelButton", "AddProfileButton", "RemoveProfileButton", "CopyCurrentHudButton" })
        {
            FrameworkElement control = Named<FrameworkElement>(window, controlName);
            Rect bounds = control.TransformToAncestor(window).TransformBounds(new Rect(new Point(), control.RenderSize));
            Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1 &&
                control.ActualWidth >= 30 && control.ActualHeight >= 30,
                $"{controlName} stays visible and unclipped at {width:0}x{height:0} DIP");
        }
        Rect save = Bounds(window, "SaveButton"), cancel = Bounds(window, "CancelButton"),
            add = Bounds(window, "AddProfileButton"), remove = Bounds(window, "RemoveProfileButton"), copy = Bounds(window, "CopyCurrentHudButton");
        Check(!save.IntersectsWith(cancel) && !add.IntersectsWith(remove), $"Sibling footer and list actions do not overlap at {width:0}x{height:0} DIP");
        Check(copy.Bottom < save.Top && copy.Bottom < cancel.Top, $"Copy-current stays above the persistent Save/Cancel footer at {width:0}x{height:0} DIP");
        Check(DisplayService.GetWindowBounds(window).Left < -10000, "Only the offscreen test dialog is made opaque for rendering");
        window.Opacity = 1;
        try
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 2), (int)Math.Ceiling(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(window); bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(Output, name)); encoder.Save(file);
        }
        finally { window.Opacity = 0; }
    }

    private static T Named<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name);
    private static Rect Bounds(GameProfilesWindow window, string name)
    {
        FrameworkElement control = Named<FrameworkElement>(window, name);
        return control.TransformToAncestor(window).TransformBounds(new Rect(new Point(), control.RenderSize));
    }
    private static T GetDraft<T>(object draft, string property) => (T)draft.GetType().GetProperty(property)!.GetValue(draft)!;
    private static void SetDraft(object draft, string property, object value) => draft.GetType().GetProperty(property)!.SetValue(draft, value);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    private static async Task Settle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Check(bool passed, string description) { if (!passed) throw new InvalidOperationException("FAIL: " + description); Checks.Add(description); }
    private static Dictionary<string, string?> RealPreferences()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaIsland");
        return new[] { "appearance.json", "components.json", "placement.json", "profiles.json" }.ToDictionary(name => name,
            name => File.Exists(Path.Combine(root, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))) : null);
    }
    private static string FindRepository()
    {
        for (DirectoryInfo? path = new(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "src", "Luma.Island", "MainWindow.xaml.cs"))) return path.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
