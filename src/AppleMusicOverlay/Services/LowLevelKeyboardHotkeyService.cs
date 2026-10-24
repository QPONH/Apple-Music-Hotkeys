using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

/// <summary>
/// Captures volume shortcuts through WH_KEYBOARD_LL so they can work in games
/// that do not reliably deliver WM_HOTKEY to background windows.
/// </summary>
internal sealed class LowLevelKeyboardHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfUp = 0x80;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<AppAction, HotkeyDefinition> _hotkeys = new();
    private readonly HashSet<uint> _suppressedKeys = new();
    private readonly HookProc _hookProc;
    private IntPtr _hook;
    private bool _disposed;

    public event EventHandler<AppAction>? ActionRequested;

    public LowLevelKeyboardHotkeyService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hookProc = HookCallback;
    }

    public bool Register(AppAction action, string text)
    {
        if (_disposed || !HotkeyParser.TryParse(text, out HotkeyDefinition hotkey))
            return false;

        if (_hook == IntPtr.Zero)
        {
            using Process process = Process.GetCurrentProcess();
            using ProcessModule? module = process.MainModule;
            IntPtr moduleHandle = module is null ? IntPtr.Zero : GetModuleHandle(module.ModuleName);
            _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, moduleHandle, 0);
            if (_hook == IntPtr.Zero)
                return false;
        }

        _hotkeys[action] = hotkey;
        return true;
    }

    public void Clear()
    {
        _hotkeys.Clear();
        _suppressedKeys.Clear();
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                int message = wParam.ToInt32();
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                uint key = data.VirtualKey;
                bool isDown = message is WmKeyDown or WmSysKeyDown;
                bool isUp = message is WmKeyUp or WmSysKeyUp || (data.Flags & LlkhfUp) != 0;

                if (isUp && _suppressedKeys.Remove(key))
                    return (IntPtr)1;

                // Suppress auto-repeat key-down messages as well. Previously the first
                // press was swallowed but repeats leaked through to the foreground app.
                if (isDown && _suppressedKeys.Contains(key))
                    return (IntPtr)1;

                if (isDown)
                {
                    uint modifiers = GetCurrentModifiers();
                    foreach (var pair in _hotkeys)
                    {
                        if (pair.Value.VirtualKey != key || pair.Value.Modifiers != modifiers)
                            continue;

                        _suppressedKeys.Add(key);
                        AppAction action = pair.Key;
                        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ActionRequested?.Invoke(this, action)));
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch
        {
            // Never allow managed exceptions to cross the native hook callback boundary.
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static uint GetCurrentModifiers()
    {
        uint modifiers = 0;
        if (IsDown(0x11)) modifiers |= ModControl;
        if (IsDown(0x12)) modifiers |= ModAlt;
        if (IsDown(0x10)) modifiers |= ModShift;
        if (IsDown(0x5B) || IsDown(0x5C)) modifiers |= ModWin;
        return modifiers;
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
