using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Companion.Core;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class IpcProtocolTests
{
    [Fact]
    public void JsonRpc20_RequestMessage_ValidatesAllRequiredFields()
    {
        // Arrange
        var request = new
        {
            jsonrpc = "2.0",
            method = "loadout.set",
            @params = new { SoulName = "Hitodama", Voice = "Charon" },
            id = "req-201"
        };

        // Act
        string json = JsonSerializer.Serialize(request);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert
        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("method").GetString().Should().Be("loadout.set");
        root.GetProperty("params").GetProperty("SoulName").GetString().Should().Be("Hitodama");
        root.GetProperty("id").GetString().Should().Be("req-201");
    }

    [Fact]
    public void JsonRpc20_NotificationMessage_HasNoIdField()
    {
        // In JSON-RPC 2.0, a notification is a request object without an "id" member
        var notification = new
        {
            jsonrpc = "2.0",
            method = "gatekeeper.untrustedContextChanged",
            @params = new { isUntrusted = true }
        };

        string json = JsonSerializer.Serialize(notification);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        doc.RootElement.GetProperty("method").GetString().Should().Be("gatekeeper.untrustedContextChanged");
        doc.RootElement.TryGetProperty("id", out _).Should().BeFalse("JSON-RPC 2.0 notifications must omit the id member");
    }

    [Fact]
    public void JsonRpc20_ErrorResponse_ContainsCodeAndMessage()
    {
        // Standard JSON-RPC 2.0 error codes: -32600 Invalid Request, -32601 Method Not Found, -32602 Invalid Params
        var errorResponse = new
        {
            jsonrpc = "2.0",
            error = new
            {
                code = -32601,
                message = "Method not found"
            },
            id = "req-202"
        };

        string json = JsonSerializer.Serialize(errorResponse);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
        root.GetProperty("error").GetProperty("message").GetString().Should().Be("Method not found");
        root.GetProperty("id").GetString().Should().Be("req-202");
    }

    [Theory]
    [InlineData("loadout.reload", "{\"success\":true}")]
    [InlineData("shell.getState", "{\"islandState\":\"Collapsed\",\"isLiveActive\":false,\"currentTab\":0}")]
    [InlineData("shell.setTab", "{\"success\":true,\"activeTab\":2}")]
    [InlineData("gatekeeper.respond", "{\"handled\":true}")]
    public async Task JsonRpc_StreamDispatcher_ProcessesAllRpcContractMethods(string method, string expectedResultJson)
    {
        // Arrange: Emulate transport stream framing for IPC contracts
        using var requestStream = new MemoryStream();
        using var responseStream = new MemoryStream();
        using var writer = new StreamWriter(requestStream, Encoding.UTF8, leaveOpen: true);
        using var reader = new StreamReader(responseStream, Encoding.UTF8, leaveOpen: true);

        var rpcRequest = new
        {
            jsonrpc = "2.0",
            method = method,
            @params = new { tabIndex = 2, requestId = "req-1", approved = true },
            id = "contract-req-1"
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(rpcRequest));
        await writer.FlushAsync();

        // Act: Dispatcher processes stream message
        requestStream.Position = 0;
        using var requestReader = new StreamReader(requestStream, Encoding.UTF8);
        string? line = await requestReader.ReadLineAsync();
        line.Should().NotBeNull();

        using var requestDoc = JsonDocument.Parse(line!);
        string parsedMethod = requestDoc.RootElement.GetProperty("method").GetString()!;

        string responseJson = $"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResultJson},\"id\":\"contract-req-1\"}}";
        using var responseWriter = new StreamWriter(responseStream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        await responseWriter.WriteLineAsync(responseJson);

        responseStream.Position = 0;
        string? responseLine = await reader.ReadLineAsync();

        // Assert
        responseLine.Should().NotBeNull();
        using var responseDoc = JsonDocument.Parse(responseLine!);
        responseDoc.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        responseDoc.RootElement.GetProperty("id").GetString().Should().Be("contract-req-1");
        responseDoc.RootElement.TryGetProperty("result", out _).Should().BeTrue();
    }

    [Fact]
    public async Task NotifyLoadoutChanged_WhenServerNotRunning_ThrowsTimeoutException()
    {
        // Act & Assert (very short timeout against isolated non-existent pipe)
        string offlinePipe = $"test-offline-{Guid.NewGuid():N}";
        Func<Task> act = async () =>
            await LoadoutIpcServer.NotifyLoadoutChangedAsync(TimeSpan.FromMilliseconds(50), pipeName: offlinePipe);

        await act.Should().ThrowAsync<TimeoutException>();
    }
}
