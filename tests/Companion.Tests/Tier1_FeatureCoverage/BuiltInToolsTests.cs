using System.IO;
using System.Text.Json;
using Companion.Core;
using Companion.Services.Tools;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class BuiltInToolsTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public async Task SystemControlTool_AssessRiskAndExecuteVolume_Succeeds()
    {
        // Arrange
        var tool = new SystemControlTool();

        using var getVolArgs = JsonDocument.Parse("{\"action\": \"get_volume\"}");
        using var shutdownArgs = JsonDocument.Parse("{\"action\": \"shutdown\"}");

        // Act 1: Assess dynamic risk
        var lowRisk = tool.AssessRisk(getVolArgs.RootElement, ToolExecutionContext.Trusted());
        var highRisk = tool.AssessRisk(shutdownArgs.RootElement, ToolExecutionContext.Trusted());

        // Assert 1
        lowRisk.Should().Be(ToolRiskLevel.Low);
        highRisk.Should().Be(ToolRiskLevel.High);

        // Act 2: Execute get_volume
        var result = await tool.ExecuteAsync(getVolArgs.RootElement, ToolExecutionContext.Trusted());

        // Assert 2
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("Volume:");
    }

    [Fact]
    public async Task ShellExecuteTool_EchoCommand_ReturnsCapturedStdout()
    {
        // Arrange
        var tool = new ShellExecuteTool();
        tool.Definition.RiskLevel.Should().Be(ToolRiskLevel.High);

        using var args = JsonDocument.Parse("""
            {
              "command": "cmd.exe",
              "arguments": "/c echo HitodamaRockSolid"
            }
            """);

        // Act
        var result = await tool.ExecuteAsync(args.RootElement, ToolExecutionContext.Trusted());

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("HitodamaRockSolid");
        result.Output.Should().Contain("ExitCode: 0");
    }

    [Fact]
    public async Task FileReadWriteTool_WriteReadExistsAndDirectoryList_RoundtripsSuccessfully()
    {
        // Arrange
        var tool = new FileReadWriteTool();
        string testFilePath = _scope.CreateFilePath("hitodama_notes.txt");
        const string testContent = "Hitodama desktop companion file tool test content.\nLine 2";

        // Act 1: write_text
        using (var writeArgs = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = "write_text",
            path = testFilePath,
            content = testContent
        })))
        {
            var writeResult = await tool.ExecuteAsync(writeArgs.RootElement, ToolExecutionContext.Trusted());
            writeResult.Success.Should().BeTrue();
        }

        // Act 2: file_exists
        using (var existsArgs = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = "file_exists",
            path = testFilePath
        })))
        {
            var existsResult = await tool.ExecuteAsync(existsArgs.RootElement, ToolExecutionContext.Trusted());
            existsResult.Success.Should().BeTrue();
            existsResult.Output.Should().Be("true");
        }

        // Act 3: read_text
        using (var readArgs = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = "read_text",
            path = testFilePath
        })))
        {
            var readResult = await tool.ExecuteAsync(readArgs.RootElement, ToolExecutionContext.Trusted());
            readResult.Success.Should().BeTrue();
            readResult.Output.Should().Be(testContent);
        }

        // Act 4: list_directory
        using (var listArgs = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = "list_directory",
            path = _scope.DirectoryPath
        })))
        {
            var listResult = await tool.ExecuteAsync(listArgs.RootElement, ToolExecutionContext.Trusted());
            listResult.Success.Should().BeTrue();
            listResult.Output.Should().Contain("hitodama_notes.txt");
        }
    }

    [Fact]
    public async Task FileReadWriteTool_SensitivePathAccess_IsBlocked()
    {
        // Arrange
        var tool = new FileReadWriteTool();
        using var readArgs = JsonDocument.Parse("""
            {
              "action": "read_text",
              "path": "C:\\Windows\\System32\\config\\SAM"
            }
            """);

        // Act
        var result = await tool.ExecuteAsync(readArgs.RootElement, ToolExecutionContext.Trusted());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Access denied");
    }

    [Fact]
    public async Task WebSearchTool_ExecutesAndTaintsSessionContext()
    {
        // Arrange
        var gatekeeper = new GatekeeperService();
        gatekeeper.IsContextUntrusted.Should().BeFalse();

        var tool = new WebSearchTool(gatekeeper);
        using var searchArgs = JsonDocument.Parse("""
            {
              "query": "Hitodama voice assistant architecture"
            }
            """);

        // Act
        var result = await tool.ExecuteAsync(searchArgs.RootElement, ToolExecutionContext.Trusted());

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().NotBeNullOrWhiteSpace();

        // Taint tracking verification: context MUST be flagged as untrusted after web search!
        gatekeeper.IsContextUntrusted.Should().BeTrue("web search results contain external text that taints the session");
    }
}
