using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Luma.Island.Services;

/// <summary>Ordinary-user client for the separately installed optional sensor service. Call on a worker.</summary>
public sealed class SensorClient : IDisposable
{
    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private int _disposed;

    public SensorResponse Read(bool cpu, bool memory)
    {
        lock (_gate)
        {
            if (!cpu && !memory || Volatile.Read(ref _disposed) != 0)
            { Close(); return SensorProtocol.Empty(false, false, "disabled"); }
            bool cold = _pipe is null;
            using var timeout = new CancellationTokenSource(cold ? 3000 : 750);
            try
            {
                if (_pipe is null)
                {
                    NamedPipeClientStream pipe = OpenVerifiedPipe();
                    Interlocked.Exchange(ref _pipe, pipe)?.Dispose();
                    if (Volatile.Read(ref _disposed) != 0) { Close(); return SensorProtocol.Empty(false, false, "disabled"); }
                }
                var pipeStream = _pipe!;
                SensorProtocol.WriteAsync(pipeStream, new SensorRequest(SensorProtocol.Version, cpu, memory), timeout.Token).GetAwaiter().GetResult();
                SensorResponse response = SensorProtocol.ReadAsync<SensorResponse>(pipeStream, timeout.Token).GetAwaiter().GetResult();
                if (!IsValidResponse(response, cpu, memory, DateTimeOffset.UtcNow)) throw new InvalidDataException("Invalid sensor response.");
                return Volatile.Read(ref _disposed) == 0 ? response : SensorProtocol.Empty(false, false, "disabled");
            }
            catch (OperationCanceledException) { Close(); return SensorProtocol.Empty(cpu, memory, "timeout"); }
            catch (SensorConnectionException exception) { Close(); return SensorProtocol.Empty(cpu, memory, exception.Status); }
            catch (UnauthorizedAccessException) { Close(); return SensorProtocol.Empty(cpu, memory, "access_denied"); }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
            { Close(); return SensorProtocol.Empty(cpu, memory, "protocol_error"); }
            catch (Exception exception) when (exception is IOException or Win32Exception or ObjectDisposedException)
            { Close(); return SensorProtocol.Empty(cpu, memory, "unavailable"); }
        }
    }

    internal static bool IsValidResponse(SensorResponse response, bool cpu, bool memory, DateTimeOffset now)
    {
        if (response.Version != SensorProtocol.Version || response.SampledAtUtc > now.AddSeconds(2) || response.SampledAtUtc < now.AddSeconds(-8)) return false;
        return ValidMetric(cpu, response.CpuCelsius, response.CpuStatus, false) && ValidMetric(memory, response.MemoryCelsius, response.MemoryStatus, true);
    }
    private static bool ValidMetric(bool enabled, double? value, string status, bool memory)
    {
        if (!enabled) return value is null && status == "disabled";
        if (status == "ok") return SensorProtocol.IsValidCelsius(value, memory);
        return value is null && status is "unavailable" or "driver_missing" or "provider_error";
    }
    private static NamedPipeClientStream OpenVerifiedPipe()
    {
        uint servicePid = GetServicePid();
        if (servicePid == 0) throw new SensorConnectionException("unavailable");
        // Specific read/write rights omit FILE_CREATE_PIPE_INSTANCE, unlike GENERIC_WRITE.
        SafePipeHandle handle = CreateFileW(@"\\.\pipe\" + SensorProtocol.PipeName, 0x12019B, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new SensorConnectionException(error == 5 ? "access_denied" : "unavailable"); }
        try
        {
            if (!GetNamedPipeServerProcessId(handle, out uint pipePid) || pipePid != servicePid)
                throw new SensorConnectionException("access_denied");
            return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
        }
        catch { handle.Dispose(); throw; }
    }
    private static uint GetServicePid()
    {
        IntPtr manager = OpenSCManagerW(null, null, 1);
        if (manager == IntPtr.Zero) throw new SensorConnectionException("access_denied");
        try
        {
            IntPtr service = OpenServiceW(manager, SensorProtocol.ServiceName, 4);
            if (service == IntPtr.Zero) throw new SensorConnectionException(Marshal.GetLastWin32Error() == 1060 ? "not_installed" : "access_denied");
            try
            {
                if (!QueryServiceStatusEx(service, 0, out ServiceStatusProcess status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return status.State == 4 ? status.ProcessId : 0;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private void Close() => Interlocked.Exchange(ref _pipe, null)?.Dispose();
    public void Dispose() { Interlocked.Exchange(ref _disposed, 1); Close(); }
    private sealed class SensorConnectionException(string status) : IOException { internal string Status { get; } = status; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatusProcess
    { internal uint Type, State, Accepted, ExitCode, SpecificExitCode, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateFileW(string path, uint access, uint share, IntPtr attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
