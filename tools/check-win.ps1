using namespace System.Runtime.InteropServices

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WinInspector {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static void Inspect(int pid) {
        EnumWindows((hWnd, lParam) => {
            uint procId;
            GetWindowThreadProcessId(hWnd, out procId);
            if (procId == pid) {
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                bool visible = IsWindowVisible(hWnd);
                RECT r;
                GetWindowRect(hWnd, out r);
                Console.WriteLine("HWND: " + hWnd + " | Visible: " + visible + " | Title: '" + sb.ToString() + "' | Rect: (" + r.Left + "," + r.Top + ") " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top));
            }
            return true;
        }, IntPtr.Zero);
    }
}
'@

$proc = Get-Process -Name Companion.Shell -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc) {
    Write-Host "Found Companion.Shell PID: $($proc.Id)"
    [WinInspector]::Inspect($proc.Id)
} else {
    Write-Host "Companion.Shell is not running."
}
