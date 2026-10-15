using System.Runtime.InteropServices;
using System.Windows;
using Companion.Core;
using Clipboard = System.Windows.Clipboard;
using IDataObject = System.Windows.IDataObject;
using TextDataFormat = System.Windows.TextDataFormat;

namespace Companion.Services;

public sealed class ClipboardService
{
    public const int MaxUnpinnedHistoryEntries = 10;

    private const int KeyeventfKeyup = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    private readonly IDatabaseService _database;

    public ClipboardService(IDatabaseService database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public long AddTextToHistory(string text) =>
        _database.AddClipboardText(text, MaxUnpinnedHistoryEntries);

    public long AddImageToHistory(byte[] pngBytes) =>
        _database.AddClipboardImage(pngBytes, MaxUnpinnedHistoryEntries);

    public bool SetPinned(long id, bool isPinned) =>
        _database.SetClipboardPinned(id, isPinned, MaxUnpinnedHistoryEntries);

    public bool DeleteEntry(long id) => _database.DeleteClipboardEntry(id);

    public int ClearUnpinnedHistory() => _database.ClearUnpinnedClipboardEntries();

    public static bool IsExcludedFromHistory(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);

        const string excludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
        if (data.GetDataPresent(excludeFormat))
        {
            return true;
        }

        const string canIncludeFormat = "CanIncludeInClipboardHistory";
        if (data.GetDataPresent(canIncludeFormat))
        {
            object? value = data.GetData(canIncludeFormat);
            bool excluded = value switch
            {
                bool include => !include,
                string include when bool.TryParse(include, out bool parsed) => !parsed,
                _ => true
            };

            if (excluded)
            {
                return true;
            }
        }

        return false;
    }

    public static bool PasteToActiveWindow(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Clipboard.SetText(text, TextDataFormat.UnicodeText);

        Input[] inputs =
        [
            KeyInput(VkControl, 0),
            KeyInput(VkV, 0),
            KeyInput(VkV, KeyeventfKeyup),
            KeyInput(VkControl, KeyeventfKeyup)
        ];

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input KeyInput(ushort virtualKey, uint flags) => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags }
        }
    };
}
