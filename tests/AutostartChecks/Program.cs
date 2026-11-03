using System;
using System.IO;
using Luma.Island.Services;
using Microsoft.Win32;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
    }
    private static bool Exists(string path) { using var key = Registry.CurrentUser.OpenSubKey(path); return key is not null; }
    private static object? Value(string path, string name = AutostartService.ValueName)
    { using var key = Registry.CurrentUser.OpenSubKey(path); return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
    private static void Set(string path, object value, RegistryValueKind kind, string name = AutostartService.ValueName)
    { using var key = Registry.CurrentUser.CreateSubKey(path, writable: true); key.SetValue(name, value, kind); }
    private static void Remove(string path, string name = AutostartService.ValueName)
    { using var key = Registry.CurrentUser.OpenSubKey(path, writable: true); key?.DeleteValue(name, false); }
    private static string Command(string path) => "\"" + path + "\" --startup";
    private static byte[] Approval(uint state)
    { var bytes = new byte[12]; BitConverter.GetBytes(state).CopyTo(bytes, 0); for (int i = 4; i < bytes.Length; i++) bytes[i] = (byte)(i + 17); return bytes; }
    private static void Rejected(Action action, string name)
    { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, name); }

    private static int Main(string[] args)
    {
        string directory = Path.GetFullPath(args.Length > 0 ? args[0] : "work/autostart-fix");
        Directory.CreateDirectory(directory);
        string id = Guid.NewGuid().ToString("N"), registryRoot = @"Software\LumaIsland\Tests\" + id;
        string runKey = registryRoot + @"\Run", approvalKey = registryRoot + @"\Approval";
        string fixtureRoot = Path.Combine(directory, "fixtures", id);
        string executable = Path.Combine(fixtureRoot, "новая версия с пробелами", "Luma.Island.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "inert test fixture; never executed");
        string oldPath = Path.Combine(fixtureRoot, "old portable folder", "Luma.Island.exe");
        var service = new AutostartService(executablePath: executable, runKey: runKey, approvalKey: approvalKey);
        string? failure = null;
        try
        {
            Check(!Exists(registryRoot), "GUID registry subtree starts absent");
            Check(service.Read() == new AutostartState(false, false), "missing registration defaults off");
            service.RefreshExecutablePath(); service.SetEnabled(false);
            Check(!Exists(runKey) && !Exists(approvalKey), "read/migration/disable do not create absent keys");
            service.SetEnabled(true);
            Check((string?)Value(runKey) == Command(executable), "quoted Unicode/space path with --startup");
            using (var key = Registry.CurrentUser.OpenSubKey(runKey))
                Check(key!.GetValueKind(AutostartService.ValueName) == RegistryValueKind.String, "command stored as REG_SZ");
            Check(service.Read().Enabled, "registration enabled without approval record");
            Check(new AutostartService(runKey: runKey, approvalKey: approvalKey, executablePath: executable).Read() == service.Read(),
                "fresh service reads registered state");
            Set(runKey, "keep this unrelated value", RegistryValueKind.String, "OtherApp");
            service.SetEnabled(false);
            Check(Value(runKey) is null && (string?)Value(runKey, "OtherApp") == "keep this unrelated value", "disable deletes only owned value");
            for (int cycle = 0; cycle < 3; cycle++)
            {
                service.SetEnabled(true); Check(service.Read().Enabled, "repeated enable " + cycle);
                service.SetEnabled(false); Check(!service.Read().Registered, "repeated disable " + cycle);
            }
            service.SetEnabled(true);
            Set(approvalKey, Approval(3), RegistryValueKind.Binary, "OtherApp");
            foreach (uint state in new uint[] { 2, 6, 3, 7 })
            {
                Set(approvalKey, Approval(state), RegistryValueKind.Binary);
                var actual = service.Read();
                bool disabled = state is 3 or 7;
                Check(actual.Registered && actual.Error is null && actual.DisabledByWindows == disabled && actual.Enabled == !disabled,
                    "approval state interpreted: " + state);
            }
            Remove(approvalKey);
            Check(service.Read().Enabled, "absent own approval is enabled despite other approval entries");
            foreach (var entry in new (object Data, RegistryValueKind Kind)[]
            {
                (Approval(4), RegistryValueKind.Binary), (Approval(uint.MaxValue), RegistryValueKind.Binary),
                (Array.Empty<byte>(), RegistryValueKind.Binary), (new byte[] { 3, 0, 0 }, RegistryValueKind.Binary),
                (new byte[] { 2, 0, 0, 0 }, RegistryValueKind.Binary), (Approval(2)[..11], RegistryValueKind.Binary),
                ("invalid approval", RegistryValueKind.String), (2, RegistryValueKind.DWord)
            })
            {
                Set(approvalKey, entry.Data, entry.Kind);
                var actual = service.Read();
                Check(!actual.Enabled && !string.IsNullOrWhiteSpace(actual.Error), "unknown/malformed approval is an error: " + entry.Kind);
            }
            foreach (uint state in new uint[] { 3, 7 })
            {
                byte[] approval = Approval(state);
                Set(runKey, Command(oldPath), RegistryValueKind.String);
                Set(approvalKey, approval, RegistryValueKind.Binary);
                service.RefreshExecutablePath();
                Check((string?)Value(runKey) == Command(executable), "owned stale/missing portable path migrates");
                Check(Value(approvalKey) is byte[] after && approval.AsSpan().SequenceEqual(after), "migration preserves Windows approval bytes");
                Check(service.Read().DisabledByWindows && !service.Read().Enabled, "migration does not bypass Windows disable");
            }
            foreach (string unrelated in new[] { "\"C:\\Windows\\notepad.exe\" --startup", Command(oldPath) + " --other", "custom command", "\" --startup" })
            {
                Set(runKey, unrelated, RegistryValueKind.String); service.RefreshExecutablePath();
                Check((string?)Value(runKey) == unrelated, "migration leaves unrelated command unchanged");
            }
            Set(runKey, Command(oldPath), RegistryValueKind.String);
            byte[] blocked = Approval(7); Set(approvalKey, blocked, RegistryValueKind.Binary);
            var readOnly = new AutostartService(runKey: runKey, approvalKey: approvalKey, executablePath: executable, readOnly: true);
            _ = readOnly.Read(); readOnly.RefreshExecutablePath();
            Rejected(() => readOnly.SetEnabled(true), "read-only rejects enable");
            Rejected(() => readOnly.SetEnabled(false), "read-only rejects disable");
            Check((string?)Value(runKey) == Command(oldPath) && Value(approvalKey) is byte[] intact && blocked.AsSpan().SequenceEqual(intact),
                "read-only operations preserve registration and approval bytes");
            var absentReadOnly = new AutostartService(runKey: registryRoot + @"\ReadOnlyRun", approvalKey: registryRoot + @"\ReadOnlyApproval", executablePath: executable, readOnly: true);
            _ = absentReadOnly.Read(); absentReadOnly.RefreshExecutablePath();
            Rejected(() => absentReadOnly.SetEnabled(true), "read-only missing registration rejects enable");
            Check(!Exists(registryRoot + @"\ReadOnlyRun") && !Exists(registryRoot + @"\ReadOnlyApproval"), "read-only never creates keys");
            service.SetEnabled(false);
            Check(Value(runKey) is null && Value(approvalKey) is byte[] retained && blocked.AsSpan().SequenceEqual(retained),
                "disable removes Run entry while preserving Windows approval bytes");

            string missingRun = registryRoot + @"\MissingRun";
            var missing = new AutostartService(runKey: missingRun, approvalKey: approvalKey, executablePath: Path.Combine(fixtureRoot, "missing.exe"));
            Rejected(() => missing.SetEnabled(true), "missing executable rejected");
            Check(!Exists(missingRun), "missing executable rejection creates no key");
            string longDirectory = Path.Combine(fixtureRoot, new string('a', 120), new string('b', 120));
            Directory.CreateDirectory(longDirectory);
            string longExecutable = Path.Combine(longDirectory, "Luma.Island.exe"); File.WriteAllText(longExecutable, "inert fixture");
            Check(Command(longExecutable).Length > 260, "long-path fixture exceeds Run command limit");
            string longRun = registryRoot + @"\LongRun";
            var longPath = new AutostartService(runKey: longRun, approvalKey: approvalKey, executablePath: longExecutable);
            Rejected(() => longPath.SetEnabled(true), "command beyond 260 characters rejected");
            Check(!Exists(longRun), "overlong command rejection creates no key");
            Check((string?)Value(runKey, "OtherApp") == "keep this unrelated value" && Value(approvalKey, "OtherApp") is byte[] other &&
                Approval(3).AsSpan().SequenceEqual(other), "all operations preserve unrelated Run and approval entries");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            // Only the GUID subtree created by this invocation is eligible for cleanup.
            Registry.CurrentUser.DeleteSubKeyTree(registryRoot, throwOnMissingSubKey: false);
        }
        Check(!Exists(registryRoot), "own GUID registry subtree removed");
        string result = failure is null ? $"PASS: {_checks} isolated autostart checks; registry subtree cleaned: HKCU\\{registryRoot}."
            : "FAIL after " + _checks + " checks: " + failure;
        File.WriteAllText(Path.Combine(directory, "result.txt"), result + Environment.NewLine);
        Console.WriteLine(result);
        return failure is null ? 0 : 1;
    }
}
