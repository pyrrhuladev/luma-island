using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Luma.Island.Services;

internal sealed record MediaArtworkInput<T>(string Title, string Artist, string Album,
    Func<CancellationToken, Task<T?>> ReadArtworkAsync) where T : class;
internal sealed record MediaArtworkState<T>(string Title, string Artist, string Album, T? Artwork) where T : class;

/// <summary>
/// Serializes metadata/artwork I/O, retaining only the newest pending event.
/// There is no work while idle; null images and transient errors get only bounded retries.
/// Continuations preserve the caller's synchronization context for UI consumers.
/// </summary>
internal sealed class MediaArtworkCoordinator<T> : IDisposable where T : class
{
    private readonly object _gate = new();
    private readonly Action<MediaArtworkState<T>> _publish;
    private readonly Action<Exception>? _report;
    private readonly TimeSpan[] _retryDelays;
    private readonly TimeSpan _coalesceDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private Request? _active, _pending;
    private MediaArtworkState<T>? _state;
    private MediaArtworkState<T>? _coverConfirmationBaseline;
    private long _version;
    private bool _running, _disposed;

    public MediaArtworkCoordinator(Action<MediaArtworkState<T>> publish, Action<Exception>? report = null,
        TimeSpan[]? retryDelays = null, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? coalesceDelay = null)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _report = report;
        _retryDelays = retryDelays is null
            ? new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(1500) }
            : (TimeSpan[])retryDelays.Clone();
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _coalesceDelay = coalesceDelay ?? TimeSpan.FromMilliseconds(40);
    }

    public MediaArtworkState<T>? Current { get { lock (_gate) return _state; } }

    public Task RefreshAsync(Func<CancellationToken, Task<MediaArtworkInput<T>>> readMetadata, bool expectTrackChange = false)
    {
        ArgumentNullException.ThrowIfNull(readMetadata);
        Request request;
        Request? superseded, active;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            request = new Request(++_version, readMetadata, expectTrackChange, _coverConfirmationBaseline ?? _state);
            superseded = _pending;
            _pending = request;
            active = _active;
            if (!_running)
            {
                _running = true;
                _ = RunAsync();
            }
        }
        Cancel(active);
        FinishPending(superseded);
        return request.Completion.Task;
    }

    public void Reset()
    {
        Request? pending, active;
        lock (_gate)
        {
            ++_version;
            pending = _pending;
            _pending = null;
            active = _active;
            _state = null;
            _coverConfirmationBaseline = null;
        }
        Cancel(active);
        FinishPending(pending);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            Request request;
            lock (_gate)
            {
                if (_pending is null) { _active = null; _running = false; return; }
                request = _pending;
                _pending = null;
                _active = request;
            }
            try { await RefreshCoreAsync(request); }
            catch (Exception exception) { _report?.Invoke(exception); }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_active, request)) _active = null; }
                request.Cancellation.Dispose();
                request.Completion.TrySetResult();
            }
        }
    }

    private async Task RefreshCoreAsync(Request request)
    {
        CancellationToken token = request.Token;
        try
        {
            if (_coalesceDelay > TimeSpan.Zero) await _delay(_coalesceDelay, token);
            for (int attempt = 0; attempt <= _retryDelays.Length; attempt++)
            {
                if (attempt > 0) await _delay(_retryDelays[attempt - 1], token);
                if (!IsCurrent(request)) return;
                try
                {
                    // Every retry obtains fresh metadata and a fresh thumbnail reference.
                    MediaArtworkInput<T> input = await request.ReadMetadata(token);
                    if (!CommitMetadata(request, input)) return;
                    T? artwork = await input.ReadArtworkAsync(token);
                    if (!IsCurrent(request)) return;
                    if (artwork is null) continue;
                    bool sameCoverAsBaseline = request.Baseline is not null &&
                        EqualityComparer<T?>.Default.Equals(request.Baseline.Artwork, artwork);
                    bool newTrackWithPreviousCover = request.Baseline is not null &&
                        !SameMetadata(request.Baseline, input) && sameCoverAsBaseline;
                    // A non-null candidate can be a legitimate shared album image, so
                    // show it immediately while checking for a delayed replacement.
                    bool confirmPreviousCover = newTrackWithPreviousCover && attempt < _retryDelays.Length;
                    CommitArtwork(request, input, artwork, confirmPreviousCover);
                    if (confirmPreviousCover) continue;
                    bool stillOldTrack = request.ExpectTrackChange && request.Baseline is not null &&
                        SameMetadata(request.Baseline, input) && sameCoverAsBaseline;
                    if (!stillOldTrack) return;
                }
                catch (OperationCanceledException) when (!IsCurrent(request)) { return; }
                catch (Exception exception)
                {
                    if (!IsCurrent(request)) return;
                    _report?.Invoke(exception);
                }
            }
        }
        catch (OperationCanceledException) when (!IsCurrent(request)) { }
    }

    private bool CommitMetadata(Request request, MediaArtworkInput<T> input)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(request)) return false;
            T? artwork = _state is not null && SameMetadata(_state, input) ? _state.Artwork : null;
            Commit(new MediaArtworkState<T>(input.Title, input.Artist, input.Album, artwork));
            return IsCurrentLocked(request);
        }
    }

    private void CommitArtwork(Request request, MediaArtworkInput<T> input, T artwork, bool confirmPreviousCover)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(request)) return;
            // Preserve this baseline if another event for the same new title
            // supersedes a request while its confirmation delay is pending.
            _coverConfirmationBaseline = confirmPreviousCover ? request.Baseline : null;
            Commit(new MediaArtworkState<T>(input.Title, input.Artist, input.Album, artwork));
        }
    }

    private void Commit(MediaArtworkState<T> state)
    {
        if (_state == state) return;
        _state = state;
        _publish(state);
    }
    private bool IsCurrent(Request request) { lock (_gate) return IsCurrentLocked(request); }
    private bool IsCurrentLocked(Request request) => !_disposed && request.Version == _version && !request.Token.IsCancellationRequested;
    private static bool SameMetadata(MediaArtworkState<T> state, MediaArtworkInput<T> input) =>
        state.Title == input.Title && state.Artist == input.Artist && state.Album == input.Album;

    private void Cancel(Request? request)
    {
        if (request is null) return;
        try { request.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException exception) { _report?.Invoke(exception); }
    }
    private void FinishPending(Request? request)
    {
        if (request is null) return;
        Cancel(request);
        request.Cancellation.Dispose();
        request.Completion.TrySetResult();
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        Reset();
    }

    private sealed class Request
    {
        public readonly long Version;
        public readonly Func<CancellationToken, Task<MediaArtworkInput<T>>> ReadMetadata;
        public readonly bool ExpectTrackChange;
        public readonly MediaArtworkState<T>? Baseline;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly CancellationToken Token;
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Request(long version, Func<CancellationToken, Task<MediaArtworkInput<T>>> readMetadata,
            bool expectTrackChange, MediaArtworkState<T>? baseline)
        {
            Version = version; ReadMetadata = readMetadata; ExpectTrackChange = expectTrackChange; Baseline = baseline;
            Token = Cancellation.Token;
        }
    }
}
