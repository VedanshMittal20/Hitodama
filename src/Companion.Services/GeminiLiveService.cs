using System.Net.WebSockets;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Companion.Core;
using Microsoft.Win32;
using Timer = System.Threading.Timer;

namespace Companion.Services;

public sealed class GeminiLiveService : IAsyncDisposable
{
    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    private readonly AudioPlayerService _audioPlayer;
    private readonly ToolRegistry? _toolRegistry;
    private readonly Func<string, JsonElement, string, CancellationToken, Task<string>>? _executeTool;
    private readonly Action<bool>? _trackUntrustedContext;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<byte[]> _audioChunks = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Timer _inactivityTimer;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveTask;
    private Task? _sendTask;
    private TaskCompletionSource? _setupComplete;
    private int _sessionEndedNotified;
    private bool _disposed;
    private bool _sessionSwitchSubscribed;

    public event Action<string>? TranscriptReceived;
    public event Action<bool>? SpeakingChanged;
    public event Action? Interrupted;
    public event Action<Exception>? ConnectionFailed;
    public event Action? SessionEnded;

    public bool IsConnected => _socket?.State == WebSocketState.Open && _setupComplete?.Task.IsCompletedSuccessfully == true;

    public GeminiLiveService(
        AudioPlayerService audioPlayer,
        ToolRegistry? toolRegistry = null,
        Func<string, JsonElement, string, CancellationToken, Task<string>>? executeTool = null,
        Action<bool>? trackUntrustedContext = null)
    {
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _toolRegistry = toolRegistry;
        _executeTool = executeTool;
        _trackUntrustedContext = trackUntrustedContext;
        _inactivityTimer = new Timer(OnInactivityTimeout, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task ConnectAsync(
        string apiKey,
        SoulLoadout loadout,
        IReadOnlyList<MemoryEntry> relevantMemories,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(relevantMemories);
        if (_socket is not null)
        {
            throw new InvalidOperationException("A Gemini Live session is already active.");
        }

        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _setupComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _sessionEndedNotified, 0);
        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        Uri uri = new($"{Endpoint}?key={Uri.EscapeDataString(apiKey)}");
        try
        {
            await _socket.ConnectAsync(uri, _sessionCancellation.Token);
            await SendSetupAsync(loadout, relevantMemories, _sessionCancellation.Token);
            _receiveTask = ReceiveLoopAsync(_sessionCancellation.Token);
            await _setupComplete.Task.WaitAsync(TimeSpan.FromSeconds(15), _sessionCancellation.Token);
            _sendTask = SendAudioLoopAsync(_sessionCancellation.Token);
            ResetInactivityTimer();
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _sessionSwitchSubscribed = true;
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
    }

    private async Task HandleToolCallAsync(JsonElement toolCall, CancellationToken cancellationToken)
    {
        if (!toolCall.TryGetProperty("functionCalls", out JsonElement calls) ||
            calls.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Gemini Live returned a malformed function call.");
        }

        var responses = new List<object>();
        foreach (JsonElement call in calls.EnumerateArray())
        {
            string name = call.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            string id = call.TryGetProperty("id", out JsonElement idElement)
                ? idElement.GetString() ?? string.Empty
                : string.Empty;
            JsonElement arguments = call.TryGetProperty("args", out JsonElement args)
                ? args
                : default;

            object response;
            try
            {
                if (_executeTool is null || string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("Function execution is not configured.");
                }

                string result = await _executeTool(name, arguments, id, cancellationToken);
                response = new { result };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                response = new { error = exception.Message };
            }

            responses.Add(new
            {
                id,
                name,
                response
            });
        }

        await SendJsonAsync(new
        {
            toolResponse = new
            {
                functionResponses = responses
            }
        }, cancellationToken);
    }

    public void SendAudioChunk(byte[] pcmChunk)
    {
        ArgumentNullException.ThrowIfNull(pcmChunk);
        if (!IsConnected)
        {
            return;
        }

        if (!_audioChunks.Writer.TryWrite(pcmChunk))
        {
            ConnectionFailed?.Invoke(new InvalidOperationException("Gemini audio input buffer is full."));
        }
        else
        {
            ResetInactivityTimer();
        }
    }

    public Task SendScreenQuestionAsync(
        byte[] jpegImage,
        byte[] pcmAudio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpegImage);
        ArgumentNullException.ThrowIfNull(pcmAudio);
        if (!IsConnected)
        {
            throw new InvalidOperationException("Gemini Live must be connected before sending a screen question.");
        }

        var mediaChunks = new List<object>
        {
            new
            {
                mimeType = "image/jpeg",
                data = Convert.ToBase64String(jpegImage)
            }
        };
        if (pcmAudio.Length > 0)
        {
            mediaChunks.Add(new
            {
                mimeType = "audio/pcm;rate=16000",
                data = Convert.ToBase64String(pcmAudio)
            });
        }

        return SendJsonAsync(
            new { realtimeInput = new { mediaChunks } },
            cancellationToken);
    }

    private async Task SendSetupAsync(
        SoulLoadout loadout,
        IReadOnlyList<MemoryEntry> memories,
        CancellationToken cancellationToken)
    {
        string memoryContext = memories.Count == 0
            ? string.Empty
            : "\nRelevant user memories:\n" + string.Join(
                "\n",
                memories.Take(5).Select(memory => $"- {memory.Fact}"));
        string systemInstruction = string.Join(
            "\n",
            loadout.PersonalityPrompt,
            $"You are called {loadout.SoulName}. Use enabled ability packs only when corresponding tools are available: {string.Join(", ", loadout.EnabledAbilityPacks)}.",
            memoryContext);
        var setupFields = new Dictionary<string, object>
        {
            ["model"] = loadout.Model.StartsWith("models/", StringComparison.Ordinal)
                ? loadout.Model
                : $"models/{loadout.Model}",
            ["generationConfig"] = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new
                {
                    voiceConfig = new
                    {
                        prebuiltVoiceConfig = new { voiceName = loadout.Voice.ToString() }
                    }
                }
            },
            ["systemInstruction"] = new { parts = new[] { new { text = systemInstruction } } },
            ["inputAudioTranscription"] = new { },
            ["outputAudioTranscription"] = new { }
        };
        IReadOnlyCollection<ToolDefinition> definitions =
            _toolRegistry?.GetDefinitionsForAbilityPacks(loadout.EnabledAbilityPacks) ?? [];
        var tools = new List<object>();
        if (definitions.Count > 0)
        {
            tools.Add(new
            {
                functionDeclarations = definitions.Select(definition => new
                {
                    name = definition.Name,
                    description = definition.Description,
                    parameters = definition.Schema?.RootElement
                }).ToArray()
            });
        }

        if (loadout.EnabledAbilityPacks.Contains("Research"))
        {
            tools.Add(new { googleSearch = new { } });
        }

        if (tools.Count > 0)
        {
            setupFields["tools"] = tools;
        }

        var setup = new Dictionary<string, object> { ["setup"] = setupFields };
        await SendJsonAsync(setup, cancellationToken);
    }

    private async Task SendAudioLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (byte[] chunk in _audioChunks.Reader.ReadAllAsync(cancellationToken))
            {
                if (_socket?.State != WebSocketState.Open)
                {
                    return;
                }

                try
                {
                    var message = new
                    {
                        realtimeInput = new
                        {
                            mediaChunks = new[]
                            {
                                new
                                {
                                    mimeType = "audio/pcm;rate=16000",
                                    data = Convert.ToBase64String(chunk)
                                }
                            }
                        }
                    };
                    await SendJsonAsync(message, cancellationToken);
                }
                finally
                {
                    Array.Clear(chunk);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ConnectionFailed?.Invoke(exception);
            _sessionCancellation?.Cancel();
            _socket?.Abort();
        }
    }

    private async Task SendJsonAsync<T>(T message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        ClientWebSocket socket = _socket
            ?? throw new InvalidOperationException("Gemini Live is not connected.");
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }

        ResetInactivityTimer();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_socket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using JsonDocument? message = await ReceiveJsonAsync(cancellationToken);
                if (message is null)
                {
                    break;
                }

                await ProcessMessageAsync(message.RootElement, cancellationToken);
                ResetInactivityTimer();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _setupComplete?.TrySetException(exception);
            ConnectionFailed?.Invoke(exception);
        }
        finally
        {
            _setupComplete?.TrySetException(new WebSocketException("Gemini Live disconnected before setup completed."));
            RaiseSessionEnded();
        }
    }

    private async Task<JsonDocument?> ReceiveJsonAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket socket = _socket
            ?? throw new InvalidOperationException("Gemini Live is not connected.");
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(message.ToArray());
    }

    private async Task ProcessMessageAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (root.TryGetProperty("setupComplete", out _))
        {
            _setupComplete?.TrySetResult();
            return;
        }

        if (root.TryGetProperty("toolCall", out JsonElement toolCall))
        {
            await HandleToolCallAsync(toolCall, cancellationToken);
            return;
        }

        if (!root.TryGetProperty("serverContent", out JsonElement content))
        {
            if (root.TryGetProperty("error", out JsonElement error))
            {
                throw new InvalidOperationException($"Gemini Live error: {error}");
            }

            return;
        }

        if (content.TryGetProperty("groundingMetadata", out _))
        {
            _trackUntrustedContext?.Invoke(true);
        }

        if (content.TryGetProperty("interrupted", out JsonElement interrupted) && interrupted.GetBoolean())
        {
            _audioPlayer.FlushImmediate();
            Interrupted?.Invoke();
        }

        if (content.TryGetProperty("inputTranscription", out JsonElement inputTranscript) &&
            inputTranscript.TryGetProperty("text", out JsonElement inputText))
        {
            string? transcript = inputText.GetString();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                TranscriptReceived?.Invoke(transcript);
            }
        }

        if (content.TryGetProperty("outputTranscription", out JsonElement outputTranscript) &&
            outputTranscript.TryGetProperty("text", out JsonElement outputText))
        {
            string? transcript = outputText.GetString();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                TranscriptReceived?.Invoke(transcript);
            }
        }

        if (content.TryGetProperty("modelTurn", out JsonElement modelTurn) &&
            modelTurn.TryGetProperty("parts", out JsonElement parts))
        {
            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("inlineData", out JsonElement inlineData) ||
                    !inlineData.TryGetProperty("data", out JsonElement dataElement))
                {
                    continue;
                }

                byte[] pcm = Convert.FromBase64String(dataElement.GetString() ?? string.Empty);
                if (pcm.Length > 0)
                {
                    _audioPlayer.EnqueuePcm(pcm);
                    SpeakingChanged?.Invoke(true);
                }
            }
        }

        if (content.TryGetProperty("turnComplete", out JsonElement complete) && complete.GetBoolean())
        {
            SpeakingChanged?.Invoke(false);
        }
    }

    private void ResetInactivityTimer()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _inactivityTimer.Change(TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnInactivityTimeout(object? state)
    {
        _socket?.Abort();
        _sessionCancellation?.Cancel();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _socket?.Abort();
            _sessionCancellation?.Cancel();
        }
    }

    private void RaiseSessionEnded()
    {
        if (Interlocked.Exchange(ref _sessionEndedNotified, 1) == 0)
        {
            SessionEnded?.Invoke();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_sessionSwitchSubscribed)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _sessionSwitchSubscribed = false;
        }

        _inactivityTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _sessionCancellation?.Cancel();
        _audioChunks.Writer.TryComplete();
        _socket?.Abort();

        if (_receiveTask is not null)
        {
            await _receiveTask;
            _receiveTask = null;
        }

        if (_sendTask is not null)
        {
            try
            {
                await _sendTask;
            }
            catch (OperationCanceledException)
            {
            }

            _sendTask = null;
        }

        _socket?.Dispose();
        _socket = null;
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        _setupComplete = null;
        _audioPlayer.FlushImmediate();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync();
        _inactivityTimer.Dispose();
        _audioChunks.Writer.TryComplete();
        _sendLock.Dispose();
    }
}
