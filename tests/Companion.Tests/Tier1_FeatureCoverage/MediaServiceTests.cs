using Companion.Services;
using FluentAssertions;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class MediaServiceTests
{
    [Fact]
    public void MediaService_ExtractAlbumPalette_UsesDefaultsForInvalidThumbnail()
    {
        AlbumPalette palette = MediaService.ExtractAlbumPalette([0x01, 0x02, 0x03]);

        palette.Should().Be(AlbumPalette.Default);
        palette.PrimaryAccent.Should().Be(MediaColor.FromRgb(0x38, 0xBD, 0xF8));
        palette.SecondaryAccent.Should().Be(MediaColor.FromRgb(0x81, 0x8C, 0xF8));
    }

    [Fact]
    public void MediaService_DecodesFrozen32PixelThumbnailAndExtractsChromaticColors()
    {
        byte[] pixels =
        [
            0, 0, 255, 255, 0, 0, 255, 255,
            255, 0, 0, 255, 255, 0, 0, 255,
            0, 0, 0, 255, 255, 255, 255, 255,
            0, 0, 255, 255, 255, 0, 0, 255
        ];
        BitmapSource source = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        BitmapSource? decoded = MediaService.DecodeThumbnail(stream.ToArray());
        decoded.Should().NotBeNull();
        decoded!.PixelWidth.Should().Be(32);
        decoded.PixelHeight.Should().Be(32);
        decoded.IsFrozen.Should().BeTrue();

        AlbumPalette palette = MediaService.ExtractAlbumPalette(decoded);
        palette.PrimaryAccent.A.Should().Be(255);
        palette.SecondaryAccent.A.Should().Be(255);
        palette.PrimaryAccent.Should().NotBe(AlbumPalette.Default.PrimaryAccent);
    }

    [Fact]
    public async Task MediaService_InstantiatesAndDisposesGracefully()
    {
        var service = new MediaService();
        service.CurrentTrack.Should().BeNull();

        await service.DisposeAsync();
    }

    [Fact]
    public async Task MediaService_PlaybackCommands_ExecuteSafelyWithoutThrowing()
    {
        var service = new MediaService();

        Func<Task> actPlayPause = async () => await service.TogglePlayPauseAsync();
        Func<Task> actNext = async () => await service.NextAsync();
        Func<Task> actPrev = async () => await service.PreviousAsync();

        await actPlayPause.Should().NotThrowAsync();
        await actNext.Should().NotThrowAsync();
        await actPrev.Should().NotThrowAsync();

        await service.DisposeAsync();
    }

    [Fact]
    public void MediaTrackInfo_InitializesCorrectly()
    {
        var info = new MediaTrackInfo(
            Title: "Test Title",
            Artist: "Test Artist",
            AlbumTitle: "Test Album",
            IsPlaying: true,
            ThumbnailBytes: [0x1, 0x2, 0x3],
            Position: TimeSpan.FromSeconds(30),
            Duration: TimeSpan.FromMinutes(3));

        info.Title.Should().Be("Test Title");
        info.Artist.Should().Be("Test Artist");
        info.AlbumTitle.Should().Be("Test Album");
        info.IsPlaying.Should().BeTrue();
        info.ThumbnailBytes.Should().Equal(new byte[] { 0x1, 0x2, 0x3 });
        info.Position.Should().Be(TimeSpan.FromSeconds(30));
        info.Duration.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void GlobalInputService_ModifierKeys_IncludeLeftAndRightAlt()
    {
        // VkLeftAlt = 0xA4, VkRightAlt = 0xA5
        const int vkLeftAlt = 0xA4;
        const int vkRightAlt = 0xA5;

        GlobalInputService.IsModifier(vkLeftAlt).Should().BeTrue();
        GlobalInputService.IsModifier(vkRightAlt).Should().BeTrue();
    }

    [Fact]
    public void MediaTrackInfo_GetCurrentPosition_AccuratelyAdvancesWhenPlaying()
    {
        var baselineTime = DateTimeOffset.UtcNow.AddSeconds(-15);
        var info = new MediaTrackInfo(
            Title: "Test",
            Artist: "Artist",
            AlbumTitle: "Album",
            IsPlaying: true,
            Position: TimeSpan.FromSeconds(30),
            Duration: TimeSpan.FromMinutes(3),
            LastUpdatedUtc: baselineTime);

        // At +15s from baseline, position should be 30s + 15s = 45s
        var testNow = baselineTime.AddSeconds(15);
        var current = info.GetCurrentPosition(testNow);

        current.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void MediaTrackInfo_GetCurrentPosition_ClampsToDuration()
    {
        var baselineTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var info = new MediaTrackInfo(
            Title: "Test",
            Artist: "Artist",
            AlbumTitle: "Album",
            IsPlaying: true,
            Position: TimeSpan.FromSeconds(30),
            Duration: TimeSpan.FromMinutes(3),
            LastUpdatedUtc: baselineTime);

        // At +5min from baseline, position would exceed duration of 3 min, should clamp to 3 min
        var testNow = baselineTime.AddMinutes(5);
        var current = info.GetCurrentPosition(testNow);

        current.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void MediaTrackInfo_GetCurrentPosition_ReturnsBaselineWhenPaused()
    {
        var baselineTime = DateTimeOffset.UtcNow.AddSeconds(-30);
        var info = new MediaTrackInfo(
            Title: "Test",
            Artist: "Artist",
            AlbumTitle: "Album",
            IsPlaying: false,
            Position: TimeSpan.FromSeconds(25),
            Duration: TimeSpan.FromMinutes(3),
            LastUpdatedUtc: baselineTime);

        var testNow = baselineTime.AddSeconds(30);
        var current = info.GetCurrentPosition(testNow);

        current.Should().Be(TimeSpan.FromSeconds(25));
    }
}
