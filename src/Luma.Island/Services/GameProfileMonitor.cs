using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Luma.Island.Services;

/// <summary>Optional two-second process-only scan; no worker or timer is retained while disabled or unconfigured.</summary>
public sealed class GameProfileMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<GameProcessSnapshot>> _capture;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private GameProfileSelection? _latestValidSelection;
    private long _generation;
    private int _running;
    private volatile bool _disposed;
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public Task Completion { get { lock (_gate) return _worker; } }
    /// <summary>Raised on a worker. Check IsCurrent again after dispatching a selection to the UI.</summary>
    public event Action<GameProfileSelection>? Changed;

    public GameProfileMonitor() : this((names, cancellation) => Task.FromResult(GameProcessReader.Capture(names, cancellation)), TimeSpan.FromSeconds(2)) { }

    internal GameProfileMonitor(Func<IReadOnlyCollection<string>, CancellationToken, Task<GameProcessSnapshot>> capture, TimeSpan interval)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval;
    }

    public void Configure(GameProfileSettings settings, string? activeProfileId = null)
    {
        GameProfileSettings snapshot = GameProfileStore.Normalize(settings);
        CancellationTokenSource? previousCancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long generation = Interlocked.Increment(ref _generation);
            Volatile.Write(ref _latestValidSelection, null);
            previousCancellation = _cancellation;
            _cancellation = null;
            Volatile.Write(ref _running, 0);
            if (snapshot.Enabled && snapshot.Profiles.Length > 0)
            {
                var cancellation = new CancellationTokenSource();
                CancellationToken token = cancellation.Token;
                _cancellation = cancellation;
                Task previous = _worker;
                _worker = Task.Run(async () =>
                {
                    // Cancellation must finish the previous scan before another generation starts.
                    try { await previous.ConfigureAwait(false); }
                    catch (Exception exception) { App.Log(exception); }
                    await WatchAsync(snapshot, activeProfileId, generation, token).ConfigureAwait(false);
                });
                Volatile.Write(ref _running, 1);
            }
        }
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
    }

    public bool IsCurrent(GameProfileSelection selection) => selection.IsValid && !_disposed &&
        selection.Generation == Volatile.Read(ref _generation) &&
        ReferenceEquals(selection, Volatile.Read(ref _latestValidSelection));

    private async Task WatchAsync(GameProfileSettings settings, string? activeProfileId, long generation, CancellationToken cancellation)
    {
        string[] names = settings.Profiles.Select(profile => Path.GetFileName(profile.ExecutablePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        GameProfileSelection? last = null;
        bool captureFailed = false;
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_disposed || generation != Volatile.Read(ref _generation)) return;
                GameProcessSnapshot snapshot;
                try
                {
                    snapshot = await _capture(names, cancellation).ConfigureAwait(false);
                    captureFailed = false;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    if (!captureFailed) App.Log(exception);
                    captureFailed = true;
                    snapshot = new GameProcessSnapshot(false, Array.Empty<GameProcessInfo>(), 0, "process_snapshot_failed");
                }
                var selected = GameProfileSelector.Select(settings, snapshot, activeProfileId) with { Generation = generation };
                if (selected.IsValid) activeProfileId = selected.ProfileId;
                bool publish = false;
                lock (_gate)
                {
                    if (selected != last && !_disposed && generation == _generation && !cancellation.IsCancellationRequested)
                    {
                        last = selected;
                        // Keep an already queued valid selection usable during a failed scan.
                        // Only a newly emitted valid selection supersedes it, even for A -> B -> A.
                        if (selected.IsValid) Volatile.Write(ref _latestValidSelection, selected);
                        publish = true;
                    }
                }
                if (publish)
                {
                    try { Changed?.Invoke(selected); }
                    catch (Exception exception) { App.Log(exception); }
                }
                await Task.Delay(_interval, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { if (generation == Volatile.Read(ref _generation)) Volatile.Write(ref _running, 0); }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Increment(ref _generation);
            Volatile.Write(ref _latestValidSelection, null);
            cancellation = _cancellation;
            _cancellation = null;
            Volatile.Write(ref _running, 0);
            Changed = null;
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
    }
}
