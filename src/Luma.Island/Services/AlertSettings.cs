using System;
using System.Text.Json.Serialization;

namespace Luma.Island.Services;

/// <summary>User-selected warning thresholds. Nothing is sampled unless the master switch is enabled.</summary>
public sealed record AlertSettings(
    bool Enabled = false,
    bool CpuTemperature = true,
    bool GpuTemperature = true,
    bool MemoryTemperature = false,
    bool Stutters = true,
    double CpuThreshold = 90,
    double GpuThreshold = 85,
    double MemoryThreshold = 70,
    double FrameTimeThreshold = 40,
    int StutterCount = 3)
{
    public static AlertSettings Default { get; } = new();
    [JsonIgnore] public bool NeedsCpuTemperature => Enabled && CpuTemperature;
    [JsonIgnore] public bool NeedsGpuTemperature => Enabled && GpuTemperature;
    [JsonIgnore] public bool NeedsMemoryTemperature => Enabled && MemoryTemperature;
    [JsonIgnore] public bool NeedsFrames => Enabled && Stutters;

    public static AlertSettings Normalize(AlertSettings? settings)
    {
        settings ??= Default;
        return settings with
        {
            CpuThreshold = Finite(settings.CpuThreshold, Default.CpuThreshold, 30, 115),
            GpuThreshold = Finite(settings.GpuThreshold, Default.GpuThreshold, 30, 115),
            MemoryThreshold = Finite(settings.MemoryThreshold, Default.MemoryThreshold, 30, 115),
            FrameTimeThreshold = Finite(settings.FrameTimeThreshold, Default.FrameTimeThreshold, 10, 250),
            StutterCount = Math.Clamp(settings.StutterCount, 2, 10)
        };
    }

    private static double Finite(double value, double fallback, double minimum, double maximum)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
