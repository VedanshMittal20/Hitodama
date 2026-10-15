using System.Diagnostics;
using System.Net.WebSockets;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Companion.Core;
using Microsoft.Win32;
using Timer = System.Threading.Timer;

namespace Companion.Services;

public interface IGeminiLiveTranscriptEvents
{
    event Action<string, bool>? UserTranscriptReceived;
    event Action<string, bool>? AssistantTranscriptReceived;
}

public sealed class GeminiLiveService : IAsyncDisposable, IGeminiLiveTranscriptEvents
{
    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    private readonly AudioPlayerService _audioPlayer;
    private readonly ToolRegistry? _toolRegistry;
    private readonly Func<string, JsonElement, string, CancellationToken, Task<string>>? _executeTool;
    private readonly Action<bool>? _trackUntrustedContext;
    private readonly GeminiVisionService? _visionFallback;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<AudioInputMessage> _audioChunks = Channel.CreateBounded<AudioInputMessage>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Timer _inactivityTimer;
    private readonly StringBuilder _assistantTranscriptBuffer = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveTask;
    private Task? _sendTask;
    private TaskCompletionSource? _setupComplete;
    private int _sessionEndedNotified;
    private int _audioInputFailureReported;
    private bool _disposed;
    private bool _sessionSwitchSubscribed;
    private event Action<string, bool>? UserTranscriptWithFinality;
    private event Action<string, bool>? AssistantTranscriptWithFinality;

    public event Action<string>? InputTranscriptReceived;
    public event Action<string>? OutputTranscriptReceived;
    // One-argument events remain for the existing shell contract.
    public event Action<string>? UserTranscriptReceived;
    public event Action<string>? AssistantTranscriptReceived;
    public event Action<string>? TranscriptReceived;
    public event Action<bool>? SpeakingChanged;
    public event Action? Interrupted;
    public event Action<Exception>? ConnectionFailed;
    public event Action? SessionEnded;
    public IGeminiLiveTranscriptEvents TranscriptEvents => this;

    event Action<string, bool>? IGeminiLiveTranscriptEvents.UserTranscriptReceived
    {
        add => UserTranscriptWithFinality += value;
        remove => UserTranscriptWithFinality -= value;
    }

    event Action<string, bool>? IGeminiLiveTranscriptEvents.AssistantTranscriptReceived
    {
        add => AssistantTranscriptWithFinality += value;
        remove => AssistantTranscriptWithFinality -= value;
    }

    private sealed record AudioInputMessage(byte[]? PcmBytes, bool IsStreamEnd);

    public bool IsConnected => _socket?.State == WebSocketState.Open && _setupComplete?.Task.IsCompletedSuccessfully == true;

    public GeminiLiveService(
        AudioPlayerService audioPlayer,
        ToolRegistry? toolRegistry = null,
        Func<string, JsonElement, string, CancellationToken, Task<string>>? executeTool = null,
        Action<bool>? trackUntrustedContext = null,
        GeminiVisionService? visionFallback = null)
    {
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _toolRegistry = toolRegistry;
        _executeTool = executeTool;
        _trackUntrustedContext = trackUntrustedContext;
        _visionFallback = visionFallback;
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
        Interlocked.Exchange(ref _audioInputFailureReported, 0);
        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        Uri uri = new($"{Endpoint}?key={Uri.EscapeDataString(apiKey)}");
        var connectionClock = Stopwatch.StartNew();
        SafeDiagnostics.WriteEvent("live", "connect-started");
        try
        {
            await _socket.ConnectAsync(uri, _sessionCancellation.Token);
            SafeDiagnostics.WriteEvent("live", "websocket-connected");
            await SendSetupAsync(loadout, relevantMemories, _sessionCancellation.Token);
            _receiveTask = ReceiveLoopAsync(_sessionCancellation.Token);
            await _setupComplete.Task.WaitAsync(TimeSpan.FromSeconds(10), _sessionCancellation.Token);
            SafeDiagnostics.WriteMetric("live", "setup-milliseconds", connectionClock.ElapsedMilliseconds);
            SafeDiagnostics.WriteEvent("live", "setup-complete");
            _sendTask = SendAudioLoopAsync(_sessionCancellation.Token);
            ResetInactivityTimer();
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _sessionSwitchSubscribed = true;
        }
        catch (Exception exception)
        {
            SafeDiagnostics.WriteFailure("live", "connect", exception);
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
            CryptographicOperations.ZeroMemory(pcmChunk);
            return;
        }

        if (!_audioChunks.Writer.TryWrite(new AudioInputMessage(pcmChunk, IsStreamEnd: false)))
        {
            CryptographicOperations.ZeroMemory(pcmChunk);
            if (Interlocked.Exchange(ref _audioInputFailureReported, 1) == 0)
            {
                SafeDiagnostics.WriteEvent("live", "audio-input-buffer-full");
                ConnectionFailed?.Invoke(new InvalidOperationException("Gemini audio input buffer is full."));
            }
        }
        else
        {
            Interlocked.Exchange(ref _audioInputFailureReported, 0);
            ResetInactivityTimer();
        }
    }

    public void EndAudioStream()
    {
        if (!IsConnected)
        {
            return;
        }

        if (!_audioChunks.Writer.TryWrite(new AudioInputMessage(null, IsStreamEnd: true)))
        {
            SafeDiagnostics.WriteEvent("live", "audio-end-buffer-full");
            ConnectionFailed?.Invoke(new InvalidOperationException("Gemini audio input buffer is full."));
            return;
        }

        SafeDiagnostics.WriteEvent("live", "audio-stream-ended");
    }

    public async Task SendScreenQuestionAsync(
        byte[] jpegImage,
        byte[] pcmAudio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpegImage);
        ArgumentNullException.ThrowIfNull(pcmAudio);
        await SendCircleToAskTurnAsync(jpegImage, pcmAudio, cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> SendCircleToAskTurnAsync(
        byte[] jpegBytes,
        byte[] pcm16kHzBytes,
        CancellationToken ct) =>
        SendCircleToAskTurnAsync(jpegBytes, pcm16kHzBytes, ocrContext: null, ct: ct);

    public async Task<string?> SendCircleToAskTurnAsync(
        byte[] jpegBytes,
        byte[] pcm16kHzBytes,
        string? ocrContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jpegBytes);
        ArgumentNullException.ThrowIfNull(pcm16kHzBytes);
        if (jpegBytes.Length == 0 || pcm16kHzBytes.Length == 0)
        {
            throw new ArgumentException("A screen image and recorded question are required.");
        }

        if (IsConnected)
        {
            try
            {
                await SendCircleToAskLiveTurnAsync(jpegBytes, pcm16kHzBytes, ct).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                SafeDiagnostics.LogError("live", "Circle to Ask WebSocket failed; using Vision REST fallback.", exception);
            }
        }

        GeminiVisionService vision = _visionFallback ?? new GeminiVisionService();
        using var waveStream = new MemoryStream();
        using (var writer = new NAudio.Wave.WaveFileWriter(waveStream, new NAudio.Wave.WaveFormat(16000, 16, 1)))
        {
            writer.Write(pcm16kHzBytes, 0, pcm16kHzBytes.Length);
        }

        string answer = await vision.AskAboutScreenAsync(jpegBytes, waveStream.ToArray(), ocrContext, ct).ConfigureAwait(false);
        OutputTranscriptReceived?.Invoke(answer);
        AssistantTranscriptReceived?.Invoke(answer);
        AssistantTranscriptWithFinality?.Invoke(answer, true);
        TranscriptReceived?.Invoke(answer);
        return answer;
    }

    private async Task SendCircleToAskLiveTurnAsync(
        byte[] jpegBytes,
        byte[] pcm16kHzBytes,
        CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Gemini Live must complete setup before sending a screen question.");
        }

        await SendJsonAsync(
            new
            {
                realtimeInput = new
                {
                    video = new
                    {
                        mimeType = "image/jpeg",
                        data = Convert.ToBase64String(jpegBytes)
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        // 3,200 bytes is 100 ms of 16 kHz, 16-bit mono PCM. Smaller frames keep
        // each realtime payload modest while preserving every captured sample.
        const int audioFrameBytes = 3200;
        for (int offset = 0; offset < pcm16kHzBytes.Length; offset += audioFrameBytes)
        {
            int length = Math.Min(audioFrameBytes, pcm16kHzBytes.Length - offset);
            byte[] frame = pcm16kHzBytes.AsMemory(offset, length).ToArray();
            try
            {
                await SendJsonAsync(
                    new
                    {
                        realtimeInput = new
                        {
                            audio = new
                            {
                                mimeType = "audio/pcm;rate=16000",
                                data = Convert.ToBase64String(frame)
                            }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(frame);
            }
        }

        await SendJsonAsync(new { realtimeInput = new { audioStreamEnd = true } }, cancellationToken);
        await SendJsonAsync(new { clientContent = new { turnComplete = true } }, cancellationToken).ConfigureAwait(false);
        SafeDiagnostics.WriteMetric("live", "circle-image-bytes", jpegBytes.Length);
        SafeDiagnostics.WriteMetric("live", "circle-pcm-bytes", pcm16kHzBytes.Length);
        SafeDiagnostics.WriteEvent("live", "circle-question-sent");
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
        await SendJsonAsync(setup, cancellationToken, isSetupMessage: true);
    }

    private async Task SendAudioLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (AudioInputMessage input in _audioChunks.Reader.ReadAllAsync(cancellationToken))
            {
                if (_socket?.State != WebSocketState.Open)
                {
                    if (input.PcmBytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(input.PcmBytes);
                    }

                    ClearQueuedAudio();
                    return;
                }

                try
                {
                    if (input.IsStreamEnd)
                    {
                        await SendJsonAsync(new { realtimeInput = new { audioStreamEnd = true } }, cancellationToken);
                        continue;
                    }

                    byte[] chunk = input.PcmBytes ?? [];
                    if (chunk.Length == 0)
                    {
                        continue;
                    }

                    await SendJsonAsync(
                        new
                        {
                            realtimeInput = new
                            {
                                audio = new
                                {
                                    mimeType = "audio/pcm;rate=16000",
                                    data = Convert.ToBase64String(chunk)
                                }
                            }
                        },
                        cancellationToken);
                }
                finally
                {
                    if (input.PcmBytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(input.PcmBytes);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SafeDiagnostics.WriteFailure("live", "audio-send", exception);
            ConnectionFailed?.Invoke(exception);
            _sessionCancellation?.Cancel();
            _socket?.Abort();
            ClearQueuedAudio();
        }
    }

    private async Task SendJsonAsync<T>(T message, CancellationToken cancellationToken, bool isSetupMessage = false)
    {
        if (!isSetupMessage)
        {
            Task setupTask = _setupComplete?.Task
                ?? throw new InvalidOperationException("Gemini Live setup has not started.");
            await setupTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }

        ClientWebSocket socket = _socket
            ?? throw new InvalidOperationException("Gemini Live is not connected.");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        try
        {
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
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }

        ResetInactivityTimer();
    }

    private void ClearQueuedAudio()
    {
        while (_audioChunks.Reader.TryRead(out AudioInputMessage? input))
        {
            if (input?.PcmBytes is not null)
            {
                CryptographicOperations.ZeroMemory(input.PcmBytes);
            }
        }
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
            SafeDiagnostics.WriteFailure("live", "receive", exception);
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

        bool inputIsFinal = content.TryGetProperty("inputTranscription", out JsonElement inputTranscript);
        if (!inputIsFinal)
        {
            content.TryGetProperty("interimInputTranscription", out inputTranscript);
        }

        if (inputTranscript.ValueKind == JsonValueKind.Object &&
            inputTranscript.TryGetProperty("text", out JsonElement inputText))
        {
            string? transcript = inputText.GetString();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                InputTranscriptReceived?.Invoke(transcript);
                UserTranscriptReceived?.Invoke(transcript);
                UserTranscriptWithFinality?.Invoke(transcript, inputIsFinal);
                TranscriptReceived?.Invoke(transcript);
            }
        }

        bool assistantIsFinal = content.TryGetProperty("turnComplete", out JsonElement turnComplete) &&
                                turnComplete.ValueKind == JsonValueKind.True;
        bool assistantTranscriptReceived = false;
        if (content.TryGetProperty("outputTranscription", out JsonElement outputTranscript) &&
            outputTranscript.TryGetProperty("text", out JsonElement outputText))
        {
            string? transcript = outputText.GetString();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                _assistantTranscriptBuffer.Append(transcript);
                assistantTranscriptReceived = true;
                string currentTranscript = _assistantTranscriptBuffer.ToString();
                OutputTranscriptReceived?.Invoke(transcript);
                AssistantTranscriptReceived?.Invoke(currentTranscript);
                AssistantTranscriptWithFinality?.Invoke(currentTranscript, assistantIsFinal);
                TranscriptReceived?.Invoke(transcript);
                if (assistantIsFinal)
                {
                    _assistantTranscriptBuffer.Clear();
                }
            }
        }

        if (assistantIsFinal && !assistantTranscriptReceived && _assistantTranscriptBuffer.Length > 0)
        {
            string completedTranscript = _assistantTranscriptBuffer.ToString();
            AssistantTranscriptReceived?.Invoke(completedTranscript);
            AssistantTranscriptWithFinality?.Invoke(completedTranscript, true);
            TranscriptReceived?.Invoke(completedTranscript);
            _assistantTranscriptBuffer.Clear();
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
                    try
                    {
                        _audioPlayer.EnqueuePcm(pcm);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(pcm);
                    }

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
            // A concurrent disconnect has already disposed the timer.
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
                // Disconnect intentionally cancels the send loop.
            }

            _sendTask = null;
        }

        ClearQueuedAudio();

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
