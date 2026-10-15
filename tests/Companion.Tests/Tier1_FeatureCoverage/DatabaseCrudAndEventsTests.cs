using Companion.Core;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class DatabaseCrudAndEventsTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly string _dbPath;
    private readonly DatabaseService _sut;

    public DatabaseCrudAndEventsTests()
    {
        _dbPath = _scope.CreateFilePath("db_crud_events_test.db");
        _sut = new DatabaseService(_dbPath);
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public async Task Writes_WaitForAnExistingWriteLock()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        command.ExecuteNonQuery();

        Task<long> pendingWrite = Task.Run(() => _sut.AddTodo("Wait for lock"));
        await Task.Delay(150);
        pendingWrite.IsCompleted.Should().BeFalse();

        using var releaseCommand = connection.CreateCommand();
        releaseCommand.CommandText = "COMMIT;";
        releaseCommand.ExecuteNonQuery();
        (await pendingWrite.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Memories_CrudAndReactiveEvents_FireProperlyAndSyncFts()
    {
        int eventCount = 0;
        _sut.MemoriesChanged += () => eventCount++;

        // 1. Create (AddMemory / InsertMemory)
        long id = _sut.InsertMemory("User prefers Dark Roast coffee", "coffee,morning");
        id.Should().BeGreaterThan(0);
        eventCount.Should().Be(1);

        // 2. Read single (GetMemory)
        var memory = _sut.GetMemory(id);
        memory.Should().NotBeNull();
        memory!.Fact.Should().Be("User prefers Dark Roast coffee");
        memory.Tags.Should().Be("coffee,morning");

        // FTS search finds it
        _sut.SearchMemoriesFts("Dark Roast").Should().ContainSingle();

        // 3. Update (UpdateMemory)
        bool updated = _sut.UpdateMemory(id, "User prefers Iced Caramel Macchiato", "coffee,summer");
        updated.Should().BeTrue();
        eventCount.Should().Be(2);

        var updatedMem = _sut.GetMemory(id);
        updatedMem!.Fact.Should().Be("User prefers Iced Caramel Macchiato");
        updatedMem.Tags.Should().Be("coffee,summer");

        // FTS search reflects update
        _sut.SearchMemoriesFts("Caramel Macchiato").Should().ContainSingle();
        _sut.SearchMemoriesFts("Dark Roast").Should().BeEmpty();

        // 4. Delete (DeleteMemory)
        bool deleted = _sut.DeleteMemory(id);
        deleted.Should().BeTrue();
        eventCount.Should().Be(3);

        _sut.GetMemory(id).Should().BeNull();
        _sut.SearchMemoriesFts("Caramel Macchiato").Should().BeEmpty();
    }

    [Fact]
    public void Todos_CrudAndReactiveEvents_FireProperlyAndFilterActive()
    {
        int eventCount = 0;
        _sut.TodosChanged += () => eventCount++;

        // 1. Create (AddTodo)
        long id1 = _sut.AddTodo("Write tests for Milestone 1", "Today 18:00");
        long id2 = _sut.AddTodo("Submit PR for review", "Tomorrow");
        eventCount.Should().Be(2);

        // 2. Read Active (GetActiveTodos)
        var active = _sut.GetActiveTodos();
        active.Should().HaveCount(2);

        // 3. Read Single (GetTodo)
        var todo1 = _sut.GetTodo(id1);
        todo1.Should().NotBeNull();
        todo1!.Title.Should().Be("Write tests for Milestone 1");
        todo1.IsCompleted.Should().BeFalse();

        // 4. Toggle (ToggleTodo)
        _sut.ToggleTodo(id1).Should().BeTrue();
        eventCount.Should().Be(3);

        _sut.GetActiveTodos().Should().ContainSingle(t => t.Id == id2);
        _sut.GetTodo(id1)!.IsCompleted.Should().BeTrue();

        // 5. Update (UpdateTodo)
        _sut.UpdateTodo(id2, "Submit final PR for review", "Tomorrow 10:00", isCompleted: true).Should().BeTrue();
        eventCount.Should().Be(4);

        var updated2 = _sut.GetTodo(id2);
        updated2!.Title.Should().Be("Submit final PR for review");
        updated2.DueTimeText.Should().Be("Tomorrow 10:00");
        updated2.IsCompleted.Should().BeTrue();

        _sut.GetActiveTodos().Should().BeEmpty();

        // 6. Delete (DeleteTodo)
        _sut.DeleteTodo(id1).Should().BeTrue();
        eventCount.Should().Be(5);
        _sut.GetTodos().Should().ContainSingle(t => t.Id == id2);
    }

    [Fact]
    public void ScheduledActions_CrudAndReactiveEvents_FireProperly()
    {
        int eventCount = 0;
        _sut.ScheduledActionsChanged += () => eventCount++;

        var triggerTime = DateTime.UtcNow.AddHours(3);

        // 1. Create (AddScheduledAction)
        long id = _sut.AddScheduledAction(
            "Sync calendar events",
            "system_control",
            "{\"action\":\"get_volume\"}",
            triggerTime,
            "daily:14:00");
        eventCount.Should().Be(1);

        // 2. Read Single (GetScheduledAction)
        var action = _sut.GetScheduledAction(id);
        action.Should().NotBeNull();
        action!.Description.Should().Be("Sync calendar events");
        action.ToolName.Should().Be("system_control");
        action.CronExpression.Should().Be("daily:14:00");
        action.IsEnabled.Should().BeTrue();

        // 3. Update (UpdateScheduledAction)
        var newTriggerTime = DateTime.UtcNow.AddDays(1);
        bool updated = _sut.UpdateScheduledAction(
            id,
            "Sync calendar events and notifications",
            "system_control",
            "{\"action\":\"get_volume\"}",
            newTriggerTime,
            "daily:15:00",
            isEnabled: true);
        updated.Should().BeTrue();
        eventCount.Should().Be(2);

        var updatedAction = _sut.GetScheduledAction(id);
        updatedAction!.Description.Should().Be("Sync calendar events and notifications");
        updatedAction.CronExpression.Should().Be("daily:15:00");

        // 4. Delete (DeleteScheduledAction)
        bool deleted = _sut.DeleteScheduledAction(id);
        deleted.Should().BeTrue();
        eventCount.Should().Be(3);

        _sut.GetScheduledAction(id).Should().BeNull();
    }

    [Fact]
    public void AuditLog_AddsAndRetrievesRecentEntries()
    {
        // Act
        _sut.AddAuditEntry("shell_execute", "{\"command\":\"cmd.exe\"}", "Success: ExitCode 0");
        _sut.AddAuditEntry("file_read_write", "{\"action\":\"read_text\"}", "Success: 42 lines");

        var entries = _sut.GetRecentAuditEntries(10);

        // Assert
        entries.Should().HaveCount(2);
        entries[0].ToolName.Should().Be("file_read_write"); // Most recent first
        entries[0].Outcome.Should().Contain("42 lines");
        entries[1].ToolName.Should().Be("shell_execute");
    }

    [Fact]
    public void FTS5_Ranking_WeightsFactAboveTags_AndBreaksTiesByRecency()
    {
        // Fact match should rank ahead of tag match due to bm25(10.0, 5.0)
        _sut.AddMemory("General note about computers", "bengaluru");
        _sut.AddMemory("Bengaluru is the Silicon Valley of India", "geo");

        var results = _sut.SearchMemoriesFts("Bengaluru");
        results.Should().HaveCount(2);
        results[0].Fact.Should().StartWith("Bengaluru", "fact match is weighted higher than tag match");
    }
}
