using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Luma.Island.Services;

public sealed record MediaSnapshot(
    string Title,
    string Artist,
    bool IsPlaying,
    bool HasSession,
    bool CanToggle,
    bool CanNext,
    bool CanPrevious,
    BitmapSource? Artwork)
{
    public static MediaSnapshot Empty { get; } = new("Музыка рядом", "Включите любимый трек", false, false, false, false, false, null);
}

/// <summary>SMTC subscriptions; no polling, audio capture, or helper processes.</summary>
public sealed class MediaService : IDisposable
{
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private Task? _initialization;
    private int _generation;
    private int _metadataVersion;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string? _metadataKey;
    private BitmapSource? _artwork;
    private bool _disposed;

    public bool IsEnabled { get; private set; } = true;
    public MediaSnapshot Current { get; private set; } = MediaSnapshot.Empty;
    /// <summary>Raised on the WPF application dispatcher.</summary>
    public event Action<MediaSnapshot>? Changed;

    public Task InitializeAsync()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsEnabled) return Task.CompletedTask;
        return _initialization ??= InitializeCoreAsync(_generation);
    }

    public Task SetEnabledAsync(bool enabled)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsEnabled == enabled) return enabled ? InitializeAsync() : Task.CompletedTask;
        IsEnabled = enabled;
        if (enabled) return InitializeAsync();

        Disconnect();
        Current = MediaSnapshot.Empty;
        Changed?.Invoke(Current);
        return Task.CompletedTask;
    }

    private async Task InitializeCoreAsync(int generation)
    {
        // Cache the initialization task before native events can reenter a caller.
        await Task.Yield();
        if (!IsActive(generation)) return;
        GlobalSystemMediaTransportControlsSessionManager manager;
        try { manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception) when (!IsActive(generation)) { return; }
        if (!IsActive(generation)) return;
        _manager = manager;
        try
        {
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            manager.SessionsChanged += OnSessionsChanged;
            SelectSession();
        }
        catch
        {
            // Do not retain a partially initialized event subscription.
            if (IsActive(generation) && ReferenceEquals(manager, _manager)) Disconnect();
            throw;
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => Dispatch(() => { if (ReferenceEquals(sender, _manager)) SelectSession(); });
    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => Dispatch(() => { if (ReferenceEquals(sender, _manager)) SelectSession(); });
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => Dispatch(() => { if (ReferenceEquals(sender, _session)) RequestMetadata(); });
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => Dispatch(() => { if (ReferenceEquals(sender, _session)) Publish(); });

    private void SelectSession()
    {
        if (_disposed || !IsEnabled || _manager is null) return;
        try
        {
            var next = _manager.GetCurrentSession() ?? _manager.GetSessions().FirstOrDefault();
            if (ReferenceEquals(next, _session)) return;
            UnsubscribeSession();
            _session = next;
            _metadataVersion++;
            _metadataKey = null;
            _title = string.Empty;
            _artist = string.Empty;
            _artwork = null;
            if (next is not null)
            {
                next.MediaPropertiesChanged += OnMediaPropertiesChanged;
                next.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }
            Publish();
            RequestMetadata();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Trace.WriteLine("SMTC session: " + ex.Message);
        }
    }

    private void RequestMetadata()
    {
        if (_disposed || !IsEnabled || _session is null) return;
        _ = RefreshMetadataAsync(_session, ++_metadataVersion);
    }

    private async Task RefreshMetadataAsync(GlobalSystemMediaTransportControlsSession session, int version)
    {
        try
        {
            if (!IsCurrent(session, version)) return;
            var properties = await session.TryGetMediaPropertiesAsync();
            if (!IsCurrent(session, version)) return;
            var key = properties.Title + "\0" + properties.Artist + "\0" + properties.AlbumTitle;
            BitmapSource? artwork = _artwork;
            if (key != _metadataKey)
            {
                artwork = await ReadArtworkAsync(properties.Thumbnail, session, version);
                if (!IsCurrent(session, version)) return;
            }
            _title = properties.Title ?? string.Empty;
            _artist = properties.Artist ?? string.Empty;
            _metadataKey = key;
            _artwork = artwork;
            Publish();
        }
        catch (Exception ex)
        {
            // A player can disappear while WinRT is returning its metadata.
            Trace.WriteLine("SMTC metadata: " + ex.Message);
        }
    }

    private bool IsCurrent(GlobalSystemMediaTransportControlsSession session, int version)
        => !_disposed && IsEnabled && version == _metadataVersion && ReferenceEquals(session, _session);

    private bool IsActive(int generation) => !_disposed && IsEnabled && generation == _generation;

    private async Task<BitmapSource?> ReadArtworkAsync(IRandomAccessStreamReference? thumbnail,
        GlobalSystemMediaTransportControlsSession session, int version)
    {
        if (thumbnail is null || !IsCurrent(session, version)) return null;
        try
        {
            using var source = await thumbnail.OpenReadAsync();
            if (!IsCurrent(session, version) || source.Size == 0 || source.Size > 8 * 1024 * 1024) return null;
            using var input = source.GetInputStreamAt(0);
            using var reader = new DataReader(input);
            var size = checked((uint)source.Size);
            var loaded = await reader.LoadAsync(size);
            if (!IsCurrent(session, version) || loaded != size) return null;
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            using var stream = new MemoryStream(bytes, writable: false);
            return ArtworkDecoder.Decode(stream);
        }
        catch (Exception ex)
        {
            Trace.WriteLine("SMTC artwork: " + ex.Message);
            return null;
        }
    }

    private void Publish()
    {
        if (_disposed || !IsEnabled) return;
        MediaSnapshot snapshot;
        try
        {
            if (_session is null) snapshot = MediaSnapshot.Empty;
            else
            {
                var info = _session.GetPlaybackInfo();
                var controls = info.Controls;
                snapshot = new MediaSnapshot(
                    string.IsNullOrWhiteSpace(_title) ? "Медиаплеер" : _title,
                    string.IsNullOrWhiteSpace(_artist) ? "Сейчас играет" : _artist,
                    info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    true, controls.IsPlayPauseToggleEnabled, controls.IsNextEnabled, controls.IsPreviousEnabled, _artwork);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Trace.WriteLine("SMTC playback: " + ex.Message);
            return;
        }
        if (snapshot == Current) return;
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }

    public Task ToggleAsync() => ExecuteAsync(0);
    public Task NextAsync() => ExecuteAsync(1);
    public Task PreviousAsync() => ExecuteAsync(2);

    private async Task ExecuteAsync(int command)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !IsEnabled || _session is null) return;
        var session = _session;
        int generation = _generation;
        bool accepted;
        try
        {
            if (command == 0) accepted = await session.TryTogglePlayPauseAsync();
            else if (command == 1) accepted = await session.TrySkipNextAsync();
            else accepted = await session.TrySkipPreviousAsync();
        }
        catch (Exception) when (!IsActive(generation)) { return; }
        if (!IsActive(generation) || !ReferenceEquals(session, _session)) return;
        if (!accepted) throw new InvalidOperationException("Плеер не выполнил команду. Попробуйте управлять воспроизведением в самом приложении.");
        Publish();
    }

    private void Dispatch(Action action)
    {
        if (_disposed || !IsEnabled || _dispatcher.HasShutdownStarted) return;
        int generation = _generation;
        _dispatcher.BeginInvoke(new Action(() => { if (IsActive(generation)) action(); }), DispatcherPriority.Background);
    }

    private void UnsubscribeSession()
    {
        if (_session is null) return;
        // A player may exit before WinRT removes its event registrations.
        // Remove both independently so one disconnected event cannot prevent
        // the other cleanup or switching to the next player.
        try { _session.MediaPropertiesChanged -= OnMediaPropertiesChanged; }
        catch (COMException ex) { Trace.WriteLine("SMTC metadata unsubscribe: " + ex.Message); }
        try { _session.PlaybackInfoChanged -= OnPlaybackInfoChanged; }
        catch (COMException ex) { Trace.WriteLine("SMTC playback unsubscribe: " + ex.Message); }
    }

    private void UnsubscribeManager()
    {
        var manager = _manager;
        _manager = null;
        if (manager is null) return;
        try { manager.CurrentSessionChanged -= OnCurrentSessionChanged; }
        catch (COMException ex) { Trace.WriteLine("SMTC manager unsubscribe: " + ex.Message); }
        try { manager.SessionsChanged -= OnSessionsChanged; }
        catch (COMException ex) { Trace.WriteLine("SMTC sessions unsubscribe: " + ex.Message); }
    }

    private void Disconnect()
    {
        _generation++;
        _metadataVersion++;
        _initialization = null;
        UnsubscribeSession();
        _session = null;
        UnsubscribeManager();
        _metadataKey = null;
        _title = string.Empty;
        _artist = string.Empty;
        _artwork = null;
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        IsEnabled = false;
        Disconnect();
        Current = MediaSnapshot.Empty;
        Changed = null;
    }
}
