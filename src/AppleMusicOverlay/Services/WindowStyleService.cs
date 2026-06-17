using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AppleMusicOverlay.Services;

public static class WindowStyleService
{
    private const int GwlExStyle = -20;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private static readonly IntPtr HwndTopmost = new(-1);

    public static void ApplyOverlayStyles(Window window)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.Handle == IntPtr.Zero ? helper.EnsureHandle() : helper.Handle;
        int style = GetWindowLong(hwnd, GwlExStyle);
        style |= WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate;
        SetWindowLong(hwnd, GwlExStyle, style);
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    public static void ApplyShadowStyles(Window window)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.Handle == IntPtr.Zero ? helper.EnsureHandle() : helper.Handle;
        int style = GetWindowLong(hwnd, GwlExStyle);
        style |= WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    public static void PlaceShadowBehind(Window shadowWindow, Window ownerWindow)
    {
        var shadowHelper = new WindowInteropHelper(shadowWindow);
        var ownerHelper = new WindowInteropHelper(ownerWindow);
        IntPtr shadowHwnd = shadowHelper.Handle == IntPtr.Zero ? shadowHelper.EnsureHandle() : shadowHelper.Handle;
        IntPtr ownerHwnd = ownerHelper.Handle == IntPtr.Zero ? ownerHelper.EnsureHandle() : ownerHelper.Handle;
        if (ownerHwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(shadowHwnd, ownerHwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);
}
