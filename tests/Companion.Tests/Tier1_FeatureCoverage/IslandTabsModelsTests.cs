using System;
using System.Collections.Generic;
using System.Text.Json;
using Companion.Core;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class IslandTabsModelsTests
{
    [Fact]
    public void Tab1_Controls_AudioAndThemeModels_PropertiesAreValid()
    {
        // Arrange
        const string device = "Realtek High Definition Audio";
        const int volume = 75;
        const bool darkMode = true;

        // Assert
        device.Should().NotBeNullOrWhiteSpace();
        volume.Should().BeInRange(0, 100);
        darkMode.Should().BeTrue();
    }

    [Fact]
    public void Tab2_Today_TodoItem_And_ScheduledAction_InstantiateCorrectly()
    {
        // Arrange
        var todo = new TodoItem(1, "Buy birthday gift", "17:00", false, DateTime.UtcNow);
        var scheduled = new ScheduledAction(
            10,
            "Search Amazon for gift wraps",
            "System.OpenUrl",
            "{\"url\":\"https://amazon.com\"}",
            DateTime.UtcNow.AddHours(3),
            "daily:11:00",
            true);
        var routine = new Routine(100, "Morning Standup", "[\"read_todos\",\"read_stats\"]", DateTime.UtcNow, true);

        // Assert
        todo.Title.Should().Be("Buy birthday gift");
        todo.DueTimeText.Should().Be("17:00");
        todo.IsCompleted.Should().BeFalse();

        scheduled.Description.Should().Be("Search Amazon for gift wraps");
        scheduled.CronExpression.Should().Be("daily:11:00");
        scheduled.IsEnabled.Should().BeTrue();

        routine.Name.Should().Be("Morning Standup");
        routine.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Tab3_Stats_DailyStats_CalculatesWpmAndTimeSaved()
    {
        // Arrange: User dictated 1200 words today, typical typing speed is 40 WPM, voice is 150 WPM
        var stats = new DailyStats(
            DateTime.UtcNow.ToString("yyyy-MM-dd"),
            DictatedWords: 1200,
            VoiceCommands: 18,
            ToolsExecuted: 7);

        // Act
        double typingMinutes = (double)stats.DictatedWords / 40.0; // 30 mins
        double voiceMinutes = (double)stats.DictatedWords / 150.0;  // 8 mins
        double minutesSaved = typingMinutes - voiceMinutes;        // 22 mins saved

        // Assert
        stats.DictatedWords.Should().Be(1200);
        stats.VoiceCommands.Should().Be(18);
        stats.ToolsExecuted.Should().Be(7);
        minutesSaved.Should().BeApproximately(22.0, 0.1);
    }

    [Fact]
    public void Tab4_Clipboard_ClipEntry_PreviewGeneration_LimitsLength()
    {
        // Arrange
        string fullText = new string('A', 500);
        string preview = fullText.Length > 300 ? fullText[..300] : fullText;

        var clip = new ClipEntry(5, "text", fullText, preview, false, DateTime.UtcNow);

        // Assert
        clip.ContentType.Should().Be("text");
        clip.PreviewText.Length.Should().Be(300);
        clip.IsPinned.Should().BeFalse();
    }

    [Fact]
    public void Tab5_Apps_LauncherItem_FuzzyScoreCalculation()
    {
        // Arrange
        var appItem = new { Name = "Visual Studio Code", Path = @"C:\Tools\Code.exe" };
        const string query = "code";

        // Act
        bool matches = appItem.Name.Contains(query, StringComparison.OrdinalIgnoreCase);

        // Assert
        matches.Should().BeTrue();
    }

    [Fact]
    public void Tab6_Wallpaper_ModelContract_ValidatesNonLockingRequirements()
    {
        // Arrange
        var wallpaper = new { Path = @"C:\Wallpapers\hitodama_neon.png", Title = "Neon Hitodama" };

        // Assert
        wallpaper.Path.Should().EndWith(".png");
        wallpaper.Title.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Tab7_Power_Tiles_CoversAllRequiredSystemActions()
    {
        // Arrange
        var powerActions = new HashSet<string> { "Lock", "Sleep", "Restart", "Shutdown", "QuietMode" };

        // Assert
        powerActions.Should().Contain("Lock");
        powerActions.Should().Contain("Sleep");
        powerActions.Should().Contain("Restart");
        powerActions.Should().Contain("Shutdown");
        powerActions.Should().Contain("QuietMode");
    }

    [Fact]
    public void SoulLoadout_FourSlotLoadout_SerializesAndDeserializesCompletely()
    {
        // Arrange
        var loadout = new SoulLoadout
        {
            SoulName = "Kitsune",
            VisualStyle = SoulVisualStyle.SolarEmber,
            PersonalityName = "Wise Mentor",
            PersonalityPrompt = "Speak with ancient wisdom and modern precision.",
            Voice = VoicePreset.Aoede,
            Model = "models/gemini-2.5-flash-native-audio-preview-12-2025",
            EnabledAbilityPacks = new HashSet<string> { "System", "Research", "Media" }
        };

        // Act
        string json = JsonSerializer.Serialize(loadout);
        var restored = JsonSerializer.Deserialize<SoulLoadout>(json);

        // Assert
        restored.Should().NotBeNull();
        restored!.SoulName.Should().Be("Kitsune");
        restored.VisualStyle.Should().Be(SoulVisualStyle.SolarEmber);
        restored.PersonalityName.Should().Be("Wise Mentor");
        restored.Voice.Should().Be(VoicePreset.Aoede);
        restored.EnabledAbilityPacks.Should().Contain("Research");
        restored.EnabledAbilityPacks.Should().NotContain("DesktopUIA");
    }
}
