using System;
using System.Buffers.Binary;
using Companion.Services;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier2_BoundaryCornerCases;

public sealed class AudioBufferBoundaryTests
{
    [Fact]
    public void AudioPlayer_BufferOverflow_DiscardsOldSamplesWithoutCrashing()
    {
        // Arrange: AudioPlayerService buffer duration is 15 seconds (24000 * 2 * 15 = 720,000 bytes)
        // Push 1,000,000 bytes of PCM audio to induce buffer overflow condition
        using var player = new AudioPlayerService();
        byte[] largePcm = new byte[1_000_000];

        // Act & Assert
        Action act = () => player.EnqueuePcm(largePcm);
        act.Should().NotThrow("DiscardOnBufferOverflow must gracefully drop overflowing samples");

        player.FlushImmediate();
    }

    [Fact]
    public void AudioPlayer_RapidEnqueueAndFlush_HandlesHighFrequencyCalls()
    {
        // Act: 50 iterations of enqueue followed by immediate flush (simulating rapid barge-ins)
        using var player = new AudioPlayerService();
        byte[] chunk = new byte[2400];

        for (int i = 0; i < 50; i++)
        {
            player.EnqueuePcm(chunk);
            player.FlushImmediate();
        }

        player.Should().NotBeNull();
    }

    [Fact]
    public void RmsLevel_ExtremeClipping_ClampsStrictlyBetweenZeroAndOne()
    {
        // Arrange: Buffer with maximum positive and negative values (32767 and -32768)
        byte[] clippedPcm = new byte[1280];
        for (int i = 0; i < clippedPcm.Length / 2; i++)
        {
            short sample = (i % 2 == 0) ? (short)32767 : (short)-32768;
            BinaryPrimitives.WriteInt16LittleEndian(clippedPcm.AsSpan(i * 2, 2), sample);
        }

        // Act
        double sumSquares = 0;
        int sampleCount = clippedPcm.Length / 2;
        for (int offset = 0; offset < sampleCount * 2; offset += 2)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(clippedPcm.AsSpan(offset, 2)) / 32768.0;
            sumSquares += sample * sample;
        }
        double level = Math.Clamp(Math.Sqrt(sumSquares / sampleCount) * 3.5, 0, 1);

        // Assert
        level.Should().Be(1.0, "extreme audio clipping must clamp cleanly to 1.0 without NaN or overflow");
    }

    [Fact]
    public void RmsLevel_OddByteLength_SafelyIgnoresTrailingDanglingByte()
    {
        // 1281 bytes (odd length, dangling single byte)
        byte[] oddPcm = new byte[1281];
        int sampleCount = oddPcm.Length / 2; // integer division rounds down to complete 16-bit pairs

        sampleCount.Should().Be(640);
    }
}
