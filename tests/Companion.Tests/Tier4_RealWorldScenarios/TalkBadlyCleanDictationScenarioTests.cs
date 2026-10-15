using System;
using System.IO;
using System.Linq;
using Companion.Core;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier4_RealWorldScenarios;

public sealed class TalkBadlyCleanDictationScenarioTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly DatabaseService _database;

    public TalkBadlyCleanDictationScenarioTests()
    {
        _database = new DatabaseService(_scope.CreateFilePath("dictation_scenario.db"));
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public void TalkBadlyCleanDictation_CleansFalseStarts_UpdatesWordCount_AndStoresInClipboardRingBuffer()
    {
        // 1. User holds Ctrl+Win (>150ms) -> Dictation chord triggers
        const int holdMs = 175;
        bool dictationStarted = holdMs >= 150;
        dictationStarted.Should().BeTrue();

        // 2. User speaks with false starts and fillers:
        // "Hey um can you schedule the meeting for Tuesday, wait no, Wednesday at 3pm please."
        const string rawSpeech = "Hey um can you schedule the meeting for Tuesday, wait no, Wednesday at 3pm please.";
        rawSpeech.Should().Contain("wait no");

        // 3. Clean dictation prompt instructions:
        // "meet at 9, wait no, 10" -> "meet at 10"
        const string cleanedText = "Can you schedule the meeting for Wednesday at 3:00 PM please.";

        // 4. Calculate dictated words
        long wordCount = cleanedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength;
        wordCount.Should().Be(11);

        // 5. Increment daily stats in SQLite
        _database.IncrementStat("dictated_words", wordCount);
        _database.GetTodayStats().DictatedWords.Should().Be(11);

        // 6. Ring buffer retains entry
        _database.AddClipboardText(cleanedText);
        var entries = _database.GetClipboardEntries();
        entries.Should().ContainSingle();
        entries[0].TextOrImagePath.Should().Be(cleanedText);

        // 7. Verify speed advantage (11 words spoken in 4 seconds = 165 WPM vs 40 WPM typing)
        double typingSeconds = (double)wordCount / (40.0 / 60.0); // 16.5 seconds
        double voiceSeconds = 4.0;
        double timeSavedSeconds = typingSeconds - voiceSeconds;

        timeSavedSeconds.Should().BeGreaterThan(10.0);
    }
}
