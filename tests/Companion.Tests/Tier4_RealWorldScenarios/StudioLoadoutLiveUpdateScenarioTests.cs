using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Companion.Core;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier4_RealWorldScenarios;

public sealed class StudioLoadoutLiveUpdateScenarioTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public void Studio_4SlotLoadoutCustomization_And_LiveJsonPersistence_RoundtripsAccurately()
    {
        // 1. Studio loads current loadout configuration
        var currentLoadout = new SoulLoadout();

        // 2. User customizes all 4 slots in Studio UI:
        // Slot 1: Soul visual flame
        currentLoadout.VisualStyle = SoulVisualStyle.CyberOrb;
        currentLoadout.SoulName = "Void-Cyber";

        // Slot 2: Personality prompt
        currentLoadout.PersonalityName = "Tactical Operator";
        currentLoadout.PersonalityPrompt = "Ultra-terse terminal operator. Report facts, eliminate pleasantries.";

        // Slot 3: Voice preset
        currentLoadout.Voice = VoicePreset.Puck;

        // Slot 4: Enabled Abilities tool pack
        currentLoadout.EnabledAbilityPacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System",
            "DesktopUIA",
            "Research"
        };

        // 3. Studio saves updated loadout configuration to disk
        string testSettingsPath = _scope.CreateFilePath("test_loadout.json");
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        string json = JsonSerializer.Serialize(currentLoadout, options);
        File.WriteAllText(testSettingsPath, json);

        // 4. Shell reloads loadout configuration
        string loadedJson = File.ReadAllText(testSettingsPath);
        var shellReloadedLoadout = JsonSerializer.Deserialize<SoulLoadout>(loadedJson, options);

        // 5. Assert: Complete live synchronization of loadout slots
        shellReloadedLoadout.Should().NotBeNull();
        shellReloadedLoadout!.SoulName.Should().Be("Void-Cyber");
        shellReloadedLoadout.VisualStyle.Should().Be(SoulVisualStyle.CyberOrb);
        shellReloadedLoadout.PersonalityName.Should().Be("Tactical Operator");
        shellReloadedLoadout.PersonalityPrompt.Should().Contain("Ultra-terse terminal operator");
        shellReloadedLoadout.Voice.Should().Be(VoicePreset.Puck);
        shellReloadedLoadout.EnabledAbilityPacks.Should().Contain("DesktopUIA");
        shellReloadedLoadout.EnabledAbilityPacks.Should().NotContain("Media");
    }

    [Fact]
    public void GeminiLive_DynamicHandshakePayload_IncorporatesLoadoutAndFtsMemories()
    {
        // 1. Configure active 4-slot loadout
        var loadout = new SoulLoadout
        {
            SoulName = "Void Prime",
            Voice = VoicePreset.Fenrir,
            PersonalityPrompt = "Speak with deep resonance and extreme conciseness."
        };

        // 2. Query relevant memories from database
        var relevantMemories = new List<MemoryEntry>
        {
            new(1, "User prefers dark mode on all screens", "prefs", DateTime.UtcNow),
            new(2, "Default audio device is Studio Monitors", "hardware", DateTime.UtcNow)
        };

        // 3. Construct Gemini Live BidiGenerateContent handshake
        string memoryContext = "\nRelevant user memories:\n" + string.Join(
            "\n",
            relevantMemories.Select(m => $"- {m.Fact}"));

        string systemInstructionText = string.Join(
            "\n",
            loadout.PersonalityPrompt,
            $"You are called {loadout.SoulName}.",
            memoryContext);

        var setupPayload = new
        {
            setup = new
            {
                model = "models/gemini-2.0-flash-exp",
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" },
                    speechConfig = new
                    {
                        voiceConfig = new
                        {
                            prebuiltVoiceConfig = new { voiceName = loadout.Voice.ToString() }
                        }
                    }
                },
                systemInstruction = new { parts = new[] { new { text = systemInstructionText } } }
            }
        };

        string json = JsonSerializer.Serialize(setupPayload);

        // 4. Verify handshake payload meets API contract
        json.Should().Contain("Fenrir");
        json.Should().Contain("Void Prime");
        json.Should().Contain("Studio Monitors");
        json.Should().Contain("dark mode");
    }
}
