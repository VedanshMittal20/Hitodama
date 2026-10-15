using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Companion.Core;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier4_RealWorldScenarios;

[Collection("SecretStoreCollection")]
public sealed class ColdStartOnboardingScenarioTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly string? _originalKey;

    public ColdStartOnboardingScenarioTests()
    {
        _originalKey = SecretStore.LoadApiKey();
    }

    public void Dispose()
    {
        if (_originalKey is not null)
        {
            SecretStore.SaveApiKey(_originalKey);
        }
        else
        {
            SecretStore.DeleteApiKey();
        }

        _scope.Dispose();
    }

    [Fact]
    public void ColdStart_CompleteOnboardingWorkflow_InitializesSystemReadyState()
    {
        // 1. Initial Launch: SecretStore has no API key
        SecretStore.DeleteApiKey();
        SecretStore.LoadApiKey().Should().BeNull();

        // 2. Step 1 (Make): User gets key from Google AI Studio
        const string mockUserKey = "AIzaSy_RealWorld_ValidGeminiKey_123456789";

        // 3. Step 2 (Paste): User pastes key in Studio onboarding wizard
        SecretStore.SaveApiKey(mockUserKey);
        SecretStore.LoadApiKey().Should().Be(mockUserKey);

        // 4. Step 3 (Talk): Default Soul Loadout is initialized with 4 slots
        var loadout = new SoulLoadout();
        loadout.SoulName.Should().Be("Void");
        loadout.VisualStyle.Should().Be(SoulVisualStyle.VoidFlame);
        loadout.Voice.Should().Be(VoicePreset.Charon);
        loadout.PersonalityName.Should().Be("True Chad");
        loadout.EnabledAbilityPacks.Should().Contain("System");

        // 5. Database Service initialized with WAL mode and initial seed memory
        string dbPath = _scope.CreateFilePath("onboarding.db");
        var db = new DatabaseService(dbPath);

        long memoryId = db.AddMemory("User completed onboarding on Windows desktop", "system,onboarding");
        memoryId.Should().BeGreaterThan(0);

        var searchResult = db.SearchMemoriesFts("onboarding");
        searchResult.Should().ContainSingle();
        searchResult[0].Fact.Should().Contain("onboarding");

        // 6. Verify DailyStats initialized
        var stats = db.GetTodayStats();
        stats.DictatedWords.Should().Be(0);
        stats.ToolsExecuted.Should().Be(0);
    }
}
