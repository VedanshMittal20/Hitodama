using System.Text.Json;
using Companion.Core;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class JsonRpcIpcTests : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly CompanionIpcServer _server;
    private readonly CompanionIpcClient _client;
    private SoulLoadout _activeLoadout = new();
    private int _activeTab = 0;

    public JsonRpcIpcTests()
    {
        _pipeName = $"test-ipc-{Guid.NewGuid():N}";
        _server = new CompanionIpcServer(_pipeName);
        _client = new CompanionIpcClient(_pipeName);

        // Register standard IPC contracts
        _server.RegisterMethod("shell.getState", (_, _) =>
            Task.FromResult<object?>(new ShellStateResult("Collapsed", false, _activeTab)));

        _server.RegisterMethod("shell.setTab", (p, _) =>
        {
            if (p is null) throw new ArgumentException("Parameters required");
            var param = p.Value.Deserialize<ShellSetTabParams>();
            if (param is null || param.TabIndex is < 0 or > 6)
                throw new ArgumentException("Invalid TabIndex");

            _activeTab = param.TabIndex;
            return Task.FromResult<object?>(new { success = true, currentTab = _activeTab });
        });

        _server.RegisterMethod("loadout.get", (_, _) =>
            Task.FromResult<object?>(_activeLoadout));

        _server.RegisterMethod("loadout.set", (p, _) =>
        {
            if (p is null) throw new ArgumentException("Parameters required");
            var loadout = p.Value.Deserialize<SoulLoadout>();
            if (loadout is null) throw new ArgumentException("Invalid loadout JSON");

            _activeLoadout = loadout;
            return Task.FromResult<object?>(new { success = true });
        });

        _server.RegisterMethod("gatekeeper.respond", (p, _) =>
        {
            if (p is null) throw new ArgumentException("Parameters required");
            var resp = p.Value.Deserialize<GatekeeperRespondParams>();
            if (resp is null || string.IsNullOrWhiteSpace(resp.RequestId))
                throw new ArgumentException("RequestId required");

            return Task.FromResult<object?>(new { success = true, requestId = resp.RequestId });
        });

        _server.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task ClientServer_EndToEnd_ShellStateAndTabs_RoundtripSuccessfully()
    {
        // Arrange
        await _client.ConnectAsync(TimeSpan.FromSeconds(3));

        // Act 1: Get initial state
        var state = await _client.GetShellStateAsync(TimeSpan.FromSeconds(3));
        state.Should().NotBeNull();
        state!.IslandState.Should().Be("Collapsed");
        state.CurrentTab.Should().Be(0);

        // Act 2: Switch tab
        bool tabChanged = await _client.SetShellTabAsync(3, TimeSpan.FromSeconds(3));
        tabChanged.Should().BeTrue();

        // Act 3: Verify updated tab
        var updatedState = await _client.GetShellStateAsync(TimeSpan.FromSeconds(3));
        updatedState!.CurrentTab.Should().Be(3);
    }

    [Fact]
    public async Task ClientServer_EndToEnd_LoadoutGetAndSet_Synchronizes()
    {
        // Arrange
        await _client.ConnectAsync(TimeSpan.FromSeconds(3));

        // Act 1: Get loadout
        var loadout = await _client.GetLoadoutAsync(TimeSpan.FromSeconds(3));
        loadout.Should().NotBeNull();
        loadout!.SoulName.Should().Be("Void");

        // Act 2: Set modified loadout
        loadout.SoulName = "Void-CyberPro";
        loadout.Voice = VoicePreset.Fenrir;
        bool setSuccess = await _client.SetLoadoutAsync(loadout, TimeSpan.FromSeconds(3));
        setSuccess.Should().BeTrue();

        // Act 3: Verify persisted change
        var updated = await _client.GetLoadoutAsync(TimeSpan.FromSeconds(3));
        updated!.SoulName.Should().Be("Void-CyberPro");
        updated.Voice.Should().Be(VoicePreset.Fenrir);
    }

    [Fact]
    public async Task ClientServer_GatekeeperRespond_ProcessesAuthorizationDecision()
    {
        // Arrange
        await _client.ConnectAsync(TimeSpan.FromSeconds(3));
        string testReqId = Guid.NewGuid().ToString("N");

        // Act
        bool responded = await _client.RespondGatekeeperAsync(testReqId, approved: true, TimeSpan.FromSeconds(3));

        // Assert
        responded.Should().BeTrue();
    }

    [Fact]
    public async Task ClientServer_UnknownMethod_ReturnsMethodNotFoundErrorCode()
    {
        // Arrange
        await _client.ConnectAsync(TimeSpan.FromSeconds(3));

        // Act
        var resp = await _client.SendRequestAsync("unknown.fakeMethod", null, TimeSpan.FromSeconds(3));

        // Assert
        resp.IsSuccess.Should().BeFalse();
        resp.Error.Should().NotBeNull();
        resp.Error!.Code.Should().Be(JsonRpcErrorCodes.MethodNotFound);
    }

    [Fact]
    public async Task ClientServer_InvalidParams_ReturnsInvalidParamsErrorCode()
    {
        // Arrange
        await _client.ConnectAsync(TimeSpan.FromSeconds(3));

        // Act: send negative tabIndex to shell.setTab
        var resp = await _client.SendRequestAsync("shell.setTab", new { tabIndex = -1 }, TimeSpan.FromSeconds(3));

        // Assert
        resp.IsSuccess.Should().BeFalse();
        resp.Error.Should().NotBeNull();
        resp.Error!.Code.Should().Be(JsonRpcErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task ClientServer_BroadcastNotification_IsReceivedByClient()
    {
        // Arrange
        var tcs = new TaskCompletionSource<ShellStateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.ShellStateChanged += state => tcs.TrySetResult(state);
        await _client.ConnectAsync(TimeSpan.FromSeconds(3));

        // Act: server pushes notification
        await _server.BroadcastNotificationAsync("shell.stateChanged", new ShellStateResult("ExpandedDock", true, 2));

        // Assert
        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        result.IslandState.Should().Be("ExpandedDock");
        result.IsLiveActive.Should().BeTrue();
        result.CurrentTab.Should().Be(2);
    }

    [Fact]
    public async Task ClientServer_ConcurrentRequests_CompleteWithoutPipeCorruption()
    {
        // Arrange: 20 parallel requests sent concurrently over single pipe client
        await _client.ConnectAsync(TimeSpan.FromSeconds(3));

        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var state = await _client.GetShellStateAsync(TimeSpan.FromSeconds(5));
            state.Should().NotBeNull();
        });

        // Act & Assert
        await Task.WhenAll(tasks);
    }
}
