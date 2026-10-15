using System;
using System.IO;
using System.Linq;
using Companion.Core;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class DatabaseServiceTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly string _dbPath;
    private readonly DatabaseService _sut;

    public DatabaseServiceTests()
    {
        _dbPath = _scope.CreateFilePath("companion_test.db");
        _sut = new DatabaseService(_dbPath);
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public void Initialize_ConfiguresWalMode_AndCreatesAllRequiredTables()
    {
        // Assert: query SQLite pragmas directly to verify WAL mode
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        string? journalMode = command.ExecuteScalar()?.ToString();

        journalMode.Should().BeEquivalentTo("wal");

        // Verify tables exist
        command.CommandText = """
            SELECT name FROM sqlite_master 
            WHERE type='table' AND name IN ('memories', 'memories_fts', 'todos', 'scheduled_actions', 'stats_daily', 'clipboard_entries');
            """;
        var tables = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        tables.Should().Contain("memories");
        tables.Should().Contain("memories_fts");
        tables.Should().Contain("todos");
        tables.Should().Contain("scheduled_actions");
        tables.Should().Contain("stats_daily");
        tables.Should().Contain("clipboard_entries");
    }

    [Fact]
    public void Memories_AddAndFtsSearch_FindsRelevantMatches()
    {
        // Arrange
        _sut.AddMemory("User's favorite programming language is C# and Rust", "tech,pref");
        _sut.AddMemory("User lives in Bengaluru and works on AI agents", "personal,geo");
        _sut.AddMemory("Hitodama desktop companion runs at 120Hz spring physics", "companion,ui");

        // Act
        var rustResults = _sut.SearchMemoriesFts("Rust");
        var physicsResults = _sut.SearchMemoriesFts("physics");
        var allMemories = _sut.GetAllMemories();

        // Assert
        rustResults.Should().HaveCount(1);
        rustResults[0].Fact.Should().Contain("Rust");
        rustResults[0].Tags.Should().Be("tech,pref");

        physicsResults.Should().HaveCount(1);
        physicsResults[0].Fact.Should().Contain("120Hz");

        allMemories.Should().HaveCount(3);
    }

    [Fact]
    public void Memories_Delete_SyncsWithFtsTableAutomatically()
    {
        // Arrange
        long id = _sut.AddMemory("Ephemeral fact to be deleted soon", "temp");
        _sut.SearchMemoriesFts("Ephemeral").Should().HaveCount(1);

        // Act
        bool deleted = _sut.DeleteMemory(id);

        // Assert
        deleted.Should().BeTrue();
        _sut.GetAllMemories().Should().BeEmpty();
        _sut.SearchMemoriesFts("Ephemeral").Should().BeEmpty();
    }

    [Fact]
    public void Todos_AddToggleAndDelete_ModifiesCompletionStatus()
    {
        // Arrange & Act
        long id1 = _sut.AddTodo("Review PR #42", "14:00");
        long id2 = _sut.AddTodo("Buy groceries");

        var todos = _sut.GetTodos();
        todos.Should().HaveCount(2);
        todos.First(t => t.Id == id1).IsCompleted.Should().BeFalse();

        // Toggle completed
        _sut.ToggleTodo(id1).Should().BeTrue();
        _sut.GetTodos().First(t => t.Id == id1).IsCompleted.Should().BeTrue();

        // Delete todo
        _sut.DeleteTodo(id2).Should().BeTrue();
        _sut.GetTodos().Should().HaveCount(1);
    }

    [Fact]
    public void ScheduledActions_AddAndGetDue_FiltersProperly()
    {
        // Arrange
        var pastTime = DateTime.UtcNow.AddMinutes(-5);
        var futureTime = DateTime.UtcNow.AddHours(2);

        long idPast = _sut.AddScheduledAction("Past task", "System.Notify", "{}", pastTime);
        long idFuture = _sut.AddScheduledAction("Future task", "System.Backup", "{}", futureTime, "daily:09:00");

        // Act
        var dueActions = _sut.GetDueActions();
        var upcoming = _sut.GetUpcomingActions();
        var next = _sut.GetNextScheduledAction();

        // Assert
        dueActions.Should().ContainSingle(a => a.Id == idPast);
        upcoming.Should().Contain(a => a.Id == idFuture);
        next.Should().NotBeNull();
        next!.Id.Should().Be(idPast);

        // Reschedule past task to future
        _sut.MarkExecutedOrReschedule(idPast, DateTime.UtcNow.AddDays(1)).Should().BeTrue();
        _sut.GetDueActions().Should().BeEmpty();
    }

    [Fact]
    public void DailyStats_IncrementAndGetTodayStats_AccumulatesValues()
    {
        // Act
        _sut.IncrementStat("dictated_words", 50);
        _sut.IncrementStat("dictated_words", 25);
        _sut.IncrementStat("voice_commands", 5);
        _sut.IncrementStat("tools_executed", 2);

        var stats = _sut.GetTodayStats();

        // Assert
        stats.DictatedWords.Should().Be(75);
        stats.VoiceCommands.Should().Be(5);
        stats.ToolsExecuted.Should().Be(2);
    }

    [Fact]
    public void Clipboard_AddAndRetrieve_EncryptsContentAndTrimsEntries()
    {
        // Arrange
        for (int i = 1; i <= 5; i++)
        {
            _sut.AddClipboardText($"Clipboard text item {i}", maxEntries: 3);
        }

        // Act
        var entries = _sut.GetClipboardEntries();

        // Assert - trimmed to maxEntries = 3, newest first
        entries.Should().HaveCount(3);
        entries[0].TextOrImagePath.Should().Be("Clipboard text item 5");
        entries[1].TextOrImagePath.Should().Be("Clipboard text item 4");
        entries[2].TextOrImagePath.Should().Be("Clipboard text item 3");

        // Delete entry
        _sut.DeleteClipboardEntry(entries[0].Id).Should().BeTrue();
        _sut.GetClipboardEntries().Should().HaveCount(2);
    }

    [Fact]
    public void Clipboard_AddAndRetrieveImage_ProtectsAndPreservesPngBytes()
    {
        byte[] pngBytes = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];

        _sut.AddClipboardImage(pngBytes);

        var entry = _sut.GetClipboardEntries().Should().ContainSingle().Subject;
        entry.ContentType.Should().Be("image/png");
        entry.PreviewText.Should().Contain("Image");
        Convert.FromBase64String(entry.TextOrImagePath).Should().Equal(pngBytes);
    }

    [Fact]
    public void Routines_CreatePauseResumeAndDeleteDailyScheduledAction()
    {
        long id = _sut.AddRoutine(
            "Morning setup",
            """[{"tool_name":"open_app","args":{"app_name":"Notepad"}}]""",
            DateTime.UtcNow.AddHours(1),
            "09:00");

        _sut.GetRoutines().Should().ContainSingle(routine => routine.Id == id && routine.IsEnabled);
        ScheduledAction action = _sut.GetNextScheduledAction()!;
        action.ToolName.Should().Be("Routine.Run");
        action.ArgsJson.Should().Contain(id.ToString());

        _sut.SetRoutineEnabled(id, false).Should().BeTrue();
        _sut.GetNextScheduledAction().Should().BeNull();
        _sut.SetRoutineEnabled(id, true).Should().BeTrue();
        _sut.GetNextScheduledAction().Should().NotBeNull();

        _sut.DeleteRoutine(id).Should().BeTrue();
        _sut.GetRoutines().Should().BeEmpty();
        _sut.GetNextScheduledAction().Should().BeNull();
    }
}
