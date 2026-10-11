using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppleMusicOverlay.Services;

/// <summary>Waits for Apple Music and sends background window messages without moving the user's pointer.</summary>
public sealed class StartupPlaybackRecoveryService
{
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const int SW_RESTORE = 9;
    private const byte VK_MENU = 0x12, VK_L = 0x4C, VK_Q = 0x51, VK_TAB = 0x09, VK_RETURN = 0x0D;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
<<<<<<< HEAD
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
=======
>>>>>>> 9c4da6fa662428a2fa5a53422d751ceae9329a8a
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    public async Task RunAsync(int queueXPercent, int queueYPercent, int songXPercent, int songYPercent, string queueMethod = "Keyboard", string songMethod = "Mouse", int waitSeconds = 2, CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 600)), cancellationToken);
        // Bounded polling: at most 24 checks (about 2 minutes), and cancellable by the UI.
        const int maxChecks = 24;
        for (int attempt = 0; attempt < maxChecks; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IntPtr hwnd = FindAppleMusicWindow();
            if (hwnd != IntPtr.Zero)
            {
                await Task.Delay(800, cancellationToken);
                await RunOnceAsync(hwnd, queueXPercent, queueYPercent, songXPercent, songYPercent, queueMethod, songMethod, 0, cancellationToken);
                return;
            }
            if (attempt < maxChecks - 1)
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
        throw new TimeoutException("等待 Apple Music 窗口超时（约 2 分钟）。可以启动 Apple Music 后点击“检查（运行一次）”重试。");
    }

    public async Task RunOnceAsync(int queueXPercent, int queueYPercent, int songXPercent, int songYPercent, string queueMethod = "Keyboard", string songMethod = "Mouse", int waitSeconds = 2, CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 600)), cancellationToken);
        IntPtr hwnd = FindAppleMusicWindow();
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("没有找到 Apple Music 窗口。请先启动 Apple Music，并确保主窗口未关闭。");
        await RunOnceAsync(hwnd, queueXPercent, queueYPercent, songXPercent, songYPercent, queueMethod, songMethod, 0, cancellationToken);
    }

    private static async Task RunOnceAsync(IntPtr hwnd, int queueXPercent, int queueYPercent, int songXPercent, int songYPercent, string queueMethod, string songMethod, int waitSeconds, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 600)), token);
        token.ThrowIfCancellationRequested();
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        ActivateTargetWindow(hwnd);
<<<<<<< HEAD
        await WaitForTargetForegroundAsync(hwnd, token);
        if (queueMethod == "Keyboard") await OpenQueueByKeyboardAsync(hwnd, token);
        else await ClickAtPercentAsync(hwnd, queueXPercent, queueYPercent, token);
        await Task.Delay(700, token);
        EnsureTargetForeground(hwnd);
        if (songMethod == "Keyboard") await SelectFirstSongByKeyboardAsync(hwnd, token);
=======
        await Task.Delay(900, token);
        if (queueMethod == "Keyboard") await OpenQueueByKeyboardAsync(token);
        else await ClickAtPercentAsync(hwnd, queueXPercent, queueYPercent, token);
        await Task.Delay(700, token);
        if (songMethod == "Keyboard") await SelectFirstSongByKeyboardAsync(token);
>>>>>>> 9c4da6fa662428a2fa5a53422d751ceae9329a8a
        else
        {
            await ClickAtPercentAsync(hwnd, songXPercent, songYPercent, token);
            await Task.Delay(120, token);
            await ClickAtPercentAsync(hwnd, songXPercent, songYPercent, token);
        }
    }

<<<<<<< HEAD
    private static async Task OpenQueueByKeyboardAsync(IntPtr hwnd, CancellationToken token)
    {
        EnsureTargetForeground(hwnd);
        KeyDown(VK_MENU); await Task.Delay(100, token); KeyUp(VK_MENU);
        await Task.Delay(180, token);
        EnsureTargetForeground(hwnd); PressKey(VK_L);
        await Task.Delay(180, token);
        EnsureTargetForeground(hwnd); PressKey(VK_Q);
        await Task.Delay(250, token);
    }

    private static async Task SelectFirstSongByKeyboardAsync(IntPtr hwnd, CancellationToken token)
=======
    private static async Task OpenQueueByKeyboardAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Reproduce the user's Alt, L, Q sequence as separate key presses.
        KeyDown(VK_MENU); await Task.Delay(100, token); KeyUp(VK_MENU);
        await Task.Delay(180, token);
        PressKey(VK_L); await Task.Delay(180, token);
        PressKey(VK_Q); await Task.Delay(250, token);
    }

    private static async Task SelectFirstSongByKeyboardAsync(CancellationToken token)
>>>>>>> 9c4da6fa662428a2fa5a53422d751ceae9329a8a
    {
        for (int i = 0; i < 31; i++)
        {
            token.ThrowIfCancellationRequested();
<<<<<<< HEAD
            EnsureTargetForeground(hwnd);
            PressKey(VK_TAB);
            await Task.Delay(35, token);
        }
        EnsureTargetForeground(hwnd);
        PressKey(VK_RETURN);
    }

    private static async Task WaitForTargetForegroundAsync(IntPtr hwnd, CancellationToken token)
    {
        for (int i = 0; i < 10; i++)
        {
            token.ThrowIfCancellationRequested();
            if (IsTargetForeground(hwnd)) return;
            ActivateTargetWindow(hwnd);
            await Task.Delay(250, token);
        }
        throw new InvalidOperationException("无法将 Apple Music 切换到前台。为避免误操作 WinUI Desktop 或其他窗口，已中止自动播放。请先手动切换到 Apple Music，再点击检查。 ");
    }

    private static void EnsureTargetForeground(IntPtr hwnd)
    {
        if (!IsTargetForeground(hwnd))
            throw new InvalidOperationException("当前前台窗口已不是 Apple Music。为避免向其他窗口发送按键，自动播放已中止。");
    }

    private static bool IsTargetForeground(IntPtr hwnd)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint targetPid);
        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        return targetPid != 0 && targetPid == foregroundPid;
    }

=======
            PressKey(VK_TAB);
            await Task.Delay(35, token);
        }
        PressKey(VK_RETURN);
    }

>>>>>>> 9c4da6fa662428a2fa5a53422d751ceae9329a8a
    private static void PressKey(byte key) { KeyDown(key); Thread.Sleep(25); KeyUp(key); }
    private static void KeyDown(byte key) => keybd_event(key, 0, 0, UIntPtr.Zero);
    private static void KeyUp(byte key) => keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

    private static void ActivateTargetWindow(IntPtr hwnd)
    {
        // Attach input queues briefly so Windows is more likely to honor foreground activation.
        IntPtr foreground = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        bool attachedTarget = targetThread != 0 && targetThread != currentThread && AttachThreadInput(currentThread, targetThread, true);
        bool attachedForeground = foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
        }
        finally
        {
            if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
            if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
        }
    }

    private static IntPtr FindAppleMusicWindow()
    {
        IntPtr found = IntPtr.Zero;
        long bestArea = -1;
        EnumWindows((hWnd, _) =>
        {
<<<<<<< HEAD
            if (!IsWindow(hWnd) || !IsWindowVisible(hWnd)) return true;
            var title = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, title, title.Capacity);
            if (title.Length == 0) return true;
=======
            if (!IsWindow(hWnd)) return true;
>>>>>>> 9c4da6fa662428a2fa5a53422d751ceae9329a8a
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                string name = process.ProcessName;
                if (!(name.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) || name.Equals("Apple Music", StringComparison.OrdinalIgnoreCase))) return true;
                if (!GetClientRect(hWnd, out RECT rect)) return true;
                long area = Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);
                // Prefer the main, sizeable app window over small auxiliary windows.
                if (area > bestArea) { bestArea = area; found = hWnd; }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static async Task ClickAtPercentAsync(IntPtr hwnd, int xPercent, int yPercent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
<<<<<<< HEAD
        EnsureTargetForeground(hwnd);
=======
>>>>>>> 9c4da6fa662428a2fa5a53422d751ceae9329a8a
        if (!GetClientRect(hwnd, out RECT r) || r.Right <= 0 || r.Bottom <= 0)
            throw new InvalidOperationException("无法读取 Apple Music 窗口尺寸。");
        int x = Math.Clamp(r.Right * Math.Clamp(xPercent, 0, 100) / 100, 0, Math.Max(0, r.Right - 1));
        int y = Math.Clamp(r.Bottom * Math.Clamp(yPercent, 0, 100) / 100, 0, Math.Max(0, r.Bottom - 1));
        POINT target = new() { X = x, Y = y };
        if (!ClientToScreen(hwnd, ref target)) throw new InvalidOperationException("无法计算 Apple Music 点击位置。");
        GetCursorPos(out POINT original);
        try
        {
            SetCursorPos(target.X, target.Y);
            await Task.Delay(80, token);
<<<<<<< HEAD
            EnsureTargetForeground(hwnd);
=======
>>>>>>> 9c4da6fa662428a2fa5a53422d751ceae9329a8a
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(60, token);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(100, token);
        }
        finally { SetCursorPos(original.X, original.Y); }
    }

}
