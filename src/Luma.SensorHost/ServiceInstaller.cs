using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Luma.Island.Services;
using Microsoft.Win32.SafeHandles;

namespace Luma.SensorHost;

/// <summary>Only called by explicit --install/--remove in an elevated process. Never installs a driver.</summary>
internal static class ServiceInstaller
{
    internal static string Version => typeof(ServiceInstaller).Assembly.GetName().Version!.ToString(3);
    internal static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LumaIsland", "Sensors");
    internal static string SetupLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LumaIsland", "Sensors", "setup.log");
    private const string ProtectedSddl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;BU)";

    internal static void Install(string ownerSid)
    {
        RequireAdministrator();
        ownerSid = SensorPipeServer.ValidateOwnerSid(ownerSid);
        string source = Path.GetFullPath(AppContext.BaseDirectory);
        string root = Path.GetFullPath(InstallRoot);
        if (IsWithin(source, root)) throw new InvalidOperationException("Run setup from the portable sensors folder, not the installed service directory.");
        if (!File.Exists(Path.Combine(source, "Luma.SensorHost.exe")) || !File.Exists(Path.Combine(source, "Luma.SensorHost.dll")))
            throw new InvalidOperationException("Complete published SensorHost folder is required.");
        SecureDirectory(Path.GetDirectoryName(root)!);
        SecureDirectory(root);
        using ScHandle manager = OpenSCManagerW(null, null, 3);
        CheckHandle(manager);
        using ScHandle existing = OpenServiceW(manager, SensorProtocol.ServiceName, 0xF01FF);
        string? previousCommand = null;
        bool previouslyRunning = false;
        if (!existing.IsInvalid)
        {
            previousCommand = VerifyOwnedService(existing, root);
            if (!QueryServiceStatus(existing, out ServiceStatus previousStatus)) ThrowLastError();
            previouslyRunning = previousStatus.State != 1;
        }
        else if (Marshal.GetLastWin32Error() != 1060) ThrowLastError();

        string staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(root, Version);
        string? backup = null;
        ScHandle? created = null;
        ScHandle? active = existing.IsInvalid ? null : existing;
        bool stopRequested = false, payloadSwitched = false, configChanged = false;
        SecureDirectory(staging);
        try
        {
            CopyPayload(source, staging);
            // Staging/copy failures leave the previous running service completely untouched.
            if (!existing.IsInvalid) { stopRequested = true; Stop(existing); }
            if (Directory.Exists(destination))
            {
                RejectReparse(destination);
                backup = Path.Combine(root, ".previous-" + Guid.NewGuid().ToString("N"));
                Directory.Move(destination, backup);
            }
            Directory.Move(staging, destination);
            payloadSwitched = true;
            string command = BuildCommand(destination, ownerSid);
            if (existing.IsInvalid)
                created = CreateServiceW(manager, SensorProtocol.ServiceName, "Luma Island Sensors", 0xF01FF, 0x10, 2, 1, command, null, IntPtr.Zero, null, "LocalSystem", null);
            ScHandle service = active = existing.IsInvalid ? created! : existing;
            CheckHandle(service);
            if (!existing.IsInvalid && !ChangeServiceConfigW(service, uint.MaxValue, 2, uint.MaxValue, command, null, IntPtr.Zero, null, "LocalSystem", null, "Luma Island Sensors")) ThrowLastError();
            configChanged = true;
            ProtectService(service);
            if (!StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056) ThrowLastError();
            WaitFor(service, 4);
            Console.WriteLine("Sensor service installed. Hardware access requires the independently installed official PawnIO driver.");
            if (backup is not null) { try { DeleteOwnedTree(backup, root); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Protected old payload may be cleaned up by --remove. */ } }
        }
        catch (Exception original)
        {
            try
            {
                if (configChanged && active is not null) Stop(active);
                if (created is not null && !created.IsInvalid && !DeleteService(created)) ThrowLastError();
                if (payloadSwitched && Directory.Exists(destination)) DeleteOwnedTree(destination, root);
                if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, destination);
                if (!existing.IsInvalid && configChanged && !ChangeServiceConfigW(existing, uint.MaxValue, 2, uint.MaxValue, previousCommand!, null, IntPtr.Zero, null, "LocalSystem", null, "Luma Island Sensors")) ThrowLastError();
                if (!existing.IsInvalid && stopRequested && previouslyRunning)
                {
                    if (!StartServiceW(existing, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056) ThrowLastError();
                    WaitFor(existing, 4);
                }
            }
            catch (Exception rollback) { throw new InvalidOperationException(original.Message + " Rollback also failed: " + rollback.Message, original); }
            throw;
        }
        finally
        {
            created?.Dispose();
            try { if (Directory.Exists(staging)) DeleteOwnedTree(staging, root); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void Remove()
    {
        RequireAdministrator();
        string root = Path.GetFullPath(InstallRoot);
        using ScHandle manager = OpenSCManagerW(null, null, 1);
        CheckHandle(manager);
        using ScHandle service = OpenServiceW(manager, SensorProtocol.ServiceName, 0xF01FF);
        if (!service.IsInvalid)
        {
            VerifyOwnedService(service, root);
            Stop(service);
            if (!DeleteService(service)) ThrowLastError();
        }
        else if (Marshal.GetLastWin32Error() != 1060) ThrowLastError();
        if (Directory.Exists(root)) DeleteOwnedTree(root, root);
        Console.WriteLine("Sensor service removed. Independently installed drivers were left unchanged.");
    }

    internal static string BuildCommand(string destination, string ownerSid) => "\"" + Path.Combine(destination, "Luma.SensorHost.exe") + "\" --service --owner-sid " + SensorPipeServer.ValidateOwnerSid(ownerSid);
    internal static bool IsWithin(string path, string root) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void RejectReparse(string path)
    {
        for (string? cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if ((Directory.Exists(cursor) || File.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not allowed in sensor installation paths.");
    }
    internal static void TryWriteSetupLog(string message)
    {
        try
        {
            RequireAdministrator();
            string path = SetupLogPath;
            string directory = Path.GetDirectoryName(path)!;
            SecureDirectory(Path.GetDirectoryName(directory)!);
            SecureDirectory(directory);
            string temporary = Path.Combine(directory, "setup-" + Guid.NewGuid().ToString("N") + ".tmp");
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(ProtectedSddl.Replace("OICI", ""));
            using (FileStream stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.Read, 4096, FileOptions.WriteThrough, security))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(DateTimeOffset.UtcNow.ToString("O") + " " + new string(message.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').Take(3500).ToArray()));
            File.Move(temporary, path, true);
        }
        catch (Exception) { /* Setup errors must preserve their original exit status if logging itself fails. */ }
    }
    private static void SecureDirectory(string path)
    {
        RejectReparse(path);
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(ProtectedSddl);
        new DirectoryInfo(path).Create(security);
        new DirectoryInfo(path).SetAccessControl(security);
    }
    private static void CopyPayload(string source, string destination)
    {
        RejectReparse(source);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            RejectReparse(file);
            string target = Path.Combine(destination, Path.GetFileName(file));
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(ProtectedSddl.Replace("OICI", ""));
            // Create with the protected owner/DACL atomically, not File.Copy followed by an ACL fix.
            using FileStream output = new FileInfo(target).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 65536, FileOptions.WriteThrough, security);
            using FileStream input = File.OpenRead(file);
            input.CopyTo(output);
        }
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            RejectReparse(directory);
            string target = Path.Combine(destination, Path.GetFileName(directory));
            SecureDirectory(target);
            CopyPayload(directory, target);
        }
    }
    private static void DeleteOwnedTree(string target, string root)
    {
        if (!IsWithin(target, root)) throw new IOException("Unsafe sensor cleanup path.");
        RejectReparse(target);
        foreach (string directory in Directory.EnumerateDirectories(target)) DeleteOwnedTree(directory, root);
        foreach (string file in Directory.EnumerateFiles(target)) { RejectReparse(file); File.Delete(file); }
        Directory.Delete(target);
    }
    private static string VerifyOwnedService(ScHandle service, string root)
    {
        QueryServiceConfigW(service, IntPtr.Zero, 0, out uint required);
        if (required == 0 || required > 65536) throw new IOException("Invalid existing service configuration.");
        IntPtr buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!QueryServiceConfigW(service, buffer, required, out _)) ThrowLastError();
            string command = Marshal.PtrToStringUni(Marshal.PtrToStructure<ServiceConfig>(buffer).BinaryPath) ?? "";
            int end = command.IndexOf('"', 1);
            if (!command.StartsWith('"') || end < 2 || !IsWithin(command.Substring(1, end - 1), root))
                throw new IOException("Existing service does not belong to this sensor installation.");
            return command;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void ProtectService(ScHandle service)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;LC;;;AU)", 1, out IntPtr descriptor, out _)) ThrowLastError();
        try { if (!SetServiceObjectSecurity(service, 4, descriptor)) ThrowLastError(); }
        finally { LocalFree(descriptor); }
    }
    private static void Stop(ScHandle service)
    {
        if (!QueryServiceStatus(service, out ServiceStatus status)) ThrowLastError();
        if (status.State == 1) return;
        if (status.State != 3 && !ControlService(service, 1, out _) && Marshal.GetLastWin32Error() != 1062) ThrowLastError();
        WaitFor(service, 1);
    }
    private static void WaitFor(ScHandle service, uint state)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (!QueryServiceStatus(service, out ServiceStatus status)) ThrowLastError();
            if (status.State == state) return;
            if (state == 4 && status.State == 1) throw new IOException("Sensor service stopped during startup.");
            Task.Delay(100).GetAwaiter().GetResult();
        }
        throw new TimeoutException("Sensor service did not reach the requested state.");
    }
    private static void RequireAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Explicit administrator setup is required.");
    }
    private static void CheckHandle(ScHandle handle) { if (handle.IsInvalid) ThrowLastError(); }
    private static void ThrowLastError() => throw new Win32Exception(Marshal.GetLastWin32Error());
    internal sealed class ScHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public ScHandle() : base(true) { } protected override bool ReleaseHandle() => CloseServiceHandle(handle); }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { internal uint Type, State, Accepted, ExitCode, SpecificExitCode, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig
    { internal uint Type, Start, Error; internal IntPtr BinaryPath, LoadOrderGroup; internal uint Tag; internal IntPtr Dependencies, Account, DisplayName; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ScHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ScHandle OpenServiceW(ScHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ScHandle CreateServiceW(ScHandle manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfigW(ScHandle service, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string account, string? password, string display);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfigW(ScHandle service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(ScHandle service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(ScHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceW(ScHandle service, uint argc, IntPtr argv);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(ScHandle service);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceObjectSecurity(ScHandle service, uint information, IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
