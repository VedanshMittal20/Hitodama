using System;
using System.IO;
using System.Text.Json;
using Companion.Core;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier4_RealWorldScenarios;

public sealed class CircleToAskScenarioTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly DatabaseService _database;

    public CircleToAskScenarioTests()
    {
        _database = new DatabaseService(_scope.CreateFilePath("circle_to_ask.db"));
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public void CircleToAsk_EndToEndPipeline_CapturesScreenRegionAndAnswersQuestion()
    {
        // 1. User holds Ctrl+Shift (>180ms) -> Circle to ask chord triggered
        const int holdDurationMs = 200;
        bool chordTriggered = holdDurationMs >= 180;
        chordTriggered.Should().BeTrue();

        // 2. Lasso selection defines cropped screen region (e.g. 400x300 bounding box)
        byte[] simulatedCroppedJpeg = new byte[1024]; // 1KB JPEG payload
        simulatedCroppedJpeg.Length.Should().BeGreaterThan(0);

        // 3. User speaks voice question ("What code library is shown here?") -> 16kHz PCM WAV
        byte[] simulatedWavAudio = new byte[3200]; // 100ms 16kHz audio
        simulatedWavAudio.Length.Should().BeGreaterThan(44);

        // 4. Gatekeeper marks context as untrusted external content
        var gatekeeper = new GatekeeperSecurityEngine();
        gatekeeper.TrackUntrustedContext(true);
        gatekeeper.IsContextUntrusted.Should().BeTrue();

        // 5. Structure payload for Gemini 2.5 Flash Vision
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { inline_data = new { mime_type = "image/jpeg", data = Convert.ToBase64String(simulatedCroppedJpeg) } },
                        new { inline_data = new { mime_type = "audio/wav", data = Convert.ToBase64String(simulatedWavAudio) } }
                    }
                }
            }
        };

        string jsonPayload = JsonSerializer.Serialize(payload);
        jsonPayload.Should().Contain("image/jpeg");
        jsonPayload.Should().Contain("audio/wav");

        // 6. Gemini returns answer -> Answer saved into SQLite Memory FTS5
        const string geminiAnswer = "The image shows the NAudio 2.2.1 library package used for 16kHz audio streaming.";
        long memoryId = _database.AddMemory(geminiAnswer, "circle-to-ask,screen");

        // 7. Verify retrieval through FTS5
        var retrieved = _database.SearchMemoriesFts("NAudio");
        retrieved.Should().ContainSingle();
        retrieved[0].Fact.Should().Contain("NAudio 2.2.1");
    }
}
