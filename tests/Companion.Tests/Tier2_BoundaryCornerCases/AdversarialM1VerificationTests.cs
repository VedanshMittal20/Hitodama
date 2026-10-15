using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Companion.Core;
using Companion.Services.Tools;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier2_BoundaryCornerCases;

public sealed class AdversarialM1VerificationTests
{
    #region NamedPipe IPC Adversarial Tests

    [Fact]
    public async Task Ipc_RapidConnectDisconnect_DoesNotDeadlockServer()
    {
        string pipeName = $"adv-rapid-{Guid.NewGuid():N}";
        await using var server = new CompanionIpcServer(pipeName);
        server.RegisterMethod("ping", (_, _) => Task.FromResult<object?>("pong"));
        server.Start();

        // 10 clients rapidly connect and immediately disconnect
        for (int i = 0; i < 10; i++)
        {
            await using var client = new CompanionIpcClient(pipeName);
            await client.ConnectAsync(TimeSpan.FromSeconds(3));
            client.IsConnected.Should().BeTrue();
            // Disconnect immediately via DisposeAsync
        }

        // Verify server is still completely responsive to a subsequent client
        await using var finalClient = new CompanionIpcClient(pipeName);
        await finalClient.ConnectAsync(TimeSpan.FromSeconds(3));
        var resp = await finalClient.SendRequestAsync("ping", null, TimeSpan.FromSeconds(3));
        resp.IsSuccess.Should().BeTrue();
        resp.GetResult<string>().Should().Be("pong");
    }

    [Fact]
    public async Task Ipc_ParallelClients_HandleConcurrentRequestsWithoutCrosstalk()
    {
        string pipeName = $"adv-parallel-{Guid.NewGuid():N}";
        await using var server = new CompanionIpcServer(pipeName);
        server.RegisterMethod("echo", (p, _) =>
        {
            string? msg = p?.GetProperty("msg").GetString();
            return Task.FromResult<object?>(msg);
        });
        server.Start();

        const int clientCount = 5;
        var clients = new List<CompanionIpcClient>();
        try
        {
            for (int i = 0; i < clientCount; i++)
            {
                var c = new CompanionIpcClient(pipeName);
                await c.ConnectAsync(TimeSpan.FromSeconds(5));
                clients.Add(c);
            }

            var tasks = clients.Select((client, idx) => Task.Run(async () =>
            {
                for (int req = 0; req < 5; req++)
                {
                    string expected = $"client-{idx}-req-{req}";
                    var resp = await client.SendRequestAsync("echo", new { msg = expected }, TimeSpan.FromSeconds(5));
                    resp.IsSuccess.Should().BeTrue();
                    resp.GetResult<string>().Should().Be(expected);
                }
            })).ToList();

            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var c in clients)
            {
                await c.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Ipc_BurstConcurrentConnections_AllClientsConnectSuccessfully()
    {
        string pipeName = $"adv-burst-{Guid.NewGuid():N}";
        await using var server = new CompanionIpcServer(pipeName);
        server.RegisterMethod("ping", (_, _) => Task.FromResult<object?>("pong"));
        server.Start();

        const int clientCount = 15;
        var clients = Enumerable.Range(0, clientCount).Select(_ => new CompanionIpcClient(pipeName)).ToList();
        try
        {
            // Connect all 15 clients in parallel
            await Task.WhenAll(clients.Select(c => c.ConnectAsync(TimeSpan.FromSeconds(5))));

            // Send a request from each client in parallel
            var results = await Task.WhenAll(clients.Select(c => c.SendRequestAsync("ping", null, TimeSpan.FromSeconds(5))));
            results.Should().AllSatisfy(r =>
            {
                r.IsSuccess.Should().BeTrue();
                r.GetResult<string>().Should().Be("pong");
            });
        }
        finally
        {
            foreach (var c in clients)
            {
                await c.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Ipc_MalformedJson_ReturnsParseErrorAndKeepsServerAlive()
    {
        string pipeName = $"adv-malformed-{Guid.NewGuid():N}";
        await using var server = new CompanionIpcServer(pipeName);
        server.RegisterMethod("ping", (_, _) => Task.FromResult<object?>("pong"));
        server.Start();

        // Connect raw named pipe client stream
        using var rawClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await rawClient.ConnectAsync(3000);

        // Send invalid JSON line
        byte[] malformedBytes = Encoding.UTF8.GetBytes("{ broken json-rpc: true, \n");
        await rawClient.WriteAsync(malformedBytes);
        await rawClient.FlushAsync();

        // Read response
        var buffer = new byte[1024];
        int read = await rawClient.ReadAsync(buffer);
        string responseText = Encoding.UTF8.GetString(buffer, 0, read);

        using var doc = JsonDocument.Parse(responseText.Trim());
        doc.RootElement.TryGetProperty("error", out var errorProp).Should().BeTrue();
        errorProp.GetProperty("code").GetInt32().Should().Be(JsonRpcErrorCodes.ParseError);

        // Verify server is still alive for standard client
        await using var regularClient = new CompanionIpcClient(pipeName);
        await regularClient.ConnectAsync(TimeSpan.FromSeconds(3));
        var resp = await regularClient.SendRequestAsync("ping", null, TimeSpan.FromSeconds(3));
        resp.IsSuccess.Should().BeTrue();
        resp.GetResult<string>().Should().Be("pong");
    }

    [Fact]
    public async Task Ipc_UnexpectedMethod_ReturnsMethodNotFound()
    {
        string pipeName = $"adv-unexp-{Guid.NewGuid():N}";
        await using var server = new CompanionIpcServer(pipeName);
        server.Start();

        await using var client = new CompanionIpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(3));

        var resp = await client.SendRequestAsync("malicious.system.exploit", new { payload = "attack" }, TimeSpan.FromSeconds(3));
        resp.IsSuccess.Should().BeFalse();
        resp.Error.Should().NotBeNull();
        resp.Error!.Code.Should().Be(JsonRpcErrorCodes.MethodNotFound);
    }

    [Fact]
    public async Task Ipc_HugePayload_RoundtripsSuccessfullyWithoutTruncation()
    {
        string pipeName = $"adv-huge-{Guid.NewGuid():N}";
        await using var server = new CompanionIpcServer(pipeName);
        server.RegisterMethod("echoLength", (p, _) =>
        {
            string? text = p?.GetProperty("text").GetString();
            return Task.FromResult<object?>(text?.Length ?? 0);
        });
        server.Start();

        await using var client = new CompanionIpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(3));

        // 64 KB text payload
        string largePayload = new string('X', 64 * 1024);
        var resp = await client.SendRequestAsync("echoLength", new { text = largePayload }, TimeSpan.FromSeconds(5));

        resp.IsSuccess.Should().BeTrue();
        resp.GetResult<int>().Should().Be(64 * 1024);
    }

    [Fact]
    public async Task Ipc_Utf8NonAsciiPayload_PreservesEncoding()
    {
        string pipeName = $"adv-utf8-{Guid.NewGuid():N}";
        await using var server = new CompanionIpcServer(pipeName);
        server.RegisterMethod("echo", (p, _) =>
        {
            string? text = p?.GetProperty("text").GetString();
            return Task.FromResult<object?>(text);
        });
        server.Start();

        await using var client = new CompanionIpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(3));

        string unicodeText = "Hitodama - 人魂 霊魂 🌟 テスト";
        var resp = await client.SendRequestAsync("echo", new { text = unicodeText }, TimeSpan.FromSeconds(3));

        resp.IsSuccess.Should().BeTrue();
        string? result = resp.GetResult<string>();
        result.Should().Be(unicodeText, "UTF-8 multi-byte characters must not be corrupted by IPC stream framing");
    }

    [Fact]
    public async Task NewlineIpcFramer_RawUtf8MultibyteBytes_DecodesAccurately()
    {
        var framer = new NewlineIpcFramer();
        using var ms = new MemoryStream();
        string originalText = "{\"text\": \"人魂 - Hitodama\"}";
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(originalText + "\n");
        ms.Write(utf8Bytes);
        ms.Position = 0;

        string? decoded = await framer.ReadMessageAsync(ms, CancellationToken.None);
        decoded.Should().Be(originalText, "NewlineIpcFramer must decode UTF-8 byte sequences without Mojibake corruption");
    }

    #endregion

    #region Gatekeeper Security Engine Adversarial Tests

    [Fact]
    public async Task Gatekeeper_UntrustedContext_EscalatesLowRiskToolToConfirmation()
    {
        using var gatekeeper = new GatekeeperSecurityEngine();
        var tool = new ToolDefinition("System.GetStatus", "Reads status", ToolRiskLevel.Low);
        var untrustedContext = new ToolExecutionContext(IsUntrustedInput: true, SourceMessageId: "untrusted-prompt-1");
        using var args = JsonDocument.Parse("{}");

        bool approvalRequested = false;
        gatekeeper.ApprovalRequested += req =>
        {
            approvalRequested = true;
            req.Tool.Name.Should().Be("System.GetStatus");
            req.Context.IsUntrustedInput.Should().BeTrue();
            gatekeeper.RespondToRequest(req.RequestId, true);
        };

        bool approved = await gatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, untrustedContext);

        approvalRequested.Should().BeTrue("untrusted context must trigger user confirmation even for low-risk tools");
        approved.Should().BeTrue();
    }

    [Fact]
    public async Task Gatekeeper_TaintSessionFlag_EscalatesAllToolsUntilReset()
    {
        using var gatekeeper = new GatekeeperSecurityEngine();
        var tool = new ToolDefinition("System.GetTheme", "Reads theme", ToolRiskLevel.Low);
        var trustedContext = ToolExecutionContext.Trusted();
        using var args = JsonDocument.Parse("{}");

        // Step 1: Trusted context executes autonomously
        bool initialApproved = await gatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, trustedContext);
        initialApproved.Should().BeTrue();
        gatekeeper.GetPendingRequests().Should().BeEmpty();

        // Step 2: Taint session (e.g. from web search or indirect injection)
        gatekeeper.TrackUntrustedContext(true);
        gatekeeper.IsContextUntrusted.Should().BeTrue();

        bool dialogTriggered = false;
        gatekeeper.ApprovalRequested += req =>
        {
            dialogTriggered = true;
            gatekeeper.RespondToRequest(req.RequestId, false); // User rejects
        };

        // Step 3: Same low-risk tool now requires confirmation and is rejected
        bool taintedApproved = await gatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, trustedContext);
        dialogTriggered.Should().BeTrue();
        taintedApproved.Should().BeFalse();

        // Step 4: Reset taint
        gatekeeper.ResetUntrustedContext();
        gatekeeper.IsContextUntrusted.Should().BeFalse();

        bool resetApproved = await gatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, trustedContext);
        resetApproved.Should().BeTrue("after reset, low-risk tools in trusted context are autonomous again");
    }

    [Fact]
    public async Task Gatekeeper_TimeoutExpiration_StrictDefaultDeny()
    {
        // 100ms timeout
        using var gatekeeper = new GatekeeperSecurityEngine(defaultTimeout: TimeSpan.FromMilliseconds(100));
        var tool = new ToolDefinition("File.Delete", "Deletes file", ToolRiskLevel.High);
        using var args = JsonDocument.Parse("{}");

        // Do not respond to approval request
        bool approved = await gatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, ToolExecutionContext.Trusted());

        approved.Should().BeFalse("timeout must result in default-deny");
        gatekeeper.ApprovedCount.Should().Be(0);
        gatekeeper.RejectedCount.Should().Be(1);
        gatekeeper.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task Gatekeeper_LateApprovalAfterTimeout_IsIgnoredAndReturnsFalse()
    {
        using var gatekeeper = new GatekeeperSecurityEngine(defaultTimeout: TimeSpan.FromMilliseconds(80));
        var tool = new ToolDefinition("Power.Reboot", "Reboots PC", ToolRiskLevel.High);
        using var args = JsonDocument.Parse("{}");

        string? capturedReqId = null;
        gatekeeper.ApprovalRequested += req => capturedReqId = req.RequestId;

        var task = gatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, ToolExecutionContext.Trusted());

        // Wait for timeout to expire
        bool result = await task;
        result.Should().BeFalse();

        // Attempt to approve after timeout
        capturedReqId.Should().NotBeNull();
        bool lateRespond = gatekeeper.RespondToRequest(capturedReqId!, true);
        lateRespond.Should().BeFalse("cannot approve a request that has already timed out");
    }

    [Fact]
    public async Task Gatekeeper_CancelledRequest_AbortsImmediatelyWithDenial()
    {
        using var gatekeeper = new GatekeeperSecurityEngine(defaultTimeout: TimeSpan.FromSeconds(30));
        var tool = new ToolDefinition("System.Format", "Format disk", ToolRiskLevel.High);
        using var args = JsonDocument.Parse("{}");

        using var cts = new CancellationTokenSource();
        var task = gatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, ToolExecutionContext.Trusted(), cts.Token);

        gatekeeper.GetPendingRequests().Should().HaveCount(1);

        // Cancel after 50ms
        await Task.Delay(50);
        cts.Cancel();

        bool approved = await task;
        approved.Should().BeFalse("cancelled request must resolve to false (denied)");
        gatekeeper.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task Gatekeeper_EmergencyKillSwitch_CancelsAllPendingRequestsImmediately()
    {
        using var gatekeeper = new GatekeeperSecurityEngine(defaultTimeout: TimeSpan.FromSeconds(30));
        using var args = JsonDocument.Parse("{}");

        var tasks = Enumerable.Range(0, 10).Select(i =>
        {
            var tool = new ToolDefinition($"Tool_{i}", $"Description {i}", ToolRiskLevel.High);
            return gatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, ToolExecutionContext.Trusted());
        }).ToList();

        gatekeeper.GetPendingRequests().Should().HaveCount(10);

        // Trigger emergency kill switch
        gatekeeper.CancelAllPendingRequests();

        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.Should().BeFalse());
        gatekeeper.GetPendingRequests().Should().BeEmpty();
        gatekeeper.RejectedCount.Should().Be(10);
    }

    [Fact]
    public async Task GatekeeperService_InServicesTools_FollowsSameInvariants()
    {
        // Test the GatekeeperService in Companion.Services.Tools
        using var service = new GatekeeperService(TimeSpan.FromMilliseconds(100));
        var tool = new ToolDefinition("Shell.Run", "Runs shell", ToolRiskLevel.High);
        using var args = JsonDocument.Parse("{}");

        // Timeout test
        bool approved = await service.RequestExecutionApprovalAsync(tool, args.RootElement, ToolExecutionContext.Trusted());
        approved.Should().BeFalse("GatekeeperService in Companion.Services.Tools must also default-deny on timeout");

        // Untrusted context test
        service.TrackUntrustedContext(true);
        var lowRiskTool = new ToolDefinition("System.GetVolume", "Volume", ToolRiskLevel.Low);
        bool requiresApproval = service.RequiresApproval(lowRiskTool, args.RootElement, ToolExecutionContext.Trusted(), out var risk, out var reason);
        requiresApproval.Should().BeTrue("untrusted context in GatekeeperService must force confirmation");
        reason.Should().Contain("UNTRUSTED");
    }

    [Fact]
    public async Task Gatekeeper_PreCancelledToken_IncrementsRejectedCountExactlyOnce()
    {
        using var gatekeeper = new GatekeeperSecurityEngine(defaultTimeout: TimeSpan.FromSeconds(5));
        var tool = new ToolDefinition("Tool.Test", "Test", ToolRiskLevel.High);
        using var args = JsonDocument.Parse("{}");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool result = await gatekeeper.RequestExecutionApprovalAsync(tool, args.RootElement, ToolExecutionContext.Trusted(), cts.Token);
        result.Should().BeFalse();
        gatekeeper.ApprovedCount.Should().Be(0);
        gatekeeper.RejectedCount.Should().Be(1);
        gatekeeper.TotalApprovalRequests.Should().Be(1);
    }

    [Fact]
    public async Task GatekeeperService_TimeoutAndKillSwitch_CountsRejectionsAccurately()
    {
        using var service = new GatekeeperService(TimeSpan.FromMilliseconds(50));
        var tool = new ToolDefinition("Shell.Run", "Runs shell", ToolRiskLevel.High);
        using var args = JsonDocument.Parse("{}");

        // 1. Timeout
        bool approved = await service.RequestExecutionApprovalAsync(tool, args.RootElement, ToolExecutionContext.Trusted());
        approved.Should().BeFalse();
        service.RejectedCount.Should().Be(1);
        service.ApprovedCount.Should().Be(0);

        // 2. Kill switch on 5 concurrent requests
        using var longService = new GatekeeperService(TimeSpan.FromSeconds(30));
        var tasks = Enumerable.Range(0, 5).Select(_ =>
            longService.RequestExecutionApprovalAsync(tool, args.RootElement, ToolExecutionContext.Trusted())
        ).ToList();

        longService.CancelAllPendingRequests();
        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.Should().BeFalse());
        longService.RejectedCount.Should().Be(5);
        longService.ApprovedCount.Should().Be(0);
    }

    [Fact]
    public async Task ITool_MutualRecursionEliminated_ThrowsNotSupportedExceptionIfUnimplemented()
    {
        ITool dummy = new UnimplementedTool();
        using var args = JsonDocument.Parse("{}");

        // Calling ExecuteAsync with context must throw NotSupportedException, not StackOverflowException
        Func<Task> callWithContext = async () => await dummy.ExecuteAsync(args.RootElement, ToolExecutionContext.Trusted());
        await callWithContext.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*does not implement ExecuteAsync*");

        // Calling ExecuteAsync without context must also throw NotSupportedException
        Func<Task> callWithoutContext = async () => await dummy.ExecuteAsync(args.RootElement);
        await callWithoutContext.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*does not implement ExecuteAsync*");
    }

    [Fact]
    public async Task ITool_ContextOverloadImplemented_BackwardCompatibleOverloadDelegatesCleanly()
    {
        ITool tool = new ContextOnlyTool();
        using var args = JsonDocument.Parse("{}");

        // Parameterless overload should delegate to context overload and return string output
        string output = await tool.ExecuteAsync(args.RootElement);
        output.Should().Be("SuccessFromContext");
    }

    [Fact]
    public async Task FileReadWriteTool_LegitimatePathsWithSampleOrSystemSubstrings_NotBlocked()
    {
        var tool = new FileReadWriteTool();

        // Temporary directory with "samples" in folder name
        string tempDir = Path.Combine(Path.GetTempPath(), $"samples_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string sampleFilePath = Path.Combine(tempDir, "system_notes.md");
        File.WriteAllText(sampleFilePath, "# System Architecture Notes");

        try
        {
            using var readArgs = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                action = "read_text",
                path = sampleFilePath
            }));

            var result = await tool.ExecuteAsync(readArgs.RootElement, ToolExecutionContext.Trusted());
            result.Success.Should().BeTrue("files in paths containing 'samples' or 'system_notes' must not be blocked");
            result.Output.Should().Contain("# System Architecture Notes");

            // Also test file_exists
            using var existsArgs = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                action = "file_exists",
                path = sampleFilePath
            }));
            var existsResult = await tool.ExecuteAsync(existsArgs.RootElement, ToolExecutionContext.Trusted());
            existsResult.Success.Should().BeTrue();
            existsResult.Output.Should().Be("true");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FileReadWriteTool_SensitiveLocations_AreStrictlyBlocked()
    {
        var tool = new FileReadWriteTool();

        string[] sensitivePaths =
        [
            @"C:\Windows\System32\config\SAM",
            @"C:\Windows\System32\drivers\etc\hosts",
            @"C:\Windows\System\vmm32.vxd",
            @"C:\Users\Someone\.ssh\id_rsa",
            @"C:\Users\Someone\.aws\credentials",
            @"C:\Users\Someone\AppData\Local\Microsoft\Credentials\Token"
        ];

        foreach (string path in sensitivePaths)
        {
            using var readArgs = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                action = "read_text",
                path
            }));

            var result = await tool.ExecuteAsync(readArgs.RootElement, ToolExecutionContext.Trusted());
            result.Success.Should().BeFalse($"sensitive path '{path}' must be blocked");
            result.ErrorMessage.Should().Contain("Access denied");
        }
    }

    private sealed class UnimplementedTool : ITool
    {
        public ToolDefinition Definition { get; } = new("Unimplemented", "Test tool", ToolRiskLevel.Low);
    }

    private sealed class ContextOnlyTool : ITool
    {
        public ToolDefinition Definition { get; } = new("ContextOnly", "Test tool", ToolRiskLevel.Low);

        public Task<ToolResult> ExecuteAsync(
            JsonElement arguments,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ToolResult.Ok("SuccessFromContext"));
        }
    }

    #endregion
}
