using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Windows.Storage.Streams;
using MediaColor = System.Windows.Media.Color;

namespace Companion.Services;

public sealed record AlbumPalette(MediaColor PrimaryAccent, MediaColor SecondaryAccent)
{
    public static AlbumPalette Default { get; } = new(
        MediaColor.FromRgb(0x38, 0xBD, 0xF8),
        MediaColor.FromRgb(0x81, 0x8C, 0xF8));
}

public sealed record MediaTrackInfo(
    string Title,
    string Artist,
    string AlbumTitle,
    bool IsPlaying,
    byte[]? ThumbnailBytes = null,
    TimeSpan Position = default,
    TimeSpan Duration = default,
    DateTimeOffset LastUpdatedUtc = default,
    BitmapSource? ThumbnailBitmap = null,
    AlbumPalette? Palette = null)
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
    private const float DuckedSessionVolume = 0.22f;
    private readonly object _audioDuckingSync = new();
    private readonly Dictionary<string, float> _originalSessionVolumes = new(StringComparer.Ordinal);
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
            BitmapSource? thumbnailBitmap = DecodeThumbnail(thumbnail);
            AlbumPalette palette = ExtractAlbumPalette(thumbnailBitmap);
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
                lastUpdatedUtc,
                thumbnailBitmap,
                palette);

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

    public static BitmapSource? DecodeThumbnail(byte[]? thumbnailBytes)
    {
        if (thumbnailBytes is null || thumbnailBytes.Length == 0 || thumbnailBytes.Length > 4 * 1024 * 1024)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(thumbnailBytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = 32;
            image.DecodePixelHeight = 32;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            if (image.PixelWidth == 32 && image.PixelHeight == 32)
            {
                return image;
            }

            var scaled = new TransformedBitmap(
                image,
                new ScaleTransform(32.0 / image.PixelWidth, 32.0 / image.PixelHeight));
            if (scaled.PixelWidth == 32 && scaled.PixelHeight == 32)
            {
                scaled.Freeze();
                return scaled;
            }

            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            int stride = 32 * 4;
            byte[] pixels = new byte[stride * 32];
            converted.CopyPixels(pixels, stride, 0);
            var exactSize = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            exactSize.Freeze();
            return exactSize;
        }
        catch
        {
            return null;
        }
    }

    public static AlbumPalette ExtractAlbumPalette(byte[]? thumbnailBytes) =>
        ExtractAlbumPalette(DecodeThumbnail(thumbnailBytes));

    public static AlbumPalette ExtractAlbumPalette(BitmapSource? thumbnail)
    {
        if (thumbnail is null || thumbnail.PixelWidth == 0 || thumbnail.PixelHeight == 0)
        {
            return AlbumPalette.Default;
        }

        try
        {
            BitmapSource source = thumbnail.Format == PixelFormats.Bgra32
                ? thumbnail
                : new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0);
            if (source.CanFreeze && !source.IsFrozen)
            {
                source.Freeze();
            }

            int stride = checked(source.PixelWidth * 4);
            byte[] pixels = new byte[checked(stride * source.PixelHeight)];
            source.CopyPixels(pixels, stride, 0);

            var hueBins = new Dictionary<int, HueBin>();
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                double blue = pixels[offset] / 255.0;
                double green = pixels[offset + 1] / 255.0;
                double red = pixels[offset + 2] / 255.0;
                ToHsl(red, green, blue, out double hue, out double saturation, out double lightness);
                double maximum = Math.Max(red, Math.Max(green, blue));
                double minimum = Math.Min(red, Math.Min(green, blue));
                if (maximum < 0.20 || minimum > 0.94 || saturation < 0.14)
                {
                    continue;
                }

                int binIndex = (int)(hue / 360.0 * 24.0) % 24;
                if (!hueBins.TryGetValue(binIndex, out HueBin? bin))
                {
                    bin = new HueBin();
                    hueBins.Add(binIndex, bin);
                }

                bin.Add(hue, saturation, lightness);
            }

            if (hueBins.Count == 0)
            {
                return AlbumPalette.Default;
            }

            HueBin primary = hueBins.Values.OrderByDescending(bin => bin.Weight).First();
            double primaryHue = primary.MeanHue;
            HueBin? secondary = hueBins.Values
                .Where(bin => HueDistance(primaryHue, bin.MeanHue) >= 28.0)
                .OrderByDescending(bin => bin.Weight)
                .FirstOrDefault();

            double primarySaturation = Math.Clamp(primary.MeanSaturation, 0.42, 0.88);
            double primaryLightness = Math.Clamp(primary.MeanLightness, 0.58, 0.78);
            MediaColor primaryColor = FromHsl(primaryHue, primarySaturation, primaryLightness);

            MediaColor secondaryColor;
            if (secondary is null)
            {
                secondaryColor = FromHsl(
                    (primaryHue + 38.0) % 360.0,
                    Math.Clamp(primarySaturation * 0.86, 0.42, 0.82),
                    Math.Clamp(primaryLightness + 0.04, 0.58, 0.78));
            }
            else
            {
                secondaryColor = FromHsl(
                    secondary.MeanHue,
                    Math.Clamp(secondary.MeanSaturation, 0.42, 0.88),
                    Math.Clamp(secondary.MeanLightness, 0.58, 0.78));
            }

            return new AlbumPalette(primaryColor, secondaryColor);
        }
        catch
        {
            return AlbumPalette.Default;
        }
    }

    private sealed class HueBin
    {
        private double _hueSin;
        private double _hueCos;
        private double _saturation;
        private double _lightness;

        public int Count { get; private set; }
        public double Weight => Count * (0.5 + MeanSaturation);
        public double MeanHue => (Math.Atan2(_hueSin, _hueCos) * 180.0 / Math.PI + 360.0) % 360.0;
        public double MeanSaturation => Count == 0 ? 0 : _saturation / Count;
        public double MeanLightness => Count == 0 ? 0.68 : _lightness / Count;

        public void Add(double hue, double saturation, double lightness)
        {
            double radians = hue * Math.PI / 180.0;
            _hueSin += Math.Sin(radians);
            _hueCos += Math.Cos(radians);
            _saturation += saturation;
            _lightness += lightness;
            Count++;
        }
    }

    private static double HueDistance(double first, double second)
    {
        double distance = Math.Abs(first - second) % 360.0;
        return Math.Min(distance, 360.0 - distance);
    }

    private static void ToHsl(double red, double green, double blue, out double hue, out double saturation, out double lightness)
    {
        double maximum = Math.Max(red, Math.Max(green, blue));
        double minimum = Math.Min(red, Math.Min(green, blue));
        double delta = maximum - minimum;
        lightness = (maximum + minimum) / 2.0;
        saturation = delta == 0
            ? 0
            : delta / (1.0 - Math.Abs(2.0 * lightness - 1.0));

        if (delta == 0)
        {
            hue = 0;
        }
        else if (maximum == red)
        {
            hue = 60.0 * (((green - blue) / delta) % 6.0);
        }
        else if (maximum == green)
        {
            hue = 60.0 * (((blue - red) / delta) + 2.0);
        }
        else
        {
            hue = 60.0 * (((red - green) / delta) + 4.0);
        }

        if (hue < 0)
        {
            hue += 360.0;
        }
    }

    private static MediaColor FromHsl(double hue, double saturation, double lightness)
    {
        hue = (hue % 360.0 + 360.0) % 360.0;
        double chroma = (1.0 - Math.Abs(2.0 * lightness - 1.0)) * saturation;
        double x = chroma * (1.0 - Math.Abs(hue / 60.0 % 2.0 - 1.0));
        double m = lightness - chroma / 2.0;
        (double red, double green, double blue) = hue switch
        {
            < 60 => (chroma, x, 0),
            < 120 => (x, chroma, 0),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return MediaColor.FromRgb(
            ToByte(red + m),
            ToByte(green + m),
            ToByte(blue + m));
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255.0), 0, 255);

    public void SetAudioDucking(bool shouldDuck)
    {
        lock (_audioDuckingSync)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;
                for (int index = 0; index < sessions.Count; index++)
                {
                    AudioSessionControl session = sessions[index];
                    try
                    {
                        if (session.IsSystemSoundsSession || session.GetProcessID == (uint)Environment.ProcessId)
                        {
                            continue;
                        }

                        string instanceId = session.GetSessionInstanceIdentifier;
                        using SimpleAudioVolume volume = session.SimpleAudioVolume;
                        if (shouldDuck)
                        {
                            if (!_originalSessionVolumes.TryGetValue(instanceId, out float original))
                            {
                                original = volume.Volume;
                                _originalSessionVolumes.Add(instanceId, original);
                            }

                            volume.Volume = Math.Clamp(original * DuckedSessionVolume, 0.0f, 1.0f);
                        }
                        else if (_originalSessionVolumes.TryGetValue(instanceId, out float original))
                        {
                            volume.Volume = Math.Clamp(original, 0.0f, 1.0f);
                            _originalSessionVolumes.Remove(instanceId);
                        }
                    }
                    catch
                    {
                        // A process can end while the audio session list is being read.
                    }
                    finally
                    {
                        session.Dispose();
                    }
                }

                if (!shouldDuck)
                {
                    _originalSessionVolumes.Clear();
                }
            }
            catch
            {
                // Core Audio is optional on headless devices; do not interrupt capture.
            }
        }
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
        catch (Exception exception)
        {
            SafeDiagnostics.WriteFailure("media", "send-media-key", exception);
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
            catch (Exception exception)
            {
                SafeDiagnostics.WriteFailure("media", "toggle-play-pause", exception);
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
            catch (Exception exception)
            {
                SafeDiagnostics.WriteFailure("media", "skip-next", exception);
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
            catch (Exception exception)
            {
                SafeDiagnostics.WriteFailure("media", "skip-previous", exception);
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
        SetAudioDucking(shouldDuck: false);
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
