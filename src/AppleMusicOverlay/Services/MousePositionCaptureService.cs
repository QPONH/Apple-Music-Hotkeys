using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppleMusicOverlay.Services;

/// <summary>Captures the next left-click on an Apple Music window without forwarding that click.</summary>
public sealed class MousePositionCaptureService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    private readonly HookProc _proc;
    private GCHandle _procRoot;
    private IntPtr _hook;
    private bool _disposed;
    private readonly TaskCompletionSource<(int x, int y)> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MousePositionCaptureService()
    {
        _proc = HookCallback;
        _procRoot = GCHandle.Alloc(_proc, GCHandleType.Normal);
        try
        {
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero)
                throw new InvalidOperationException($"无法安装鼠标捕获钩子，Win32 错误码：{Marshal.GetLastWin32Error()}。");
        }
        catch
        {
            if (_procRoot.IsAllocated) _procRoot.Free();
            throw;
        }
    }
    public Task<(int x, int y)> Result => _tcs.Task;
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                IntPtr child = WindowFromPoint(data.pt);
                IntPtr hwnd = child == IntPtr.Zero ? IntPtr.Zero : GetAncestor(child, 2); // GA_ROOT
                if (IsAppleMusicWindow(hwnd) && GetClientRect(hwnd, out RECT r) && r.Right > 0 && r.Bottom > 0)
                {
                    POINT client = data.pt;
                    ScreenToClient(hwnd, ref client);
                    _tcs.TrySetResult((Math.Clamp(client.X * 100 / r.Right, 0, 100), Math.Clamp(client.Y * 100 / r.Bottom, 0, 100)));
                    return (IntPtr)1;
                }
            }
        }
        catch
        {
            // Never allow a managed exception to escape through the native hook callback.
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
    private static bool IsAppleMusicWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid); if (pid == 0) return false;
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("Apple Music", StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        bool removed = _hook == IntPtr.Zero || UnhookWindowsHookEx(_hook);
        if (removed) _hook = IntPtr.Zero;
        // Only release the delegate after Windows confirms the hook is gone.
        if (removed && _procRoot.IsAllocated) _procRoot.Free();

        GC.SuppressFinalize(this);
    }
}
