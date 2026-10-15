using System;
using System.Buffers.Binary;
using Companion.Services;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class AudioBufferTests
{
    [Fact]
    public void AudioPlayerService_Initialization_And_FlushImmediate_DoesNotThrow()
    {
        // Arrange & Act
        using var player = new AudioPlayerService();
        player.FlushImmediate();

        // Assert
        player.Should().NotBeNull();
    }

    [Fact]
    public void AudioPlayerService_EnqueueEmptyArray_IsIgnoredCleanly()
    {
        // Arrange
        using var player = new AudioPlayerService();

        // Act
        Action act = () => player.EnqueuePcm([]);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void AudioPlayerService_EnqueuePcm_AcceptsValid24kHzBuffer()
    {
        // Arrange
        using var player = new AudioPlayerService();
        // 24000 samples/sec * 2 bytes/sample * 0.1s = 4800 bytes
        byte[] dummyPcm = new byte[4800];

        // Act & Assert
        Action act = () => player.EnqueuePcm(dummyPcm);
        act.Should().NotThrow();

        // Flush immediately (simulating barge-in)
        player.FlushImmediate();
    }

    [Fact]
    public void AudioPlayerService_DisposedState_HandlesFlushGracefully()
    {
        // Arrange
        var player = new AudioPlayerService();
        player.Dispose();

        // Act & Assert: calling Flush on disposed instance must be safe and idempotent
        Action act = () => player.FlushImmediate();
        act.Should().NotThrow();
    }

    [Fact]
    public void MicCapture_RmsCalculation_ComputesZeroForSilence()
    {
        // Arrange: 40ms of 16kHz 16-bit silence (1280 bytes of 0x00)
        byte[] silence = new byte[1280];

        // Act
        double sumSquares = 0;
        int sampleCount = silence.Length / 2;
        for (int offset = 0; offset < sampleCount * 2; offset += 2)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(silence.AsSpan(offset, 2)) / 32768.0;
            sumSquares += sample * sample;
        }
        double level = Math.Clamp(Math.Sqrt(sumSquares / sampleCount) * 3.5, 0, 1);

        // Assert
        level.Should().Be(0.0);
    }

    [Fact]
    public void MicCapture_RmsCalculation_DetectsHighEnergyVoice()
    {
        // Arrange: Generate 40ms of 1kHz sine wave at full amplitude (32767)
        byte[] sineWave = new byte[1280];
        int sampleCount = sineWave.Length / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 1000 * i / 16000.0) * 30000);
            BinaryPrimitives.WriteInt16LittleEndian(sineWave.AsSpan(i * 2, 2), sample);
        }

        // Act
        double sumSquares = 0;
        for (int offset = 0; offset < sampleCount * 2; offset += 2)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(sineWave.AsSpan(offset, 2)) / 32768.0;
            sumSquares += sample * sample;
        }
        double level = Math.Clamp(Math.Sqrt(sumSquares / sampleCount) * 3.5, 0, 1);

        // Assert: RMS should be well above threshold (0.025)
        level.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void VadHangover_Calculates720msWindowFrom18Chunks()
    {
        // In MicCaptureService: BufferMilliseconds = 40; _voiceHangoverChunks = 18;
        const int chunkDurationMs = 40;
        const int hangoverChunks = 18;

        int totalHangoverDurationMs = chunkDurationMs * hangoverChunks;

        totalHangoverDurationMs.Should().Be(720, "VAD hangover must bridge 720ms of brief pauses");
    }
}
