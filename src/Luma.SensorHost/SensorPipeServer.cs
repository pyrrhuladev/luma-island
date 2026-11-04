using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Luma.Island.Services;
using Microsoft.Win32.SafeHandles;

namespace Luma.SensorHost;

internal static class SensorPipeServer
{
    internal static string ValidateOwnerSid(string value)
    {
        var sid = new SecurityIdentifier(value);
        // Only account SIDs, never Everyone, Anonymous, BUILTIN groups, service or logon SIDs.
        if (sid.Value != value || !(value.StartsWith("S-1-5-21-", StringComparison.Ordinal) || value.StartsWith("S-1-12-1-", StringComparison.Ordinal)))
            throw new ArgumentException("A Windows user account SID is required.");
        return sid.Value;
    }
    internal static string PipeSddl(string ownerSid) => "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x12019b;;;" + ValidateOwnerSid(ownerSid) + ")";

    internal static NamedPipeServerStream CreatePipe(string ownerSid, string? testName = null)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(PipeSddl(ownerSid), 1, out IntPtr descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // Duplex + overlapped + FIRST_PIPE_INSTANCE; byte mode + REJECT_REMOTE_CLIENTS; only one owner connection.
            SafePipeHandle handle = CreateNamedPipeW(@"\\.\pipe\" + (testName ?? SensorProtocol.PipeName),
                3 | 0x40000000 | 0x00080000, 8, 1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }
    internal static async Task RunAsync(string ownerSid, CancellationToken stop, Action? ready = null,
        string? testName = null, Func<SensorSession>? sessionFactory = null, TimeSpan? idleTimeout = null, SensorReadGate? readGate = null)
    {
        // Keep the same first-instance handle for the service lifetime, including between clients.
        using NamedPipeServerStream pipe = CreatePipe(ownerSid, testName);
        ready?.Invoke();
        var cadence = readGate ?? new SensorReadGate();
        while (!stop.IsCancellationRequested)
        {
            // Idle waits in the kernel. No LHM object or polling task is created before a valid request.
            await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
            using var session = sessionFactory?.Invoke() ?? new SensorSession();
            try
            {
                while (pipe.IsConnected && !stop.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    deadline.CancelAfter(idleTimeout ?? TimeSpan.FromSeconds(6));
                    SensorRequest request = await SensorProtocol.ReadAsync<SensorRequest>(pipe, deadline.Token).ConfigureAwait(false);
                    if (request.Version != SensorProtocol.Version) throw new InvalidDataException("Unsupported sensor protocol.");
                    // Rate-limit hardware, not the IPC reply: Configure may trigger an immediate
                    // request while the normal worker and this service both have a 2 s cadence.
                    SensorResponse response = session.Read(request, cadence);
                    await SensorProtocol.WriteAsync(pipe, response, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or OperationCanceledException)
            { /* Disconnect/timeout/malformed frame drops this connection and its sensor session. */ }
            // EOF can mark the managed stream Broken/IsConnected=false while the native instance
            // still needs DisconnectNamedPipe before another WaitForConnection.
            finally { pipe.Disconnect(); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint instances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes securityAttributes);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
