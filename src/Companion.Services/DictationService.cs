using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using System.Windows;
using Companion.Core;
using Clipboard = System.Windows.Clipboard;
using IDataObject = System.Windows.IDataObject;
using TextDataFormat = System.Windows.TextDataFormat;

namespace Companion.Services;

public sealed class DictationService : IDisposable
{
    private const string CleanupInstruction =
        "You are an intelligent voice dictation cleanup engine. Transcribe the user's speech into clean, natural written text. Remove all filler words (um, uh, like), false starts, stutters, and self-corrections (e.g., if the user says 'meet at 9, wait no, 10', output only 'meet at 10'). Fix punctuation and capitalization. Output ONLY the final cleaned text with zero commentary or quotes.";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private readonly MicCaptureService _microphone = new();
    private readonly DatabaseService _database;
    private CancellationTokenSource? _activeOperation;
    private bool _isRecording;
    private IntPtr _targetWindow;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    public event Action<double>? AudioLevelChanged;

    public DictationService(DatabaseService? database = null)
    {
        _database = database ?? new DatabaseService();
        _microphone.AudioLevelChanged += level => AudioLevelChanged?.Invoke(level);
    }

    public bool IsRecording => _isRecording;

    public void Start()
    {
        if (_isRecording)
        {
            return;
        }

        _targetWindow = GetForegroundWindow();
        if (_targetWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("There is no active window to receive dictated text.");
        }

        _microphone.StartRecording();
        _isRecording = true;
    }

    public async Task<string> StopAndTypeAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Dictation capture is not active.");
        }

        _isRecording = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeOperation = operation;
        try
        {
            byte[] audio = await _microphone.StopRecordingAsWaveAsync(operation.Token);
            if (audio.Length <= 44)
            {
                throw new InvalidOperationException("No audio was captured. Check the microphone and try again.");
            }

            string apiKey = SecretStore.LoadApiKey()
                ?? throw new InvalidOperationException("Add a Gemini API key in Void Studio before using dictation.");
            string cleanedText = await TranscribeAndCleanAsync(apiKey, audio, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(cleanedText))
            {
                throw new InvalidOperationException("Gemini returned no dictation text.");
            }

            await TypeTextAsync(cleanedText);
            long words = cleanedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength;
            _database.IncrementStat("dictated_words", words);
            return cleanedText;
        }
        finally
        {
            _activeOperation = null;
        }
    }

    public async Task CancelAsync()
    {
        _activeOperation?.Cancel();
        if (_isRecording)
        {
            _isRecording = false;
            await _microphone.CancelRecordingAsync();
        }
    }

    private static async Task<string> TranscribeAndCleanAsync(
        string apiKey,
        byte[] audio,
        CancellationToken cancellationToken)
    {
        string endpoint =
            $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var payload = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = CleanupInstruction } }
            },
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { inline_data = new { mime_type = "audio/wav", data = Convert.ToBase64String(audio) } }
                    }
                }
            }
        };

        using var response = await HttpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Gemini dictation request failed ({(int)response.StatusCode}): {responseText}",
                null,
                response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.GetArrayLength() == 0 ||
            !candidates[0].TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("parts", out JsonElement parts))
        {
            throw new InvalidDataException("Gemini returned a response without dictation content.");
        }

        return string.Join(
            Environment.NewLine,
            parts.EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text)))
            .Trim();
    }

    private async Task TypeTextAsync(string text)
    {
        IDataObject? previousClipboard = Clipboard.GetDataObject();
        bool clipboardChanged = false;
        try
        {
            Clipboard.SetText(text, TextDataFormat.UnicodeText);
            clipboardChanged = true;

            if (!SetForegroundWindow(_targetWindow) || GetForegroundWindow() != _targetWindow)
            {
                throw new InvalidOperationException("The original target window is no longer available for dictation.");
            }

            var inputs = new[]
            {
                KeyInput(0x11, 0),
                KeyInput(0x56, 0),
                KeyInput(0x56, 0x0002),
                KeyInput(0x11, 0x0002)
            };
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (sent != inputs.Length)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (clipboardChanged)
            {
                await Task.Delay(300);
                if (previousClipboard is null)
                {
                    Clipboard.Clear();
                }
                else
                {
                    Clipboard.SetDataObject(previousClipboard, true);
                }
            }
        }
    }

    private static Input KeyInput(ushort virtualKey, uint flags) => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = flags
            }
        }
    };

    public void Dispose()
    {
        _activeOperation?.Cancel();
        _microphone.Dispose();
    }
}
