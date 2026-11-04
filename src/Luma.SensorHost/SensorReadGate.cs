using System;

namespace Luma.SensorHost;

/// <summary>Service-wide monotonic hardware cadence, shared across pipe connections; never waits or polls.</summary>
internal sealed class SensorReadGate
{
    private readonly Func<long> _milliseconds;
    private bool _sampled;
    private long _last;
    internal SensorReadGate(Func<long>? milliseconds = null) => _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    internal bool TryAcquire()
    {
        long now = _milliseconds();
        if (_sampled && now >= _last && (double)now - _last < 2000) return false;
        _last = now; _sampled = true;
        return true;
    }
}
