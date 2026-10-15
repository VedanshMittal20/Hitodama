using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Companion.Core;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier2_BoundaryCornerCases;

public sealed class DatabaseBoundaryTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly DatabaseService _sut;

    public DatabaseBoundaryTests()
    {
        _sut = new DatabaseService(_scope.CreateFilePath("db_boundary_test.db"));
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"\"\"\"")]
    [InlineData("*")]
    [InlineData("AND OR NOT NEAR")]
    [InlineData("(foo OR bar)")]
    [InlineData("tags:test")]
    [InlineData("🔥✨🚀 Unicode Emojis")]
    [InlineData("C# 12 & .NET 8 WPF")]
    public void FTS5_AdversarialSpecialCharacters_AreSanitizedAndDoNotThrow(string maliciousQuery)
    {
        // Arrange
        _sut.AddMemory("Hitodama desktop companion with C# 12 & .NET 8 WPF", "tags:test");

        // Act & Assert
        Action act = () => _sut.SearchMemoriesFts(maliciousQuery);
        act.Should().NotThrow("FTS5 query sanitization must escape double quotes and special tokens cleanly");
    }

    [Fact]
    public void Memories_HugePayload_PersistsAndSearchesSuccessfully()
    {
        // Arrange: 1MB memory fact
        string hugeFact = "PrefixMatch " + new string('Z', 1024 * 1024) + " SuffixMatch";

        // Act
        long id = _sut.AddMemory(hugeFact, "large_payload");
        var results = _sut.SearchMemoriesFts("PrefixMatch");

        // Assert
        id.Should().BeGreaterThan(0);
        results.Should().HaveCount(1);
        results[0].Fact.Should().StartWith("PrefixMatch");
    }

    [Fact]
    public void DatabaseService_ConcurrentReadersAndWriters_ExecuteInWalModeWithoutLockingErrors()
    {
        // Act: 10 parallel threads writing and reading simultaneously
        Parallel.For(0, 10, i =>
        {
            _sut.AddMemory($"Concurrent memory from worker {i}", $"thread-{i}");
            _sut.AddTodo($"Concurrent task {i}", "12:00");
            _sut.IncrementStat("tools_executed", 1);

            var memories = _sut.GetAllMemories();
            memories.Should().NotBeNull();
            var todos = _sut.GetTodos();
            todos.Should().NotBeNull();
        });

        // Assert
        _sut.GetAllMemories().Count.Should().Be(10);
        _sut.GetTodos().Count.Should().Be(10);
        _sut.GetTodayStats().ToolsExecuted.Should().Be(10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void SearchMemoriesFts_InvalidLimits_ThrowArgumentOutOfRangeException(int invalidLimit)
    {
        Action act = () => _sut.SearchMemoriesFts("test", invalidLimit);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Clipboard_MaxEntriesBoundary_ClampsCorrectlyToOne()
    {
        // Act: maxEntries = 1
        _sut.AddClipboardText("Item 1", maxEntries: 1);
        _sut.AddClipboardText("Item 2", maxEntries: 1);

        // Assert: only Item 2 remains
        var entries = _sut.GetClipboardEntries();
        entries.Should().HaveCount(1);
        entries[0].TextOrImagePath.Should().Be("Item 2");
    }

    [Fact]
    public void IncrementStat_NegativeAmount_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => _sut.IncrementStat("dictated_words", -5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
