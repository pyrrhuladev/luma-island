using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Luma.Island.Services;

public sealed record AutostartState(bool Registered, bool DisabledByWindows, string? Error = null)
{
    public bool Enabled => Registered && !DisabledByWindows && Error is null;
}

public sealed class AutostartService
{
    public const string ValueName = "LumaIsland";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private readonly string _executablePath;
    private readonly string _runKey;
    private readonly string _approvalKey;
    private readonly bool _readOnly;

    public AutostartService(string? executablePath = null, string? runKey = null, string? approvalKey = null, bool readOnly = false)
    {
        _executablePath = Path.GetFullPath(executablePath ?? Path.Combine(AppContext.BaseDirectory, "Luma.Island.exe"));
        _runKey = runKey ?? RunKey;
        _approvalKey = approvalKey ?? ApprovalKey;
        _readOnly = readOnly;
    }

    public AutostartState Read()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(_runKey);
            bool registered = run?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command
                && !string.IsNullOrWhiteSpace(command);
            if (!registered) return new AutostartState(false, false);
            using var approval = Registry.CurrentUser.OpenSubKey(_approvalKey);
            var approvalValue = approval?.GetValue(ValueName);
            if (approvalValue is null) return new AutostartState(true, false);
            // Explorer owns this record. Observe its disabled states without changing Windows' decision.
            if (approvalValue is byte[] { Length: >= 12 } bytes)
            {
                uint state = BitConverter.ToUInt32(bytes, 0);
                if (state is 2 or 6) return new AutostartState(true, false);
                if (state is 3 or 7) return new AutostartState(true, true);
            }
            return new AutostartState(true, false, "Не удалось определить состояние автозапуска в Windows.");
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            return new AutostartState(false, false, "Не удалось прочитать настройку автозапуска.");
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (_readOnly) throw new InvalidOperationException("Автозапуск недоступен в режиме проверки.");
        if (enabled)
        {
            string command = BuildCommand();
            using var run = Registry.CurrentUser.CreateSubKey(_runKey, writable: true);
            run.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var run = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public void RefreshExecutablePath()
    {
        if (_readOnly) return;
        // A portable update can move the executable. Keep an existing registration current,
        // without opting in or overriding an entry disabled by Windows.
        using var run = Registry.CurrentUser.OpenSubKey(_runKey);
        if (run?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command
            || !IsOwnedCommand(command)) return;
        string current = BuildCommand();
        if (!string.Equals(command, current, StringComparison.OrdinalIgnoreCase)) SetEnabled(true);
    }

    private string BuildCommand()
    {
        string command = "\"" + _executablePath + "\" --startup";
        if (command.Length > 260)
            throw new InvalidOperationException("Для автозапуска переместите папку программы по более короткому пути.");
        if (_executablePath.Contains('"') || !File.Exists(_executablePath))
            throw new InvalidOperationException("Не найден файл программы для автозапуска.");
        return command;
    }

    private static bool IsOwnedCommand(string command)
    {
        if (command.Length <= 12 || !command.StartsWith('"') || !command.EndsWith("\" --startup", StringComparison.Ordinal)) return false;
        string path = command.Substring(1, command.Length - 12);
        return !path.Contains('"') && Path.IsPathFullyQualified(path)
            && string.Equals(Path.GetFileName(path), "Luma.Island.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegistryError(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException;
}
