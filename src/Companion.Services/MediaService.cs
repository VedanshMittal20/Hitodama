using System.Runtime.InteropServices;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Companion.Services;

public sealed record MediaTrackInfo(
    string Title,
    string Artist,
    string AlbumTitle,
    bool IsPlaying,
    byte[]? ThumbnailBytes = null,
    TimeSpan Position = default,
    TimeSpan Duration = default,
    DateTimeOffset LastUpdatedUtc = default)
{
    public TimeSpan GetCurrentPosition(DateTimeOffset? now = null)
    {
        if (!IsPlaying || Duration <= TimeSpan.Zero || LastUpdatedUtc == default)
        {
            return Position;
        }

        DateTimeOffset utcNow = now ?? DateTimeOffset.UtcNow;
        TimeSpan elapsed = utcNow - LastUpdatedUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        TimeSpan current = Position + elapsed;
        return current > Duration ? Duration : current;
    }
}

public sealed class MediaService : IAsyncDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    public event Action<MediaTrackInfo?>? TrackChanged;
    public event Action<bool>? PlaybackStateChanged;

    public MediaTrackInfo? CurrentTrack { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager is not null)
            {
                _sessionManager.CurrentSessionChanged += OnSessionManagerCurrentSessionChanged;
                _sessionManager.SessionsChanged += OnSessionManagerSessionsChanged;
                await RefreshCurrentSessionAsync();
            }
        }
        catch
        {
            // GSMTC may fail if media service or audio engine is disabled; fail gracefully
        }
    }

    private async void OnSessionManagerCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        await RefreshCurrentSessionAsync();
    }

    private async void OnSessionManagerSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        await RefreshCurrentSessionAsync();
    }

    public async Task RefreshCurrentSessionAsync()
    {
        try
        {
            if (_sessionManager is null)
            {
                try
                {
                    _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    if (_sessionManager is not null)
                    {
                        _sessionManager.CurrentSessionChanged += OnSessionManagerCurrentSessionChanged;
                        _sessionManager.SessionsChanged += OnSessionManagerSessionsChanged;
                    }
                }
                catch
                {
                    // GSMTC unavailable
                }
            }

            if (_currentSession is not null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                _currentSession = null;
            }

            if (_sessionManager is not null)
            {
                _currentSession = _sessionManager.GetCurrentSession();
                if (_currentSession is null)
                {
                    var sessions = _sessionManager.GetSessions();
                    if (sessions is not null && sessions.Count > 0)
                    {
                        _currentSession = sessions.FirstOrDefault(s =>
                        {
                            try
                            {
                                return s.GetPlaybackInfo()?.PlaybackStatus ==
                                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                            }
                            catch
                            {
                                return false;
                            }
                        }) ?? sessions.FirstOrDefault();
                    }
                }
            }

            if (_currentSession is not null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                await UpdateTrackInfoAsync();
            }
            else
            {
                CurrentTrack = null;
                TrackChanged?.Invoke(null);
            }
        }
        catch
        {
            CurrentTrack = null;
            TrackChanged?.Invoke(null);
        }
    }

    private async void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        await UpdateTrackInfoAsync();
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        try
        {
            bool isPlaying = IsPlaybackPlaying(sender.GetPlaybackInfo());
            if (CurrentTrack is not null)
            {
                CurrentTrack = CurrentTrack with { IsPlaying = isPlaying };
                PlaybackStateChanged?.Invoke(isPlaying);
                TrackChanged?.Invoke(CurrentTrack);
            }
            else
            {
                PlaybackStateChanged?.Invoke(isPlaying);
                _ = UpdateTrackInfoAsync();
            }
        }
        catch
        {
            _ = RefreshCurrentSessionAsync();
        }
    }

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        try
        {
            if (CurrentTrack is null || !ReferenceEquals(sender, _currentSession))
            {
                return;
            }

            var timeline = sender.GetTimelineProperties();
            TimeSpan duration = timeline is not null && timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;
            DateTimeOffset lastUpdatedUtc = (timeline is not null && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
                ? timeline.LastUpdatedTime.UtcDateTime
                : DateTimeOffset.UtcNow;
            TimeSpan position = timeline is not null
                ? ClampPosition(timeline.Position, duration)
                : TimeSpan.Zero;

            CurrentTrack = CurrentTrack with
            {
                Position = position,
                Duration = duration,
                LastUpdatedUtc = lastUpdatedUtc
            };
            TrackChanged?.Invoke(CurrentTrack);
        }
        catch
        {
            // Transient timeline read errors ignored
        }
    }

    private async Task UpdateTrackInfoAsync()
    {
        if (_currentSession is null)
        {
            CurrentTrack = null;
            TrackChanged?.Invoke(null);
            return;
        }

        try
        {
            var properties = await _currentSession.TryGetMediaPropertiesAsync();
            var playback = _currentSession.GetPlaybackInfo();
            var timeline = _currentSession.GetTimelineProperties();
            bool isPlaying = IsPlaybackPlaying(playback);

            string title = properties is not null && !string.IsNullOrWhiteSpace(properties.Title)
                ? properties.Title
                : (properties is not null && !string.IsNullOrWhiteSpace(properties.AlbumTitle)
                    ? properties.AlbumTitle
                    : (!string.IsNullOrWhiteSpace(_currentSession.SourceAppUserModelId)
                        ? _currentSession.SourceAppUserModelId
                        : "Media Track"));

            string artist = properties is not null && !string.IsNullOrWhiteSpace(properties.Artist)
                ? properties.Artist
                : (properties is not null && !string.IsNullOrWhiteSpace(properties.AlbumArtist)
                    ? properties.AlbumArtist
                    : "Audio playback");

            byte[]? thumbnail = properties is not null ? await ReadThumbnailAsync(properties.Thumbnail) : null;
            TimeSpan duration = timeline is not null && timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;
            DateTimeOffset lastUpdatedUtc = (timeline is not null && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
                ? timeline.LastUpdatedTime.UtcDateTime
                : DateTimeOffset.UtcNow;
            TimeSpan position = timeline is not null
                ? ClampPosition(timeline.Position, duration)
                : TimeSpan.Zero;

            CurrentTrack = new MediaTrackInfo(
                title,
                artist,
                properties?.AlbumTitle ?? string.Empty,
                isPlaying,
                thumbnail,
                position,
                duration,
                lastUpdatedUtc);

            TrackChanged?.Invoke(CurrentTrack);
            PlaybackStateChanged?.Invoke(isPlaying);
        }
        catch
        {
            CurrentTrack = null;
            TrackChanged?.Invoke(null);
        }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        using IRandomAccessStreamWithContentType stream = await thumbnail.OpenReadAsync();
        if (stream.Size is 0 or > 4 * 1024 * 1024)
        {
            return null;
        }

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint size = checked((uint)stream.Size);
        await reader.LoadAsync(size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration) =>
        position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > duration
                ? duration
                : position;

    private static bool IsPlaybackPlaying(GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo)
    {
        if (playbackInfo is null)
        {
            return false;
        }

        return playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPrevTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;
    private const uint KeyeventfKeyup = 0x0002;

    private static void SendMediaKey(byte vk)
    {
        try
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KeyeventfKeyup, UIntPtr.Zero);
        }
        catch
        {
        }
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        if (_currentSession is null)
        {
            await RefreshCurrentSessionAsync();
        }

        if (_currentSession is not null)
        {
            try
            {
                if (await _currentSession.TryTogglePlayPauseAsync())
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        // Fallback: send system media key
        SendMediaKey(VkMediaPlayPause);
        return true;
    }

    public async Task<bool> SkipNextAsync()
    {
        if (_currentSession is null)
        {
            await RefreshCurrentSessionAsync();
        }

        if (_currentSession is not null)
        {
            try
            {
                if (await _currentSession.TrySkipNextAsync())
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        // Fallback: send system media key
        SendMediaKey(VkMediaNextTrack);
        return true;
    }

    public async Task<bool> SkipPreviousAsync()
    {
        if (_currentSession is null)
        {
            await RefreshCurrentSessionAsync();
        }

        if (_currentSession is not null)
        {
            try
            {
                if (await _currentSession.TrySkipPreviousAsync())
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        // Fallback: send system media key
        SendMediaKey(VkMediaPrevTrack);
        return true;
    }

    public Task<bool> NextAsync() => SkipNextAsync();
    public Task<bool> PreviousAsync() => SkipPreviousAsync();

    public ValueTask DisposeAsync()
    {
        if (_sessionManager is not null)
        {
            _sessionManager.CurrentSessionChanged -= OnSessionManagerCurrentSessionChanged;
            _sessionManager.SessionsChanged -= OnSessionManagerSessionsChanged;
        }

        if (_currentSession is not null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _currentSession = null;
        _sessionManager = null;
        return ValueTask.CompletedTask;
    }
}
