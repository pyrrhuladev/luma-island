using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LibreHardwareMonitor.Hardware;
using Luma.Island.Services;
using Microsoft.Win32;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.Windows.Driver;

namespace Luma.SensorHost;

internal sealed record TemperatureReading(string HardwareId, bool IsMemory, string SensorId, string Name, double? Celsius);

internal static class TemperatureMapping
{
    internal static int CpuRank(string name) => name switch
    {
        "CPU Package" or "Core (Tctl/Tdie)" => 0,
        "Core (Tdie)" => 1,
        "Core (Tctl)" => 2,
        _ => -1
    };
    internal static bool IsDimmTemperature(string id)
    {
        string[] parts = id.Split('/');
        return parts.Length == 6 && parts[0] == "" && parts[1] == "memory" && parts[2] == "dimm" &&
            int.TryParse(parts[3], out int index) && index >= 0 && parts[4] == "temperature" && parts[5] == "0";
    }
    internal static (double? Cpu, double? Memory) Select(IEnumerable<TemperatureReading> readings)
    {
        var valid = readings.Where(r => SensorProtocol.IsValidCelsius(r.Celsius, r.IsMemory)).ToArray();
        double? cpu = valid.Where(r => !r.IsMemory && CpuRank(r.Name) >= 0)
            .GroupBy(r => r.HardwareId, StringComparer.Ordinal)
            .Select(g => g.OrderBy(r => CpuRank(r.Name)).First().Celsius).DefaultIfEmpty(null).Max();
        double? memory = valid.Where(r => r.IsMemory && IsDimmTemperature(r.SensorId))
            .Select(r => r.Celsius).DefaultIfEmpty(null).Max();
        return (cpu, memory);
    }
}

internal interface ITemperatureSource : IDisposable
{
    IReadOnlyList<TemperatureReading> Read();
}

/// <summary>One pipe connection owns one session. No timer or reads exist without explicit requests.</summary>
internal sealed class SensorSession : IDisposable
{
    private readonly Func<bool, bool, ITemperatureSource> _factory;
    private ITemperatureSource? _source;
    private SensorResponse? _cached;
    private bool _cpu, _memory, _disposed;
    internal SensorSession(Func<bool, bool, ITemperatureSource>? factory = null) => _factory = factory ?? ((cpu, memory) => new LhmTemperatureSource(cpu, memory));

    internal SensorResponse Read(SensorRequest request, SensorReadGate? cadence = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SensorSession));
        if (request.Version != SensorProtocol.Version) throw new System.IO.InvalidDataException("Unsupported sensor protocol.");
        if (!request.Cpu && !request.Memory) { Close(); _cached = null; return SensorProtocol.Empty(false, false, "disabled"); }
        if (cadence is not null && !cadence.TryAcquire())
        {
            if (_cached is null) return SensorProtocol.Empty(request.Cpu, request.Memory, "unavailable");
            // An immediate Configure can precede the next 2 s hardware slot. Reply without waiting
            // beyond the client's deadline, exposing only requested metrics from this connection.
            return _cached with
            {
                CpuCelsius = request.Cpu ? _cached.CpuCelsius : null,
                MemoryCelsius = request.Memory ? _cached.MemoryCelsius : null,
                CpuStatus = request.Cpu ? _cached.CpuStatus == "disabled" ? "unavailable" : _cached.CpuStatus : "disabled",
                MemoryStatus = request.Memory ? _cached.MemoryStatus == "disabled" ? "unavailable" : _cached.MemoryStatus : "disabled"
            };
        }
        try
        {
            if (_source is null || _cpu != request.Cpu || _memory != request.Memory)
            {
                Close(); _cpu = request.Cpu; _memory = request.Memory;
                _source = _factory(_cpu, _memory);
            }
            var selected = TemperatureMapping.Select(_source.Read());
            return _cached = new(SensorProtocol.Version, DateTimeOffset.UtcNow, request.Cpu ? selected.Cpu : null,
                request.Memory ? selected.Memory : null, request.Cpu ? selected.Cpu.HasValue ? "ok" : "unavailable" : "disabled",
                request.Memory ? selected.Memory.HasValue ? "ok" : "unavailable" : "disabled");
        }
        catch (DriverMissingException) { Close(); return _cached = SensorProtocol.Empty(request.Cpu, request.Memory, "driver_missing"); }
        catch (Exception) { Close(); return _cached = SensorProtocol.Empty(request.Cpu, request.Memory, "provider_error"); }
    }
    private void Close() { var source = _source; _source = null; source?.Dispose(); }
    public void Dispose() { if (!_disposed) { _disposed = true; _cached = null; Close(); } }
}

internal sealed class DriverMissingException : Exception;

internal sealed class LhmTemperatureSource : ITemperatureSource
{
    private readonly Computer _computer;
    private readonly bool _cpu, _memory;
    private readonly GuardedSpdDriver? _memoryDriver;
    private bool _disposed;
    internal LhmTemperatureSource(bool cpu, bool memory)
    {
        // LHM PawnIo.IsInstalled is a process-lifetime cached registry result. A fresh check lets
        // an already running idle service notice a driver installed explicitly by the user later.
        using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (RegistryKey? installed = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
            if (!System.Version.TryParse(installed?.GetValue("DisplayVersion") as string, out _)) throw new DriverMissingException();
        _cpu = cpu; _memory = memory;
        if (memory)
        {
            _memoryDriver = GuardedSpdDriver.CreateLhm();
            DriverManager.Driver = _memoryDriver;
            SMBusManager.UseWMI = false;
        }
        // Every unlisted Computer group remains disabled. No GPU, disk, EC, fan, network or motherboard discovery.
        _computer = new Computer { IsCpuEnabled = cpu, IsMemoryEnabled = memory };
        try { _computer.Open(); }
        catch
        {
            try { _computer.Close(); } finally { ReleaseMemoryDriver(); }
            throw;
        }
    }
    public IReadOnlyList<TemperatureReading> Read()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LhmTemperatureSource));
        var readings = new List<TemperatureReading>();
        foreach (IHardware hardware in _computer.Hardware) ReadHardware(hardware, readings);
        return readings;
    }
    private void ReadHardware(IHardware hardware, List<TemperatureReading> readings)
    {
        bool memory = hardware.HardwareType == HardwareType.Memory;
        if (!(memory && _memory || hardware.HardwareType == HardwareType.Cpu && _cpu)) return;
        if (memory && !hardware.Identifier.ToString().StartsWith("/memory/dimm/", StringComparison.Ordinal)) return;
        var cleared = new HashSet<string>(StringComparer.Ordinal);
        foreach (ISensor sensor in hardware.Sensors)
        {
            sensor.ValuesTimeWindow = TimeSpan.Zero;
            if (IsSelected(sensor, memory) && ResetValue(sensor)) cleared.Add(sensor.Identifier.ToString());
        }
        hardware.Update();
        foreach (ISensor sensor in hardware.Sensors)
        {
            sensor.ValuesTimeWindow = TimeSpan.Zero;
            string id = sensor.Identifier.ToString();
            if (IsSelected(sensor, memory) && (cleared.Contains(id) || sensor.Value is null))
                readings.Add(new(hardware.Identifier.ToString(), memory, id, sensor.Name, sensor.Value));
        }
        foreach (IHardware sub in hardware.SubHardware) ReadHardware(sub, readings);
    }
    private static bool IsSelected(ISensor sensor, bool memory) => sensor.SensorType == SensorType.Temperature &&
        (memory ? TemperatureMapping.IsDimmTemperature(sensor.Identifier.ToString()) : TemperatureMapping.CpuRank(sensor.Name) >= 0);
    // LHM 0.9.6's internal Sensor has a public writable Value, while ISensor exposes only its getter.
    // Its DIMM Update ignores failed reads and otherwise retains old Value. Clear the cached property,
    // never a hardware register, before each update; an unresettable sensor is excluded conservatively.
    internal static bool ResetValue(object sensor)
    {
        PropertyInfo? property = sensor.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType != typeof(float?) || property.SetMethod is null) return false;
        property.SetValue(sensor, null);
        return true;
    }
    private void ReleaseMemoryDriver()
    {
        if (!_memory) return;
        // Close proxy gates before the underlying modules. RAMSPDToolkit SPD accessors have a
        // finalizer (no IDisposable) which may run much later and try to reset the page through
        // the old module. Calling the raw LHM Unload here makes that finalizer crash the process.
        try { _memoryDriver?.Unload(); }
        finally { if (ReferenceEquals(DriverManager.Driver, _memoryDriver)) DriverManager.Driver = null; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _computer.Close(); } finally { ReleaseMemoryDriver(); }
    }
}
