using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Luma.Island.Services;

/// <summary>
/// Reads the hottest available NVIDIA GPU sensor through an already installed driver NVML.
/// Construction does not load a native library; callers supply their own sampling cadence.
/// </summary>
public sealed class GpuTemperatureReader : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<INvmlApi> _createApi;
    private INvmlApi? _api;
    private bool _attempted, _disposed;
    private volatile string _status = "Температура GPU ещё не запрашивалась";
    public string Status => _status;

    public GpuTemperatureReader() : this(() => new NativeNvmlApi()) { }
    internal GpuTemperatureReader(Func<INvmlApi> createApi) => _createApi = createApi ?? throw new ArgumentNullException(nameof(createApi));

    /// <summary>Returns degrees Celsius, or null when no trustworthy sensor reading is available.</summary>
    public double? ReadCelsius()
    {
        lock (_gate)
        {
            if (_disposed) { _status = "Мониторинг температуры GPU выключен"; return null; }
            if (!EnsureInitialized()) return null;
            uint status = _api!.GetDeviceCount(out uint count);
            if (status != 0) { _status = Failure(status); return null; }
            if (count == 0) { _status = "Совместимые GPU NVIDIA не найдены"; return null; }
            if (count > 128) { _status = "NVML вернула некорректное число GPU"; return null; }

            double? hottest = null;
            uint firstError = 0;
            bool invalidReading = false;
            for (uint index = 0; index < count; index++)
            {
                status = _api.GetDeviceHandle(index, out IntPtr device);
                if (status != 0)
                {
                    if (firstError == 0) firstError = status;
                    continue;
                }
                if (device == IntPtr.Zero) { invalidReading = true; continue; }
                status = _api.GetTemperature(device, out uint value);
                if (status != 0) { if (firstError == 0) firstError = status; continue; }
                if (!IsValidCelsius(value)) { invalidReading = true; continue; }
                hottest = hottest.HasValue ? Math.Max(hottest.Value, value) : value;
            }

            _status = hottest.HasValue ? "NVML · максимум доступных GPU" : firstError != 0 ? Failure(firstError) :
                invalidReading ? "NVML вернула некорректные данные датчика GPU" : "Температура GPU недоступна";
            return hottest;
        }
    }

    private bool EnsureInitialized()
    {
        if (_attempted) return _api is not null;
        _attempted = true;
        try
        {
            _api = _createApi();
            uint status = _api.Initialize();
            if (status == 0) return true;
            _status = Failure(status);
        }
        catch (Exception exception) when (exception is DllNotFoundException or FileNotFoundException or DirectoryNotFoundException)
        {
            _status = "NVML драйвера NVIDIA не найдена";
        }
        catch (EntryPointNotFoundException) { _status = "Установленная NVML не поддерживает чтение температуры"; }
        catch (BadImageFormatException) { _status = "Разрядность NVML не соответствует приложению"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _status = "Не удалось загрузить NVML драйвера NVIDIA";
        }
        _api?.Dispose();
        _api = null;
        return false;
    }

    internal static bool IsValidCelsius(double value) => double.IsFinite(value) && value >= 0 && value <= 150;
    private static string Failure(uint status) => status == 4 ? "Нет доступа к датчику температуры GPU" :
        status == 3 ? "GPU не поддерживает чтение температуры через NVML" : $"Температура GPU недоступна (NVML {status})";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _api?.Dispose();
            _api = null;
            _status = "Мониторинг температуры GPU выключен";
        }
    }

    internal interface INvmlApi : IDisposable
    {
        uint Initialize();
        uint GetDeviceCount(out uint count);
        uint GetDeviceHandle(uint index, out IntPtr device);
        uint GetTemperature(IntPtr device, out uint temperature);
    }

    private sealed class NativeNvmlApi : INvmlApi
    {
        private IntPtr _library;
        private bool _initialized;
        private readonly SimpleCall _initialize = null!, _shutdown = null!;
        private readonly CountCall _getCount = null!;
        private readonly HandleCall _getHandle = null!;
        private readonly TemperatureCall _getTemperature = null!;

        public NativeNvmlApi()
        {
            // NVIDIA documents System32 for DCH drivers and ProgramW6432/NVIDIA Corporation/NVSMI
            // for standard drivers. Never search the working directory, PATH, or a bundled DLL.
            // https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html
            // https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlInitializationAndCleanup.html
            // https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html
            string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll");
            string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", "nvml.dll");
            string path = File.Exists(system) ? system : File.Exists(standard) ? standard : throw new DllNotFoundException();
            try
            {
                _library = NativeLibrary.Load(path, typeof(GpuTemperatureReader).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
                T Bind<T>(string name) where T : Delegate
                {
                    if (!NativeLibrary.TryGetExport(_library, name, out IntPtr entry)) throw new EntryPointNotFoundException(name);
                    return Marshal.GetDelegateForFunctionPointer<T>(entry);
                }
                _initialize = Bind<SimpleCall>("nvmlInit_v2");
                _shutdown = Bind<SimpleCall>("nvmlShutdown");
                _getCount = Bind<CountCall>("nvmlDeviceGetCount_v2");
                _getHandle = Bind<HandleCall>("nvmlDeviceGetHandleByIndex_v2");
                _getTemperature = Bind<TemperatureCall>("nvmlDeviceGetTemperature");
            }
            catch { Dispose(); throw; }
        }

        public uint Initialize()
        {
            uint status = _initialize();
            _initialized = status == 0;
            return status;
        }
        public uint GetDeviceCount(out uint count) => _getCount(out count);
        public uint GetDeviceHandle(uint index, out IntPtr device) => _getHandle(index, out device);
        public uint GetTemperature(IntPtr device, out uint temperature) => _getTemperature(device, 0, out temperature); // NVML_TEMPERATURE_GPU
        public void Dispose()
        {
            if (_initialized) { _shutdown(); _initialized = false; }
            if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint SimpleCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CountCall(out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint HandleCall(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint TemperatureCall(IntPtr device, uint sensor, out uint temperature);
    }
}
