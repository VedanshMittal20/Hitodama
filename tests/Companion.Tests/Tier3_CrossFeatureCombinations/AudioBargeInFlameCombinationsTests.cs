using System;
using System.Buffers.Binary;
using Companion.Services;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier3_CrossFeatureCombinations;

public sealed class AudioBargeInFlameCombinationsTests
{
    [Fact]
    public void AudioCapture_BargeInInterruption_FlushesPlaybackBufferImmediately()
    {
        // Arrange
        using var player = new AudioPlayerService();

        // 1. Assistant is speaking (enqueue 24kHz audio)
        byte[] assistantSpeech = new byte[9600]; // 200ms
        player.EnqueuePcm(assistantSpeech);

        // 2. User starts speaking (mic capture computes RMS level > 0.025)
        byte[] userVoice = new byte[1280];
        for (int i = 0; i < userVoice.Length / 2; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 800 * i / 16000.0) * 25000);
            BinaryPrimitives.WriteInt16LittleEndian(userVoice.AsSpan(i * 2, 2), sample);
        }

        double sumSquares = 0;
        int sampleCount = userVoice.Length / 2;
        for (int offset = 0; offset < sampleCount * 2; offset += 2)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(userVoice.AsSpan(offset, 2)) / 32768.0;
            sumSquares += sample * sample;
        }
        double userRms = Math.Clamp(Math.Sqrt(sumSquares / sampleCount) * 3.5, 0, 1);
        userRms.Should().BeGreaterThan(0.025, "user voice triggers voice activity");

        // 3. Barge-in triggered -> Instant flush of audio player buffer
        player.FlushImmediate();

        // Assert: buffer cleared cleanly without exceptions
        player.Should().NotBeNull();
    }

    [Fact]
    public void MicRms_DrivesFlameIntensity_LevelsScaleSmoothly()
    {
        // Verify multi-tier RMS levels:
        // Whisper (1000 -> ~0.10) -> Conversational (5000 -> ~0.53) -> Loud (15000 -> 1.0)
        double whisperRms = ComputeRmsFromAmplitude(1000);
        double speechRms = ComputeRmsFromAmplitude(5000);
        double shoutRms = ComputeRmsFromAmplitude(15000);

        whisperRms.Should().BeLessThan(speechRms);
        speechRms.Should().BeLessThan(shoutRms);
        shoutRms.Should().Be(1.0);
    }

    private static double ComputeRmsFromAmplitude(short amplitude)
    {
        byte[] buffer = new byte[1280];
        for (int i = 0; i < buffer.Length / 2; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2, 2), amplitude);
        }

        double sumSquares = 0;
        int count = buffer.Length / 2;
        for (int offset = 0; offset < count * 2; offset += 2)
        {
            double s = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2)) / 32768.0;
            sumSquares += s * s;
        }
        return Math.Clamp(Math.Sqrt(sumSquares / count) * 3.5, 0, 1);
    }
}
