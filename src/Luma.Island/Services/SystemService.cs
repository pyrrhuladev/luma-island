using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Luma.Island.Services;

/// <param name="Percent">0–100, or -1 when Windows cannot report a percentage.</param>
public sealed record BatterySnapshot(bool HasBattery, int Percent, bool IsCharging, bool IsOnAcPower = false);

/// <summary>Default output volume through Core Audio. Construction and calls belong on the WPF dispatcher.</summary>
public sealed class SystemService : IDisposable
{
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly Guid _eventContext = new("78456C49-83A9-4C4B-92D7-1C62E3A1442A");
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private EndpointVolumeCallback? _volumeCallback;
    private DeviceNotificationClient? _deviceCallback;
    private int _endpointGeneration;
    private int _rebindQueued;
    private bool _disposed;

    public float Volume { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsAvailable { get; private set; }
    /// <summary>Raised on the WPF application dispatcher. Volume is in the range 0–1.</summary>
    public event Action<float, bool>? VolumeChanged;

    public SystemService()
    {
        _dispatcher.VerifyAccess();
        try
        {
            _enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            _deviceCallback = new DeviceNotificationClient(this);
            Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_deviceCallback));
            BindDefaultDevice();
        }
        catch (COMException ex)
        {
            Trace.WriteLine("Core Audio initialization: " + ex.Message);
        }
    }

    public void SetVolume(float value)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _endpoint is null || !float.IsFinite(value)) return;
        try
        {
            var context = _eventContext;
            Marshal.ThrowExceptionForHR(_endpoint.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref context));
            RefreshVolume();
        }
        catch (COMException ex) { Trace.WriteLine("Core Audio volume: " + ex.Message); }
    }

    public void ToggleMute()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _endpoint is null) return;
        try
        {
            Marshal.ThrowExceptionForHR(_endpoint.GetMute(out var muted));
            var context = _eventContext;
            Marshal.ThrowExceptionForHR(_endpoint.SetMute(!muted, ref context));
            RefreshVolume();
        }
        catch (COMException ex) { Trace.WriteLine("Core Audio mute: " + ex.Message); }
    }

    public void RefreshVolume()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        if (_endpoint is null) { UpdateVolume(0, false, false); return; }
        try
        {
            Marshal.ThrowExceptionForHR(_endpoint.GetMasterVolumeLevelScalar(out var level));
            Marshal.ThrowExceptionForHR(_endpoint.GetMute(out var muted));
            UpdateVolume(level, muted, true);
        }
        catch (COMException ex)
        {
            Trace.WriteLine("Core Audio read: " + ex.Message);
            UpdateVolume(0, false, false);
        }
    }

    private void BindDefaultDevice()
    {
        if (_disposed || _enumerator is null) return;
        ReleaseEndpoint();
        IMMDevice? device = null;
        try
        {
            // eRender / eMultimedia: the ordinary default playback device.
            Marshal.ThrowExceptionForHR(_enumerator.GetDefaultAudioEndpoint(0, 1, out device));
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out var instance));
            _endpoint = (IAudioEndpointVolume)instance;
            _volumeCallback = new EndpointVolumeCallback(this, _endpointGeneration);
            Marshal.ThrowExceptionForHR(_endpoint.RegisterControlChangeNotify(_volumeCallback));
            RefreshVolume();
        }
        catch (COMException ex)
        {
            // No playback device is a normal state on a disconnected machine.
            Trace.WriteLine("Core Audio endpoint: " + ex.Message);
            ReleaseEndpoint();
            UpdateVolume(0, false, false);
        }
        finally { ReleaseComObject(device); }
    }

    private void UpdateVolume(float level, bool muted, bool available)
    {
        if (_disposed) return;
        level = Math.Clamp(level, 0f, 1f);
        if (Math.Abs(Volume - level) < 0.0001f && muted == IsMuted && available == IsAvailable) return;
        Volume = level;
        IsMuted = muted;
        IsAvailable = available;
        VolumeChanged?.Invoke(level, muted);
    }

    private void PostVolume(float level, bool muted, int generation)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed && generation == _endpointGeneration) UpdateVolume(level, muted, true);
        }), DispatcherPriority.Background);
    }

    private void QueueRebind()
    {
        // IMMNotificationClient methods must return quickly and cannot unregister
        // callbacks from inside a notification. Rebind on the UI dispatcher.
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _rebindQueued, 1) != 0) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _rebindQueued, 0);
            if (!_disposed) BindDefaultDevice();
        }), DispatcherPriority.Background);
    }

    private void ReleaseEndpoint()
    {
        _endpointGeneration++;
        if (_endpoint is not null && _volumeCallback is not null)
        {
            try { _endpoint.UnregisterControlChangeNotify(_volumeCallback); }
            catch (COMException) { }
        }
        _volumeCallback = null;
        ReleaseComObject(_endpoint);
        _endpoint = null;
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
    }

    public static BatterySnapshot ReadBattery()
    {
        if (!GetSystemPowerStatus(out var status)) return new BatterySnapshot(false, -1, false);
        bool hasBattery = status.BatteryFlag != 255 && (status.BatteryFlag & 128) == 0;
        return new BatterySnapshot(hasBattery, status.BatteryLifePercent == 255 ? -1 : Math.Clamp((int)status.BatteryLifePercent, 0, 100),
            (status.BatteryFlag & 8) != 0, status.ACLineStatus == 1);
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        ReleaseEndpoint();
        if (_enumerator is not null && _deviceCallback is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback); }
            catch (COMException) { }
        }
        _deviceCallback = null;
        ReleaseComObject(_enumerator);
        _enumerator = null;
        VolumeChanged = null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    // GUIDs and vtable order verified against Microsoft's Windows SDK headers:
    // microsoft/win32metadata generation/WinSDK/RecompiledIdlHeaders/um/{mmdeviceapi,endpointvolume}.h.
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
    }

    [ComVisible(true), Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notification);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class EndpointVolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly SystemService _owner;
        private readonly int _generation;
        internal EndpointVolumeCallback(SystemService owner, int generation) { _owner = owner; _generation = generation; }
        public int OnNotify(IntPtr notification)
        {
            try
            {
                if (notification != IntPtr.Zero)
                {
                    var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notification);
                    _owner.PostVolume(data.MasterVolume, data.Muted != 0, _generation);
                }
            }
            catch (Exception ex) { Trace.WriteLine("Core Audio callback: " + ex.Message); }
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioVolumeNotificationData
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
        public uint Channels;
    }

    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly SystemService _owner;
        internal DeviceNotificationClient(SystemService owner) => _owner = owner;
        public int OnDeviceStateChanged(string deviceId, uint newState) { _owner.QueueRebind(); return 0; }
        public int OnDeviceAdded(string deviceId) { _owner.QueueRebind(); return 0; }
        public int OnDeviceRemoved(string deviceId) { _owner.QueueRebind(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            if (flow == 0 && role == 1) _owner.QueueRebind();
            return 0;
        }
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
