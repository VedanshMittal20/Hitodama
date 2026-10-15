using System.Runtime.InteropServices;

namespace Companion.Services;

/// <summary>
/// Reports when the mouse crosses to a different monitor without polling.
/// </summary>
public sealed class MouseMonitorTracker : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int HcAction = 0;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        MouseHookCallback callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private readonly MouseHookCallback _hookCallback;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly object _monitorLock = new();
    private IntPtr _hook;
    private IntPtr _lastMonitor;
    private bool _disposed;

    public event Action<int, int>? MonitorChanged;

    public MouseMonitorTracker()
    {
        _hookCallback = OnMouseEvent;
        _synchronizationContext = SynchronizationContext.Current;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(WhMouseLl, _hookCallback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private IntPtr OnMouseEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HcAction && !_disposed && wParam.ToInt32() == WmMouseMove)
        {
            LowLevelMouseData data = Marshal.PtrToStructure<LowLevelMouseData>(lParam);
            IntPtr monitor = MonitorFromPoint(data.Point, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                bool changed;
                lock (_monitorLock)
                {
                    changed = monitor != _lastMonitor;
                    _lastMonitor = monitor;
                }

                if (changed)
                {
                    Dispatch(() => MonitorChanged?.Invoke(data.Point.X, data.Point.Y));
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Dispatch(Action callback)
    {
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
