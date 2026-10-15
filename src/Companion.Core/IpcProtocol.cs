using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Companion.Core;

public static class IpcConstants
{
    public const string PipeName = "void-companion-ipc";
    public const string LegacyPipeName = "companion-ipc";
    public const string JsonRpcVersion = "2.0";
}

public static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    // Server-defined errors (-32000 to -32099)
    public const int ExecutionFailed = -32000;
    public const int RequestTimeout = -32001;
    public const int InvalidState = -32002;
    public const int Unauthorized = -32003;
}

public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = IpcConstants.JsonRpcVersion;

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }

    [JsonIgnore]
    public bool IsNotification => Id is null;
}

public sealed class JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = IpcConstants.JsonRpcVersion;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }
}

public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = IpcConstants.JsonRpcVersion;

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    [JsonIgnore]
    public bool IsSuccess => Error is null;

    public T? GetResult<T>(JsonSerializerOptions? options = null)
    {
        if (Result is JsonElement element)
        {
            return element.Deserialize<T>(options);
        }

        if (Result is T typedResult)
        {
            return typedResult;
        }

        if (Result is not null)
        {
            string raw = JsonSerializer.Serialize(Result);
            return JsonSerializer.Deserialize<T>(raw, options);
        }

        return default;
    }

    public static JsonRpcResponse CreateSuccess(object? id, object? result) => new()
    {
        Id = id,
        Result = result ?? true
    };

    public static JsonRpcResponse CreateError(object? id, int code, string message, object? data = null) => new()
    {
        Id = id,
        Error = new JsonRpcError
        {
            Code = code,
            Message = message,
            Data = data
        }
    };
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }
}

// Method DTO Contracts

public sealed record LoadoutReloadResult(bool Success);

public sealed record LoadoutSetParams([property: JsonPropertyName("loadout")] SoulLoadout Loadout);

public sealed record GatekeeperRespondParams(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("approved")] bool Approved);

public sealed record ShellStateResult(
    [property: JsonPropertyName("islandState")] string IslandState,
    [property: JsonPropertyName("isLiveActive")] bool IsLiveActive,
    [property: JsonPropertyName("currentTab")] int CurrentTab);

public sealed record ShellSetTabParams([property: JsonPropertyName("tabIndex")] int TabIndex);

public sealed record GatekeeperApprovalRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("riskLevel")] string RiskLevel,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("argumentsJson")] string? ArgumentsJson);

// Stream Framer

public interface IIpcFramer
{
    Task WriteMessageAsync(Stream stream, string json, CancellationToken cancellationToken);
    Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken);
}

public sealed class NewlineIpcFramer : IIpcFramer
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public async Task WriteMessageAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        byte[] bytes = Utf8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[1];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            byte b = buffer[0];
            if (b == (byte)'\n')
            {
                break;
            }
            if (b != (byte)'\r')
            {
                ms.WriteByte(b);
            }
        }

        if (read == 0 && ms.Length == 0)
        {
            return null;
        }

        return Utf8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}

// Companion JSON-RPC IPC Server

public sealed class CompanionIpcServer : IAsyncDisposable, IDisposable
{
    private readonly string _pipeName;
    private readonly IIpcFramer _framer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, IpcServerSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Task> _sessionTasks = new();
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<object?>>> _methodHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task>> _notificationHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _listenerSync = new();
    private readonly object _lifecycleSync = new();
    private NamedPipeServerStream? _listener;
    private Task? _listenerTask;
    private Task? _disposeTask;

    public CompanionIpcServer(string pipeName = IpcConstants.PipeName, IIpcFramer? framer = null)
    {
        _pipeName = pipeName;
        _framer = framer ?? new NewlineIpcFramer();
    }

    public void RegisterMethod(string method, Func<JsonElement?, CancellationToken, Task<object?>> handler) =>
        _methodHandlers[method] = handler ?? throw new ArgumentNullException(nameof(handler));

    public void RegisterNotification(string method, Func<JsonElement?, CancellationToken, Task> handler) =>
        _notificationHandlers[method] = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Start()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_listenerTask is not null)
            {
                throw new InvalidOperationException("The IPC server is already running.");
            }

            _listenerTask = ListenAsync(_cts.Token);
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                lock (_listenerSync)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    _listener = pipe;
                }

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                ClearListener(pipe);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var session = new IpcServerSession(Guid.NewGuid(), pipe, _framer);
                _sessions[session.Id] = session;
                Task sessionTask = Task.Run(() => RunSessionAsync(session, ct), ct);
                _sessionTasks[session.Id] = sessionTask;
                _ = sessionTask.ContinueWith(
                    completedTask => _sessionTasks.TryRemove(session.Id, out Task? removedTask),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                pipe = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (pipe is not null)
                {
                    ClearListener(pipe);
                    pipe.Dispose();
                }
            }
        }
    }

    private void ClearListener(NamedPipeServerStream pipe)
    {
        lock (_listenerSync)
        {
            if (ReferenceEquals(_listener, pipe))
            {
                _listener = null;
            }
        }
    }

    private async Task RunSessionAsync(IpcServerSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && session.Stream.IsConnected)
            {
                string? rawJson = await session.Framer.ReadMessageAsync(session.Stream, ct).ConfigureAwait(false);
                if (rawJson is null) break;

                await ProcessIncomingMessageAsync(session, rawJson, ct).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("IPC session {0} closed after {1}.", session.Id, exception.GetType().Name);
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessIncomingMessageAsync(IpcServerSession session, string json, CancellationToken ct)
    {
        // Handle legacy single-command string if encountered
        if (string.Equals(json.Trim(), "reload-loadout", StringComparison.Ordinal))
        {
            if (_methodHandlers.TryGetValue("loadout.reload", out var legacyHandler))
            {
                try
                {
                    await legacyHandler(null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Trace.TraceError("Legacy loadout reload failed ({0}).", exception.GetType().Name);
                    await session.SendMessageRawAsync("error", ct).ConfigureAwait(false);
                    return;
                }
            }

            await session.SendMessageRawAsync("ok", ct).ConfigureAwait(false);
            return;
        }

        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(json);
            if (request is null || request.JsonRpc != IpcConstants.JsonRpcVersion)
            {
                await session.SendResponseAsync(JsonRpcResponse.CreateError(null, JsonRpcErrorCodes.InvalidRequest, "Invalid JSON-RPC 2.0 request."), ct).ConfigureAwait(false);
                return;
            }
        }
        catch (JsonException ex)
        {
            await session.SendResponseAsync(JsonRpcResponse.CreateError(null, JsonRpcErrorCodes.ParseError, "Parse error.", ex.Message), ct).ConfigureAwait(false);
            return;
        }

        if (request.IsNotification)
        {
            if (_notificationHandlers.TryGetValue(request.Method, out var notifHandler))
            {
                try
                {
                    await notifHandler(request.Params, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Suppress unhandled notification exceptions
                }
            }

            return;
        }

        if (!_methodHandlers.TryGetValue(request.Method, out var handler))
        {
            await session.SendResponseAsync(JsonRpcResponse.CreateError(request.Id, JsonRpcErrorCodes.MethodNotFound, $"Method '{request.Method}' not found."), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            object? result = await handler(request.Params, ct).ConfigureAwait(false);
            await session.SendResponseAsync(JsonRpcResponse.CreateSuccess(request.Id, result), ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await session.SendResponseAsync(JsonRpcResponse.CreateError(request.Id, JsonRpcErrorCodes.InvalidParams, ex.Message), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await session.SendResponseAsync(JsonRpcResponse.CreateError(request.Id, JsonRpcErrorCodes.InternalError, ex.Message), ct).ConfigureAwait(false);
        }
    }

    public async Task BroadcastNotificationAsync(string method, object? @params, CancellationToken ct = default)
    {
        var notification = new JsonRpcNotification
        {
            Method = method,
            Params = @params is null ? null : JsonSerializer.SerializeToElement(@params)
        };
        string json = JsonSerializer.Serialize(notification);

        foreach (var session in _sessions.Values)
        {
            try
            {
                await session.SendMessageRawAsync(json, ct).ConfigureAwait(false);
            }
            catch
            {
                // Ignore failure on disconnected sessions
            }
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            Task cancellation = _cts.CancelAsync();
            NamedPipeServerStream? listener;
            lock (_listenerSync)
            {
                listener = _listener;
                _listener = null;
            }

            try
            {
                listener?.Dispose();
            }
            catch
            {
                // Closing the listener is best effort during teardown.
            }

            try
            {
                await cancellation.ConfigureAwait(false);
            }
            catch
            {
                // Continue cleanup even if a cancellation callback faults.
            }

            if (_listenerTask is not null)
            {
                try
                {
                    await _listenerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Teardown is bounded; a stuck listener must not block process shutdown.
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is the expected listener shutdown path.
                }
            }

            foreach (IpcServerSession session in _sessions.Values)
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // A closed session must not prevent the remaining sessions from closing.
                }
            }

            Task[] sessionTasks = _sessionTasks.Values.ToArray();
            if (sessionTasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(sessionTasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Session callbacks are bounded so a broken client cannot hang disposal.
                }
                catch (OperationCanceledException)
                {
                    // Session cancellation is expected during server shutdown.
                }
                catch
                {
                    // Session loops isolate protocol failures; ignore any final teardown fault.
                }
            }
        }
        finally
        {
            _sessions.Clear();
            _sessionTasks.Clear();
            _cts.Dispose();
        }
    }

    private sealed class IpcServerSession : IAsyncDisposable
    {
        public Guid Id { get; }
        public NamedPipeServerStream Stream { get; }
        public IIpcFramer Framer { get; }
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposeStarted;

        public IpcServerSession(Guid id, NamedPipeServerStream stream, IIpcFramer framer)
        {
            Id = id;
            Stream = stream;
            Framer = framer;
        }

        public async Task SendResponseAsync(JsonRpcResponse response, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(response);
            await SendMessageRawAsync(json, ct).ConfigureAwait(false);
        }

        public async Task SendMessageRawAsync(string message, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await Framer.WriteMessageAsync(Stream, message, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            await Stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

// Companion JSON-RPC IPC Client

public sealed class CompanionIpcClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly IIpcFramer _framer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleSync = new();
    private NamedPipeClientStream? _stream;
    private Task? _readTask;
    private Task? _disposeTask;
    private long _idCounter;
    private int _disposeStarted;

    public event Action<JsonRpcNotification>? NotificationReceived;
    public event Action<SoulLoadout>? LoadoutChanged;
    public event Action<ShellStateResult>? ShellStateChanged;
    public event Action<GatekeeperApprovalRequest>? ApprovalRequested;

    public bool IsConnected => _stream?.IsConnected == true;

    public CompanionIpcClient(string pipeName = IpcConstants.PipeName, IIpcFramer? framer = null)
    {
        _pipeName = pipeName;
        _framer = framer ?? new NewlineIpcFramer();
    }

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        NamedPipeClientStream stream;
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_stream is not null)
            {
                throw new InvalidOperationException("The IPC client has already connected or is connecting.");
            }

            stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            _stream = stream;
        }

        try
        {
            await stream.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            lock (_lifecycleSync)
            {
                if (ReferenceEquals(_stream, stream))
                {
                    _stream = null;
                }
            }

            throw;
        }

        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stream?.IsConnected == true)
            {
                string? rawJson = await _framer.ReadMessageAsync(_stream, ct).ConfigureAwait(false);
                if (rawJson is null) break;

                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null)
                {
                    string id = idProp.ToString();
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        var response = JsonSerializer.Deserialize<JsonRpcResponse>(rawJson);
                        tcs.TrySetResult(response ?? JsonRpcResponse.CreateError(id, JsonRpcErrorCodes.InternalError, "Null response"));
                    }
                }
                else
                {
                    var notif = JsonSerializer.Deserialize<JsonRpcNotification>(rawJson);
                    if (notif is not null)
                    {
                        DispatchNotification(notif);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Client stream ended or canceled
        }
        finally
        {
            if (ct.IsCancellationRequested || _stream?.IsConnected != true)
            {
                FailPending(new IOException("The IPC connection closed before the response arrived."));
            }
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (KeyValuePair<string, TaskCompletionSource<JsonRpcResponse>> entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out TaskCompletionSource<JsonRpcResponse>? pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private void DispatchNotification(JsonRpcNotification notif)
    {
        NotificationReceived?.Invoke(notif);
        switch (notif.Method)
        {
            case "loadout.changed" when notif.Params.HasValue:
                var loadout = notif.Params.Value.Deserialize<SoulLoadout>();
                if (loadout is not null) LoadoutChanged?.Invoke(loadout);
                break;
            case "shell.stateChanged" when notif.Params.HasValue:
                var state = notif.Params.Value.Deserialize<ShellStateResult>();
                if (state is not null) ShellStateChanged?.Invoke(state);
                break;
            case "gatekeeper.approvalRequired" when notif.Params.HasValue:
                var req = notif.Params.Value.Deserialize<GatekeeperApprovalRequest>();
                if (req is not null) ApprovalRequested?.Invoke(req);
                break;
        }
    }

    public async Task<JsonRpcResponse> SendRequestAsync(string method, object? @params, TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (_stream is null || !_stream.IsConnected)
        {
            throw new InvalidOperationException("IPC client is not connected.");
        }

        string id = Interlocked.Increment(ref _idCounter).ToString();
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = @params is null ? null : JsonSerializer.SerializeToElement(@params)
        };
        string json = JsonSerializer.Serialize(request);

        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_stream is null || !_stream.IsConnected)
                {
                    throw new IOException("The IPC connection closed before the request could be sent.");
                }

                await _framer.WriteMessageAsync(_stream, json, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        using var ctsLinked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ctsLinked.CancelAfter(timeout);

        try
        {
            return await tcs.Task.WaitAsync(ctsLinked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"IPC request '{method}' timed out after {timeout.TotalSeconds}s.");
        }
    }

    // Strongly-Typed Helper Methods

    public async Task<bool> ReloadLoadoutAsync(TimeSpan timeout = default, CancellationToken ct = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(3) : timeout;
        var resp = await SendRequestAsync("loadout.reload", null, timeout, ct).ConfigureAwait(false);
        if (!resp.IsSuccess) throw new IOException($"loadout.reload failed: {resp.Error?.Message}");
        return true;
    }

    public async Task<SoulLoadout?> GetLoadoutAsync(TimeSpan timeout = default, CancellationToken ct = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(3) : timeout;
        var resp = await SendRequestAsync("loadout.get", null, timeout, ct).ConfigureAwait(false);
        if (!resp.IsSuccess) throw new IOException($"loadout.get failed: {resp.Error?.Message}");
        return resp.GetResult<SoulLoadout>();
    }

    public async Task<bool> SetLoadoutAsync(SoulLoadout loadout, TimeSpan timeout = default, CancellationToken ct = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(3) : timeout;
        var resp = await SendRequestAsync("loadout.set", loadout, timeout, ct).ConfigureAwait(false);
        if (!resp.IsSuccess) throw new IOException($"loadout.set failed: {resp.Error?.Message}");
        return true;
    }

    public async Task<bool> RespondGatekeeperAsync(string requestId, bool approved, TimeSpan timeout = default, CancellationToken ct = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(3) : timeout;
        var resp = await SendRequestAsync("gatekeeper.respond", new GatekeeperRespondParams(requestId, approved), timeout, ct).ConfigureAwait(false);
        if (!resp.IsSuccess) throw new IOException($"gatekeeper.respond failed: {resp.Error?.Message}");
        return true;
    }

    public async Task<ShellStateResult?> GetShellStateAsync(TimeSpan timeout = default, CancellationToken ct = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(3) : timeout;
        var resp = await SendRequestAsync("shell.getState", null, timeout, ct).ConfigureAwait(false);
        if (!resp.IsSuccess) throw new IOException($"shell.getState failed: {resp.Error?.Message}");
        return resp.GetResult<ShellStateResult>();
    }

    public async Task<bool> SetShellTabAsync(int tabIndex, TimeSpan timeout = default, CancellationToken ct = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(3) : timeout;
        var resp = await SendRequestAsync("shell.setTab", new ShellSetTabParams(tabIndex), timeout, ct).ConfigureAwait(false);
        if (!resp.IsSuccess) throw new IOException($"shell.setTab failed: {resp.Error?.Message}");
        return true;
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposeStarted, 1);
        try
        {
            Task cancellation = _cts.CancelAsync();
            NamedPipeClientStream? stream = _stream;
            _stream = null;
            if (stream is not null)
            {
                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Continue through cancellation and pending-call cleanup.
                }
            }

            try
            {
                await cancellation.ConfigureAwait(false);
            }
            catch
            {
                // Pending calls still need to be failed if a cancellation callback faults.
            }

            if (_readTask is not null)
            {
                try
                {
                    await _readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The read loop is bounded so disconnect cannot hang disposal.
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is expected once the client closes.
                }
            }

            FailPending(new ObjectDisposedException(nameof(CompanionIpcClient)));
        }
        finally
        {
            _cts.Dispose();
        }
    }
}

// Backward Compatibility Adapter: LoadoutIpcServer

public sealed class LoadoutIpcServer : IAsyncDisposable
{
    private readonly CompanionIpcServer _server;
    private readonly CompanionIpcServer _legacyServer;

    public event Action<SoulLoadout>? LoadoutChanged;

    public CompanionIpcServer Server => _server;

    public Task Completion => Task.CompletedTask;

    public LoadoutIpcServer()
    {
        _server = new CompanionIpcServer(IpcConstants.PipeName);
        _legacyServer = new CompanionIpcServer(IpcConstants.LegacyPipeName);

        Func<JsonElement?, CancellationToken, Task<object?>> reloadHandler = (_, _) =>
        {
            var loadout = LoadoutStore.Load();
            LoadoutChanged?.Invoke(loadout);
            return Task.FromResult<object?>(new { success = true });
        };

        _server.RegisterMethod("loadout.reload", reloadHandler);
        _legacyServer.RegisterMethod("loadout.reload", reloadHandler);
    }

    public void Start()
    {
        _server.Start();
        try
        {
            _legacyServer.Start();
        }
        catch
        {
            // If legacy pipe conflicts, proceed with primary pipe
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
        await _legacyServer.DisposeAsync().ConfigureAwait(false);
    }

    public static async Task NotifyLoadoutChangedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string pipeName = IpcConstants.PipeName)
    {
        // Try specified pipe first
        try
        {
            await using var client = new CompanionIpcClient(pipeName);
            await client.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            await client.ReloadLoadoutAsync(timeout, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch
        {
            // Fallback to legacy pipe name if primary failed and using default pipe
            if (pipeName == IpcConstants.PipeName)
            {
                await using var legacyClient = new CompanionIpcClient(IpcConstants.LegacyPipeName);
                await legacyClient.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
                await legacyClient.ReloadLoadoutAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw;
            }
        }
    }
}
