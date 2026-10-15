using System.Diagnostics;
using System.Runtime.InteropServices;
using Timer = System.Threading.Timer;

namespace Companion.Services;

public sealed class GlobalInputService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkAlt = 0x12;
    private const int VkLeftControl = 0xA2;
    private const int VkRightControl = 0xA3;
    private const int VkLeftShift = 0xA0;
    private const int VkRightShift = 0xA1;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;
    private const int VkLeftAlt = 0xA4;
    private const int VkRightAlt = 0xA5;
    private const int VkSpace = 0x20;
    private const int VkBackspace = 0x08;
    private const int VkReturn = 0x0D;
    private const int VkC = 0x43;
    private const int HcAction = 0;

    private static readonly HashSet<int> ModifierKeys =
    [
        VkControl, VkShift, VkAlt, VkLeftControl, VkRightControl,
        VkLeftShift, VkRightShift, VkLeftWindows, VkRightWindows,
        VkLeftAlt, VkRightAlt
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    public void SetPermissionPromptActive(bool active) =>
        Interlocked.Exchange(ref _permissionPromptActive, active ? 1 : 0);

    public static bool IsModifier(int key) => ModifierKeys.Contains(key);

    private delegate IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, KeyboardHookCallback callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private readonly object _sync = new();
    private readonly HashSet<int> _keysDown = [];
    private readonly KeyboardHookCallback _hookCallback;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly Stopwatch _holdClock = new();
    private IntPtr _hook;
    private Timer? _holdTimer;
    private bool _circleActive;
    private bool _dictationActive;
    private bool _circleCancelled;
    private bool _dictationCancelled;
    private bool _circleOcrKeyConsumed;
    private int _permissionPromptActive;
    private volatile bool _disposed;

    public event Action? CircleToAskStarted;
    public event Action? CircleToAskReleased;
    public event Action? CircleToAskOcrRequested;
    public event Action? DictationStarted;
    public event Action? DictationReleased;
    public event Action? AssistantHotkeyPressed;
    public event Action? KillSwitchPressed;
    public event Action? PermissionAllowPressed;
    public event Action? PermissionDenyPressed;

    public GlobalInputService()
    {
        _hookCallback = OnKeyboardEvent;
        _synchronizationContext = SynchronizationContext.Current;
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hook != IntPtr.Zero)
            {
                return;
            }

            _hook = SetWindowsHookEx(WhKeyboardLl, _hookCallback, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                var exception = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                SafeDiagnostics.WriteFailure("input", "hook-start", exception);
                throw exception;
            }

            SafeDiagnostics.WriteEvent("input", "hook-started");
        }
    }

    private IntPtr OnKeyboardEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HcAction || _disposed)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        int message = wParam.ToInt32();
        if (message is not (WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp))
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        int key = unchecked((int)Marshal.PtrToStructure<LowLevelKeyboardData>(lParam).VirtualKey);
        bool isDown = message is WmKeyDown or WmSysKeyDown;
        if (Volatile.Read(ref _permissionPromptActive) == 1 && key is VkReturn or 0x1B)
        {
            if (isDown)
            {
                Dispatch(key == VkReturn ? PermissionAllowPressed : PermissionDenyPressed);
            }

            return new IntPtr(1);
        }

        Action? callback = null;
        bool suppressInput = false;

        lock (_sync)
        {
            if (isDown)
            {
                bool wasDown = !_keysDown.Add(key);
                if (!wasDown)
                {
                    if (key == VkC && IsControlDown() && _circleActive)
                    {
                        _circleOcrKeyConsumed = true;
                        suppressInput = true;
                        callback += CircleToAskOcrRequested;
                        SafeDiagnostics.WriteEvent("input", "circle-ocr-hotkey");
                    }

                    if (!ModifierKeys.Contains(key))
                    {
                        _circleCancelled |= IsCircleChordDown();
                        _dictationCancelled |= IsDictationChordDown();
                        CancelPendingHold();
                    }

                    if (key == VkSpace && IsControlDown() && IsAltDown())
                    {
                        callback = AssistantHotkeyPressed;
                        SafeDiagnostics.WriteEvent("input", "assistant-hotkey");
                    }
                    else if (key == VkBackspace && IsControlDown() && IsAltDown())
                    {
                        callback = KillSwitchPressed;
                        SafeDiagnostics.WriteEvent("input", "kill-switch-hotkey");
                    }

                    UpdateHoldTimer();
                }
            }
            else
            {
                _keysDown.Remove(key);
                if (key == VkC && _circleOcrKeyConsumed)
                {
                    _circleOcrKeyConsumed = false;
                    suppressInput = true;
                }

                bool controlReleased = IsControlKey(key) && !IsControlDown();
                bool shiftReleased = IsShiftKey(key) && !IsShiftDown();
                bool windowsReleased = IsWindowsKey(key) && !IsWindowsDown();
                if (!IsCircleChordDown())
                {
                    _circleCancelled = false;
                }

                if (!IsDictationChordDown())
                {
                    _dictationCancelled = false;
                }

                if (_circleActive && (controlReleased || shiftReleased))
                {
                    _circleActive = false;
                    callback += CircleToAskReleased;
                    SafeDiagnostics.WriteEvent("input", "circle-chord-released");
                }

                if (_dictationActive && (controlReleased || windowsReleased))
                {
                    _dictationActive = false;
                    callback += DictationReleased;
                    SafeDiagnostics.WriteEvent("input", "dictation-chord-released");
                }

                if (!IsCircleChordDown() && !_circleActive && !_dictationActive)
                {
                    CancelPendingHold();
                }
                else
                {
                    UpdateHoldTimer();
                }
            }
        }

        Dispatch(callback);
        return suppressInput
            ? new IntPtr(1)
            : CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void UpdateHoldTimer()
    {
        bool circleDown = IsCircleChordDown();
        bool dictationDown = IsDictationChordDown();
        if (_circleActive || _dictationActive)
        {
            return;
        }

        int delay = circleDown && !_circleCancelled ? 180 :
            dictationDown && !_dictationCancelled ? 150 : 0;
        if (delay == 0)
        {
            CancelPendingHold();
            return;
        }

        _holdTimer?.Dispose();
        _holdClock.Restart();
        _holdTimer = new Timer(OnHoldThreshold, delay, delay, Timeout.Infinite);
    }

    private void OnHoldThreshold(object? state)
    {
        Action? callback = null;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            int requiredDuration = state is int duration ? duration : 0;
            long remaining = requiredDuration - _holdClock.ElapsedMilliseconds;
            if (remaining > 0)
            {
                _holdTimer?.Change((int)remaining, Timeout.Infinite);
                return;
            }

            if (IsCircleChordDown() && !_circleCancelled)
            {
                _circleActive = true;
                callback = CircleToAskStarted;
                SafeDiagnostics.WriteEvent("input", "circle-chord-started");
            }
            else if (IsDictationChordDown() && !_dictationCancelled)
            {
                _dictationActive = true;
                callback = DictationStarted;
                SafeDiagnostics.WriteEvent("input", "dictation-chord-started");
            }
        }

        Dispatch(callback);
    }

    private bool IsControlDown() => AnyDown(VkControl, VkLeftControl, VkRightControl);
    private bool IsAltDown() => AnyDown(VkAlt, VkLeftAlt, VkRightAlt);
    private bool IsShiftDown() => AnyDown(VkShift, VkLeftShift, VkRightShift);
    private bool IsWindowsDown() => AnyDown(VkLeftWindows, VkRightWindows);

    private bool IsCircleChordDown() => IsControlDown() && IsShiftDown();
    private bool IsDictationChordDown() => IsControlDown() && IsWindowsDown();

    private bool IsDown(int key) => _keysDown.Contains(key);

    private bool AnyDown(params int[] keys) => keys.Any(_keysDown.Contains);

    private static bool IsControlKey(int key) => key is VkControl or VkLeftControl or VkRightControl;
    private static bool IsShiftKey(int key) => key is VkShift or VkLeftShift or VkRightShift;
    private static bool IsWindowsKey(int key) => key is VkLeftWindows or VkRightWindows;
    private static bool IsAltKey(int key) => key is VkAlt or VkLeftAlt or VkRightAlt;

    private void CancelPendingHold()
    {
        _holdTimer?.Dispose();
        _holdTimer = null;
        _holdClock.Reset();
    }

    private void Dispatch(Action? callback)
    {
        if (callback is null)
        {
            return;
        }

        if (_synchronizationContext is null)
        {
            callback();
        }
        else
        {
            _synchronizationContext.Post(static state => ((Action)state!).Invoke(), callback);
        }
    }

    public void Dispose()
    {
        IntPtr hook;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelPendingHold();
            _keysDown.Clear();
            hook = _hook;
            _hook = IntPtr.Zero;
        }

        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
        }
    }
}
