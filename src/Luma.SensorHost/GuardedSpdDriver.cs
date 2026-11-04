using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;
using RAMSPDToolkit.I2CSMBus.Interop.PawnIO;
using RAMSPDToolkit.Windows.Driver.Interfaces;

namespace Luma.SensorHost;

/// <summary>
/// Makes the pinned library's driver-module lifetime safe for RAMSPDToolkit's deferred SPD finalizers.
/// No driver is installed, no modules are loaded until LHM explicitly requests them.
/// </summary>
internal sealed class GuardedSpdDriver : IPawnIODriver
{
    private readonly object _gate = new();
    private readonly IPawnIODriver _inner;
    private readonly List<GuardedModule> _modules = new();
    private bool _closed;
    internal GuardedSpdDriver(IPawnIODriver inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    internal static GuardedSpdDriver CreateLhm()
    {
        // The pinned LHM driver constructor only creates an empty managed module list.
        Type type = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.RAMSPDToolkitDriver", true)!;
        return new GuardedSpdDriver((IPawnIODriver)Activator.CreateInstance(type, nonPublic: true)!);
    }
    public bool IsOpen { get { lock (_gate) return !_closed && _inner.IsOpen; } }
    public bool Load() { lock (_gate) return !_closed && _inner.Load(); }
    public IPawnIOModule LoadModule(PawnIOSMBusIdentifier identifier)
    {
        lock (_gate)
        {
            if (_closed) return null!;
            IPawnIOModule? inner = _inner.LoadModule(identifier);
            if (inner is null) return null!;
            var module = new GuardedModule(inner);
            _modules.Add(module);
            return module;
        }
    }
    public void Unload()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            // Detach all public proxies and await in-flight Execute calls BEFORE LHM closes handles.
            // A late SPDAccessor.Finalize -> SetPage request can then only receive a negative status.
            foreach (GuardedModule module in _modules) module.Detach();
            _modules.Clear();
            _inner.Unload();
        }
    }

    internal sealed class GuardedModule : IPawnIOModule
    {
        internal const int DeviceUnavailable = -19;
        private readonly object _gate = new();
        private IPawnIOModule? _inner;
        internal GuardedModule(IPawnIOModule inner) => _inner = inner;
        public int Execute(string name, long[] input, uint inputSize, long[] output, uint outputSize, out uint returned)
        {
            lock (_gate)
            {
                if (_inner is not null) return _inner.Execute(name, input, inputSize, output, outputSize, out returned);
                Array.Clear(output);
                returned = 0;
                return DeviceUnavailable;
            }
        }
        internal void Detach() { lock (_gate) _inner = null; }
    }
}
