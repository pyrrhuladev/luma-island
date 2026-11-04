using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Luma.Island.Services;

namespace Luma.SensorHost;

internal sealed class NativeService
{
    private readonly string _ownerSid;
    private readonly ServiceMainDelegate _main;
    private readonly HandlerDelegate _handler;
    private readonly CancellationTokenSource _stop = new();
    private IntPtr _statusHandle;
    private ServiceStatus _status;
    private int _exitCode;
    private readonly object _statusGate = new();
    private NativeService(string ownerSid) { _ownerSid = SensorPipeServer.ValidateOwnerSid(ownerSid); _main = Main; _handler = Handler; }
    internal static int Run(string ownerSid)
    {
        var service = new NativeService(ownerSid);
        var table = new[] { new ServiceTableEntry { Name = SensorProtocol.ServiceName, Main = service._main }, new ServiceTableEntry() };
        try
        {
            if (!StartServiceCtrlDispatcherW(table)) throw new Win32Exception(Marshal.GetLastWin32Error());
            GC.KeepAlive(service);
            return service._exitCode;
        }
        finally { service._stop.Dispose(); }
    }
    private void Main(uint count, IntPtr arguments)
    {
        _statusHandle = RegisterServiceCtrlHandlerExW(SensorProtocol.ServiceName, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) { _exitCode = Marshal.GetLastWin32Error(); return; }
        try
        {
            Report(2);
            SensorPipeServer.RunAsync(_ownerSid, _stop.Token, () => Report(4)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { _exitCode = 1; }
        finally { Report(1, (uint)_exitCode); }
    }
    private uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        try
        {
            if (control is 1 or 5)
            {
                Report(3);
                _stop.Cancel();
            }
            else if (control == 4) { lock (_statusGate) SetServiceStatus(_statusHandle, ref _status); }
        }
        catch (Exception) { return 1; } // No managed exception may cross the native SCM callback boundary.
        return 0;
    }
    private void Report(uint state, uint error = 0)
    {
        lock (_statusGate)
        {
            _status = new ServiceStatus { Type = 0x10, State = state, Accepted = state == 4 ? 5u : 0u,
                ExitCode = error, WaitHint = state is 2 or 3 ? 15000u : 0u };
            SetServiceStatus(_statusHandle, ref _status);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMainDelegate(uint argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint HandlerDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ServiceTableEntry
    { [MarshalAs(UnmanagedType.LPWStr)] internal string? Name; internal ServiceMainDelegate? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { internal uint Type, State, Accepted, ExitCode, SpecificExitCode, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW([In] ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string name, HandlerDelegate handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);
}
