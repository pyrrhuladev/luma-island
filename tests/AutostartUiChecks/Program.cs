using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Luma.Island;
using Luma.Island.Services;
using Microsoft.Win32;

internal static class Program
{
    private static readonly List<string> Checks = new();

    [STAThread]
    private static int Main(string[] args)
    {
        string repository = FindRepository();
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(repository, "work", "autostart-ui"));
        Directory.CreateDirectory(output);
        string id = Guid.NewGuid().ToString("N");
        const string registryPrefix = @"Software\LumaIsland\Tests\";
        string registryRoot = registryPrefix + id;
        string runKey = registryRoot + @"\Run";
        string approvalKey = registryRoot + @"\Approval";
        string fixtures = Path.Combine(output, id);
        Directory.CreateDirectory(fixtures);
        string executable = Path.Combine(fixtures, "Fixture With Spaces", "Luma.Island.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "Autostart UI test fixture. Never execute.");

        Application? application = null;
        MediaService? media = null;
        SystemService? system = null;
        MainWindow? window = null;
        bool windowClosed = false, registryCleaned = false;
        string? failure = null;
        string command = "\"" + executable + "\" --startup";
        try
        {
            Check(registryRoot.StartsWith(registryPrefix, StringComparison.Ordinal) && Guid.TryParseExact(id, "N", out _),
                "All registry writes are confined to the unique test subtree");
            using (Registry.CurrentUser.CreateSubKey(runKey, writable: true)) { }
            var autostart = new AutostartService(executable, runKey, approvalKey);
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            media = new MediaService(); // No SMTC initialization, collection or playback action.
            system = new SystemService(); // Read/callback only; never change volume or mute.
            window = new MainWindow(media, system,
                new PlacementStore(Path.Combine(fixtures, "placement.json")),
                new AppearanceStore(Path.Combine(fixtures, "appearance.json")),
                new ComponentStore(Path.Combine(fixtures, "components.json")), autostart);
            window.Closed += (_, _) => windowClosed = true;

            // Exercise the actual capsule's Loaded hook while the HWND stays hidden.
            new WindowInteropHelper(window).EnsureHandle();
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
            var surface = (Border)window.FindName("IslandSurface");
            Check(!surface.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Запускать вместе с Windows")),
                "Autostart preferences live in settings rather than the quick-action menu");
            MenuItem capsule = window.CreateAutostartMenu();
            MenuItem tray = window.CreateAutostartMenu();
            Check(!window.IsVisible, "The test never shows a window or starts the application");
            Check(capsule.IsCheckable && tray.IsCheckable && !ReferenceEquals(capsule, tray),
                "Capsule and tray factories expose distinct checkable items");
            Check(!capsule.IsChecked && !tray.IsChecked && capsule.IsEnabled && tray.IsEnabled,
                "Both menus initially show autostart disabled");

            Click(capsule);
            using (var run = Registry.CurrentUser.OpenSubKey(runKey))
            {
                Check(run?.GetValueKind(AutostartService.ValueName) == RegistryValueKind.String,
                    "Clicking the capsule writes an actual REG_SZ value");
                Check(Equals(run?.GetValue(AutostartService.ValueName), command),
                    "The registered executable is quoted and carries --startup");
            }
            Check(capsule.IsChecked && tray.IsChecked, "Enabling in the capsule refreshes both checkmarks");
            using (var approval = Registry.CurrentUser.OpenSubKey(approvalKey))
                Check(approval is null, "A normal click does not create an approval record");

            Click(tray);
            using (var run = Registry.CurrentUser.OpenSubKey(runKey))
                Check(run?.GetValue(AutostartService.ValueName) is null, "Clicking the other menu removes registration");
            Check(!capsule.IsChecked && !tray.IsChecked, "Disabling from the other menu refreshes both checkmarks");

            using (var run = Registry.CurrentUser.CreateSubKey(runKey, writable: true))
                run.SetValue(AutostartService.ValueName, command, RegistryValueKind.String);
            window.RefreshAutostartMenus();
            Check(capsule.IsChecked && tray.IsChecked, "External registration is reflected after refresh");

            using (var approval = Registry.CurrentUser.CreateSubKey(approvalKey, writable: true))
                approval.SetValue(AutostartService.ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            window.RefreshAutostartMenus(); // Never click while disabled by Windows: that opens Settings.
            Check(!capsule.IsChecked && !tray.IsChecked && capsule.IsEnabled && tray.IsEnabled,
                "External Windows-disabled state refreshes both menus without opening Settings");
            Check(capsule.ToolTip is string tip && tip.Contains("Windows", StringComparison.Ordinal),
                "Windows-disabled state has the expected explanatory tooltip");

            using (var approval = Registry.CurrentUser.OpenSubKey(approvalKey, writable: true))
                approval!.SetValue(AutostartService.ValueName, new byte[] { 0 }, RegistryValueKind.Binary);
            window.RefreshAutostartMenus();
            Check(!capsule.IsEnabled && !tray.IsEnabled && !capsule.IsChecked && !tray.IsChecked,
                "An unreadable external approval state disables both controls");

            using (var approval = Registry.CurrentUser.OpenSubKey(approvalKey, writable: true))
                approval!.DeleteValue(AutostartService.ValueName);
            window.RefreshAutostartMenus();
            Check(capsule.IsChecked && tray.IsChecked && capsule.IsEnabled && tray.IsEnabled,
                "Removing the external restriction restores both controls");
            Click(tray);
            Check(!capsule.IsChecked && !tray.IsChecked && !autostart.Read().Registered,
                "The final test state has registration switched off");

            window.Close();
            Check(windowClosed && !window.IsVisible, "The hidden WPF test window closes and releases its hooks");
            window = null;
        }
        catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(exception.Message); }
        finally
        {
            try { window?.Close(); system?.Dispose(); media?.Dispose(); application?.Shutdown(); }
            catch (Exception exception) { failure ??= exception.ToString(); }
            try
            {
                // Both the prefix and GUID were constructed above; no user startup key is ever opened.
                Registry.CurrentUser.DeleteSubKeyTree(registryRoot, throwOnMissingSubKey: false);
                using var remaining = Registry.CurrentUser.OpenSubKey(registryRoot);
                registryCleaned = remaining is null;
                if (!registryCleaned) failure ??= "Test registry subtree was not cleaned.";
            }
            catch (Exception exception) { failure ??= exception.ToString(); }
        }

        var assembly = typeof(MainWindow).Assembly;
        var report = new
        {
            passed = failure is null && registryCleaned && windowClosed, checks = Checks.Count, completedChecks = Checks,
            testedAssembly = assembly.GetName().Name, version = assembly.GetName().Version?.ToString(),
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            testRegistryRoot = "HKCU\\" + registryRoot, registryCleaned, windowClosed, windowWasNeverShown = true,
            realTrayCreated = false, settingsLaunchBranchInvoked = false, fixtureExecutable = executable, failure
        };
        File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(failure is null ? $"PASS: {Checks.Count} autostart UI checks; test registry cleaned." : "FAIL: autostart UI verification.");
        return failure is null && registryCleaned && windowClosed ? 0 : 1;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    private static void Check(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException("FAIL: " + description);
        Checks.Add(description);
    }
    private static string FindRepository()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "src", "Luma.Island", "MainWindow.xaml.cs"))) return folder.FullName;
        throw new DirectoryNotFoundException("Overlay repository root was not found.");
    }
}
