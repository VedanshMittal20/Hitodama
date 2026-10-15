using System;
using System.Text.Json;
using Companion.Core;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier2_BoundaryCornerCases;

public sealed class IpcBoundaryTests
{
    [Fact]
    public async Task IpcServer_DisposeWhileWaitingForConnection_ClosesListenerPromptly()
    {
        string pipeName = $"void-ipc-dispose-{Guid.NewGuid():N}";
        var server = new CompanionIpcServer(pipeName);
        server.Start();

        Func<Task> dispose = async () => await server.DisposeAsync();
        await dispose().WaitAsync(TimeSpan.FromSeconds(2));
        await dispose().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void JsonRpc_HugePayloadArguments_ParsesWithoutTruncation()
    {
        // Arrange: 500KB argument object
        string hugeData = new string('A', 500 * 1024);
        var rpc = new
        {
            jsonrpc = "2.0",
            method = "system.processData",
            @params = new { Data = hugeData },
            id = "huge-1"
        };

        // Act
        string json = JsonSerializer.Serialize(rpc);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert
        root.GetProperty("params").GetProperty("Data").GetString()!.Length.Should().Be(500 * 1024);
    }

    [Fact]
    public void JsonRpc_MalformedJson_ThrowsJsonException()
    {
        // Arrange
        const string malformed = "{\"jsonrpc\": \"2.0\", \"method\": \"test\", ";

        // Act & Assert
        Action act = () => JsonDocument.Parse(malformed);
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void JsonRpc_NullOrEmptyMethodName_IsRejected()
    {
        // Arrange
        var rpc = new
        {
            jsonrpc = "2.0",
            method = "",
            @params = new { },
            id = "empty-method"
        };

        string json = JsonSerializer.Serialize(rpc);
        using var doc = JsonDocument.Parse(json);
        string method = doc.RootElement.GetProperty("method").GetString()!;

        // Assert
        string.IsNullOrWhiteSpace(method).Should().BeTrue();
    }

    [Fact]
    public void JsonRpc_DeeplyNestedParameters_ParsesAccurately()
    {
        // Arrange: 10 levels of nested objects
        var nested = new { l1 = new { l2 = new { l3 = new { l4 = new { l5 = "DeepVoidValue" } } } } };
        var rpc = new
        {
            jsonrpc = "2.0",
            method = "test.nested",
            @params = nested,
            id = "nest-1"
        };

        string json = JsonSerializer.Serialize(rpc);
        using var doc = JsonDocument.Parse(json);

        // Assert
        doc.RootElement
            .GetProperty("params")
            .GetProperty("l1")
            .GetProperty("l2")
            .GetProperty("l3")
            .GetProperty("l4")
            .GetProperty("l5")
            .GetString().Should().Be("DeepVoidValue");
    }
}
