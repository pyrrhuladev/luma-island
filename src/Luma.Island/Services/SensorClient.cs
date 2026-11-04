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
    private readonly Func<CancellationToken, Task<NamedPipeClientStream>> _connect;
    private NamedPipeClientStream? _pipe;
    private int _disposed;
    public SensorClient() : this(OpenVerifiedPipeAsync) { }
    internal SensorClient(Func<CancellationToken, Task<NamedPipeClientStream>> connect) => _connect = connect ?? throw new ArgumentNullException(nameof(connect));

    public SensorResponse Read(bool cpu, bool memory)
    {
        lock (_gate)
        {
            if (!cpu && !memory || Volatile.Read(ref _disposed) != 0)
            { Close(); return SensorProtocol.Empty(false, false, "disabled"); }
            bool cold = _pipe is null;
            // One budget includes busy-instance cleanup, connection and the first native sample.
            using var timeout = new CancellationTokenSource(cold ? 5000 : 750);
            try
            {
                if (_pipe is null)
                {
                    NamedPipeClientStream pipe = _connect(timeout.Token).GetAwaiter().GetResult();
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
    private static Task<NamedPipeClientStream> OpenVerifiedPipeAsync(CancellationToken token)
    {
        uint servicePid = GetServicePid();
        if (servicePid == 0) throw new SensorConnectionException("unavailable");
        return OpenPipeAsync(SensorProtocol.PipeName, servicePid, token);
    }
    internal static async Task<NamedPipeClientStream> OpenPipeAsync(string pipeName, uint expectedPid, CancellationToken token)
    {
        // Specific read/write rights omit FILE_CREATE_PIPE_INSTANCE, unlike GENERIC_WRITE.
        while (true)
        {
            token.ThrowIfCancellationRequested();
            SafePipeHandle handle = CreateFileW(@"\\.\pipe\" + pipeName, 0x12019B, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error(); handle.Dispose();
                if (error != 231) throw new SensorConnectionException(error == 5 ? "access_denied" : "unavailable");
                // The single server instance can still be disposing the preceding LHM session.
                // Cancellable asynchronous waits keep retries within the SAME operation deadline.
                await Task.Delay(25, token).ConfigureAwait(false);
                continue;
            }
            try
            {
                if (!GetNamedPipeServerProcessId(handle, out uint pipePid) || pipePid != expectedPid)
                    throw new SensorConnectionException("access_denied");
                token.ThrowIfCancellationRequested();
                return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
            }
            catch { handle.Dispose(); throw; }
        }
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
                return status.State switch
                {
                    4 => status.ProcessId,
                    2 or 3 or 5 or 6 => throw new SensorConnectionException("service_starting"),
                    _ => throw new SensorConnectionException("service_stopped")
                };
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
