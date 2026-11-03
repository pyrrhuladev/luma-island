using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using Luma.Island.Services;

internal static class Program
{
    private static int _checks;
    private static int Main(string[] args)
    {
        try
        {
            CheckLifecycle();
            CheckReadings();
            CheckFailures();
            if (args.Length == 2 && args[0] == "--probe") Probe(args[1]);
            Console.WriteLine($"PASS: {_checks} GPU temperature checks.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void CheckLifecycle()
    {
        int created = 0;
        var api = new FakeApi((0, 0, 43));
        var reader = new GpuTemperatureReader(() => { created++; return api; });
        Check(created == 0 && api.Initializations == 0, "Construction never loads or initializes NVML");
        Equal(43, reader.ReadCelsius(), "Successful sensor reading is returned");
        Equal(43, reader.ReadCelsius(), "Repeated reads reuse the initialized provider");
        Check(created == 1 && api.Initializations == 1, "NVML initializes once per reader");
        reader.Dispose(); reader.Dispose();
        Check(api.Disposals == 1, "Dispose releases the provider once");
        Check(reader.ReadCelsius() is null && created == 1, "Reading a disposed reader cannot restart NVML");
        int disabledCreated = 0;
        using (var disabled = new GpuTemperatureReader(() => { disabledCreated++; return new FakeApi(); })) { }
        Check(disabledCreated == 0, "Disposing an unused feature never loads NVML");
    }

    private static void CheckReadings()
    {
        using (var reader = new GpuTemperatureReader(() => new FakeApi((0, 0, 35), (0, 0, 62), (0, 0, 51))))
            Equal(62, reader.ReadCelsius(), "The hottest available GPU is selected");
        using (var reader = new GpuTemperatureReader(() => new FakeApi((4, 0, 90), (0, 3, 0), (0, 0, 48))))
            Equal(48, reader.ReadCelsius(), "Unavailable GPUs do not mask a working sensor");
        using (var reader = new GpuTemperatureReader(() => new FakeApi((0, 0, uint.MaxValue), (0, 0, 44))))
            Equal(44, reader.ReadCelsius(), "An outlier cannot become the hottest GPU");
        using (var reader = new GpuTemperatureReader(() => new FakeApi((0, 0, 151))))
            Check(reader.ReadCelsius() is null, "A successful but impossible reading remains unavailable");
        using (var reader = new GpuTemperatureReader(() => new FakeApi((0, 0, 0))))
            Equal(0, reader.ReadCelsius(), "A genuine successful zero is distinct from an unavailable reading");
        using (var reader = new GpuTemperatureReader(() => new FakeApi()))
            Check(reader.ReadCelsius() is null, "No devices gives no fabricated temperature");
        using (var reader = new GpuTemperatureReader(() => new FakeApi((0, 4, 0))))
            Check(reader.ReadCelsius() is null && reader.Status.Contains("доступ", StringComparison.Ordinal), "Permission failures remain unavailable");
        Check(!GpuTemperatureReader.IsValidCelsius(double.NaN), "NaN is invalid");
        Check(!GpuTemperatureReader.IsValidCelsius(double.PositiveInfinity), "Infinity is invalid");
        Check(!GpuTemperatureReader.IsValidCelsius(-1), "A negative unsigned-sensor result is invalid");
        Check(GpuTemperatureReader.IsValidCelsius(150), "The configured plausibility ceiling is inclusive");
    }

    private static void CheckFailures()
    {
        int attempts = 0;
        using (var missing = new GpuTemperatureReader(() => { attempts++; throw new DllNotFoundException(); }))
        {
            Check(missing.ReadCelsius() is null && missing.Status.Contains("не найдена", StringComparison.Ordinal), "Missing NVML has a useful status");
            Check(missing.ReadCelsius() is null && attempts == 1, "Missing NVML is not repeatedly loaded every sample");
        }
        using (var missingEntry = new GpuTemperatureReader(() => throw new EntryPointNotFoundException()))
            Check(missingEntry.ReadCelsius() is null && missingEntry.Status.Contains("не поддерживает", StringComparison.Ordinal), "Missing exports are handled");
        using (var wrongArchitecture = new GpuTemperatureReader(() => throw new BadImageFormatException()))
            Check(wrongArchitecture.ReadCelsius() is null, "Incorrect native architecture is handled");
        var failedInitialization = new FakeApi((0, 0, 45)) { InitStatus = 9 };
        using (var reader = new GpuTemperatureReader(() => failedInitialization))
            Check(reader.ReadCelsius() is null && failedInitialization.Disposals == 1, "Failed initialization releases its library");
        using (var reader = new GpuTemperatureReader(() => new FakeApi((0, 0, 45)) { CountStatus = 1 }))
            Check(reader.ReadCelsius() is null, "Enumeration errors cannot become a numeric temperature");
    }

    private static void Probe(string output)
    {
        var readings = new List<object>();
        var reader = new GpuTemperatureReader();
        for (int index = 0; index < 3; index++)
        {
            double? celsius = reader.ReadCelsius();
            readings.Add(new { utc = DateTimeOffset.UtcNow, celsius, status = reader.Status });
            if (index < 2) Thread.Sleep(250);
        }
        reader.Dispose();
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            source = "Existing driver NVML; GPU sensor; hottest available NVIDIA device",
            api = new[] { "nvmlInit_v2", "nvmlDeviceGetCount_v2", "nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetTemperature", "nvmlShutdown" },
            documentation = "https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html",
            installedLibraryOnly = true, elevationRequested = false, elevated, subprocessStarted = false,
            readings, disposedStatus = reader.Status, checks = _checks
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class FakeApi : GpuTemperatureReader.INvmlApi
    {
        private readonly (uint HandleStatus, uint TemperatureStatus, uint Value)[] _devices;
        public int Initializations, Disposals;
        public uint InitStatus, CountStatus;
        public FakeApi(params (uint, uint, uint)[] devices) => _devices = devices;
        public uint Initialize() { Initializations++; return InitStatus; }
        public uint GetDeviceCount(out uint count) { count = (uint)_devices.Length; return CountStatus; }
        public uint GetDeviceHandle(uint index, out IntPtr device) { device = new IntPtr(index + 1); return _devices[index].HandleStatus; }
        public uint GetTemperature(IntPtr device, out uint temperature) { var value = _devices[device.ToInt32() - 1]; temperature = value.Value; return value.TemperatureStatus; }
        public void Dispose() => Disposals++;
    }
    private static void Equal(double expected, double? actual, string description) => Check(actual.HasValue && Math.Abs(actual.Value - expected) < .0001, description);
    private static void Check(bool passed, string description) { _checks++; if (!passed) throw new InvalidOperationException("FAIL: " + description); }
}
