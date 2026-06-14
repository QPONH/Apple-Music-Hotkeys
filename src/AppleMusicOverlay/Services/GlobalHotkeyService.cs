using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly Window _owner;
    private readonly Dictionary<int, AppAction> _actions = new();
    private HwndSource? _source;
    private int _nextId = 0x4150;
    private bool _disposed;

    public event EventHandler<AppAction>? ActionRequested;

    public GlobalHotkeyService(Window owner)
    {
        _owner = owner;
    }

    public bool Register(AppAction action, string hotkeyText)
    {
        ThrowIfDisposed();
        if (!HotkeyParser.TryParse(hotkeyText, out HotkeyDefinition hotkey))
        {
            return false;
        }

        IntPtr handle = EnsureHandle();
        int id = _nextId++;
        bool registered = RegisterHotKey(handle, id, hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey);
        if (!registered)
        {
            return false;
        }

        _actions[id] = action;
        return true;
    }

    public void Clear()
    {
        if (_disposed)
        {
            return;
        }

        IntPtr handle = EnsureHandle();
        foreach (int id in _actions.Keys)
        {
            UnregisterHotKey(handle, id);
        }

        _actions.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        _source?.RemoveHook(WndProc);
        _source = null;
        _disposed = true;
    }

    private IntPtr EnsureHandle()
    {
        var helper = new WindowInteropHelper(_owner);
        IntPtr handle = helper.Handle == IntPtr.Zero ? helper.EnsureHandle() : helper.Handle;
        if (_source == null)
        {
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WndProc);
        }

        return handle;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out AppAction action))
        {
            handled = true;
            ActionRequested?.Invoke(this, action);
        }

        return IntPtr.Zero;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GlobalHotkeyService));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
