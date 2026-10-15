using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Companion.Services;

public sealed class DesktopAutomationService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;
    private const int MaximumTextLength = 4000;
    private const int MaximumTreeDepth = 6;
    private const int MaximumElements = 500;
    private const int MaximumOutputLength = 4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    public IntPtr GetActiveWindowHandle()
    {
        IntPtr handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("There is no active window to inspect.");
        }

        if (AutomationElement.FromHandle(handle).Current.ProcessId == Environment.ProcessId)
        {
            throw new InvalidOperationException("Void cannot inspect or control its own window.");
        }

        return handle;
    }

    public string ReadActiveWindow()
    {
        AutomationElement root = GetActiveRoot();
        var output = new StringBuilder();
        int visited = 0;
        AppendElement(root, depth: 0, output, ref visited);
        return output.Length == 0 ? "The active window exposed no readable controls." : output.ToString();
    }

    public void ClickElement(string elementName, IntPtr expectedWindow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementName);
        AutomationElement root = GetActiveRoot(expectedWindow);
        List<AutomationElement> matches = FindElements(root, elementName.Trim());
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"No clickable UI element named '{elementName}' was found.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"More than one clickable UI element is named '{elementName}'. Provide a more specific name.");
        }

        if (!matches[0].TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
        {
            throw new InvalidOperationException($"UI element '{elementName}' does not support Invoke.");
        }

        ((InvokePattern)pattern).Invoke();
    }

    public void TypeIntoActiveWindow(string text, bool pressEnter, IntPtr expectedWindow)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is 0 or > MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(text), $"Text must be between 1 and {MaximumTextLength} characters.");
        }

        EnsureForegroundWindow(expectedWindow);
        if (expectedWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("There is no active window to type into.");
        }

        var inputs = new List<NativeInput>(text.Length * 2 + (pressEnter ? 2 : 0));
        foreach (char character in text)
        {
            inputs.Add(CreateUnicodeInput(character, keyUp: false));
            inputs.Add(CreateUnicodeInput(character, keyUp: true));
        }

        if (pressEnter)
        {
            inputs.Add(new NativeInput
            {
                Type = InputKeyboard,
                Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0x0D } }
            });
            inputs.Add(new NativeInput
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput { VirtualKey = 0x0D, Flags = KeyEventKeyUp }
                }
            });
        }

        NativeInput[] nativeInputs = inputs.ToArray();
        uint sent = SendInput((uint)nativeInputs.Length, nativeInputs, Marshal.SizeOf<NativeInput>());
        if (sent != nativeInputs.Length)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static NativeInput CreateUnicodeInput(char character, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                ScanCode = character,
                Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0)
            }
        }
    };

    private AutomationElement GetActiveRoot(IntPtr? expectedWindow = null)
    {
        IntPtr handle = expectedWindow ?? GetActiveWindowHandle();
        EnsureForegroundWindow(handle);

        AutomationElement root = AutomationElement.FromHandle(handle);
        if (root.Current.ProcessId == Environment.ProcessId)
        {
            throw new InvalidOperationException("Void cannot inspect or control its own window.");
        }

        return root;
    }

    private static void EnsureForegroundWindow(IntPtr expectedWindow)
    {
        if (expectedWindow == IntPtr.Zero || GetForegroundWindow() != expectedWindow)
        {
            throw new InvalidOperationException(
                "The active window changed before approval completed. Retry from the intended application.");
        }
    }

    private static void AppendElement(
        AutomationElement element,
        int depth,
        StringBuilder output,
        ref int visited)
    {
        if (visited++ >= MaximumElements || output.Length >= MaximumOutputLength)
        {
            return;
        }

        AutomationElement.AutomationElementInformation info = element.Current;
        string name = info.IsPassword ? "[password field]" : info.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (output.Length > 0)
            {
                output.AppendLine();
            }

            output.Append(' ', depth * 2)
                .Append(info.ControlType.ProgrammaticName)
                .Append(": ")
                .Append(name);
            if (output.Length > MaximumOutputLength)
            {
                output.Length = MaximumOutputLength;
            }
        }

        if (depth >= MaximumTreeDepth || output.Length >= MaximumOutputLength)
        {
            return;
        }

        for (AutomationElement? child = TreeWalker.RawViewWalker.GetFirstChild(element);
             child is not null && visited < MaximumElements && output.Length < MaximumOutputLength;
             child = TreeWalker.RawViewWalker.GetNextSibling(child))
        {
            AppendElement(child, depth + 1, output, ref visited);
        }
    }

    private static List<AutomationElement> FindElements(AutomationElement root, string name)
    {
        var matches = new List<AutomationElement>();
        var pending = new Stack<(AutomationElement Element, int Depth)>();
        pending.Push((root, 0));
        int visited = 0;
        while (pending.Count > 0 && visited < MaximumElements)
        {
            (AutomationElement current, int depth) = pending.Pop();
            visited++;
            AutomationElement.AutomationElementInformation info = current.Current;
            if (!info.IsPassword &&
                string.Equals(info.Name, name, StringComparison.CurrentCultureIgnoreCase) &&
                info.ControlType is { } type &&
                (type == ControlType.Button || type == ControlType.Hyperlink))
            {
                matches.Add(current);
                if (matches.Count > 1)
                {
                    return matches;
                }
            }

            if (depth >= MaximumTreeDepth)
            {
                continue;
            }

            for (AutomationElement? child = TreeWalker.RawViewWalker.GetFirstChild(current);
                 child is not null;
                 child = TreeWalker.RawViewWalker.GetNextSibling(child))
            {
                pending.Push((child, depth + 1));
            }
        }

        return matches;
    }
}
