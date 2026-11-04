namespace Luma.Island.Services;

/// <summary>The HUD and alerts share one set of existing collectors.</summary>
public sealed record MonitorRequirements(bool Cpu, bool Gpu, bool Memory, bool GpuTemperature,
    bool CpuTemperature, bool MemoryTemperature, bool Fps, bool OnePercentLow, bool FrameTime)
{
    public static MonitorRequirements Resolve(ComponentSettings components, AlertSettings? alerts, bool visible)
    {
        alerts = AlertSettings.Normalize(alerts);
        return new(visible && components.Cpu, visible && components.Gpu, visible && components.Memory,
            visible && (components.GpuTemperature || alerts.NeedsGpuTemperature),
            visible && (components.CpuTemperature || alerts.NeedsCpuTemperature),
            visible && (components.MemoryTemperature || alerts.NeedsMemoryTemperature),
            visible && components.Fps, visible && components.OnePercentLow,
            visible && (components.FrameTime || alerts.NeedsFrames));
    }
}
