using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AppleMusicOverlay.Services;

/// <summary>
/// Owns the notification-area icon through a direct <c>Shell_NotifyIcon</c> call instead of
/// <see cref="Forms.NotifyIcon"/>, because WinForms reports success even when the shell
/// rejects the icon (for example with ERROR_ACCESS_DENIED while the taskbar is starting up).
/// Every registration attempt is checked and retried until the shell accepts it.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const string Tooltip = "MusicFloat";
    private const string IconFileName = "MusicFloat.ico";

    private const int HwndMessage = -3;
    private const uint TrayIconMessage = 0x8001; // WM_APP + 1
    private const uint CallbackMessage = TrayIconMessage;

    private const uint NIM_ADD = 0x0;
    private const uint NIM_MODIFY = 0x1;
    private const uint NIM_DELETE = 0x2;
    private const uint NIM_SETVERSION = 0x4;

    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NIF_INFO = 0x10;

    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint NIIF_INFO = 0x1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private readonly Window _owner;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly DispatcherTimer _retryTimer;
    private readonly uint _iconId = (uint)Environment.ProcessId;
    private HwndSource? _messageWindow;
    private Forms.ContextMenuStrip? _menu;
    private Icon? _icon;
    private int _taskbarCreatedMessage;
    private int _retryCount;
    private bool _iconAvailable;
    private bool _disposed;

    public TrayIconService(Window owner, Action openSettings, Action exitApplication)
    {
        _owner = owner;
        _openSettings = openSettings;
        _exitApplication = exitApplication;

        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _retryTimer.Tick += (_, _) => RetryRegister();

        CreateShellMessageWindow();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        TryRegister();
    }

    /// <summary>True once the shell has really accepted the icon.</summary>
    public bool IsIconAvailable => _iconAvailable;

    /// <summary>Reason reported by the last failed registration attempt (empty when healthy).</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>Drops and re-adds the icon; safe to call at any time.</summary>
    public void EnsureVisible()
    {
        if (_disposed) return;
        RemoveIcon();
        TryRegister();
    }

    /// <summary>Balloon tip used once to point the user at a possibly hidden tray icon.</summary>
    public bool TryShowBalloon(string title, string text)
    {
        if (_disposed || !_iconAvailable) return false;
        try
        {
            var data = CreateData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(text, 255);
            data.dwInfoFlags = NIIF_INFO;
            if (Shell_NotifyIcon(NIM_MODIFY, ref data)) return true;
            LastError = DescribeFailure("显示气泡提示失败");
            return false;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TrayIcon:Balloon");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retryTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (_messageWindow is not null)
        {
            _messageWindow.RemoveHook(ShellMessageHook);
            _messageWindow.Dispose();
            _messageWindow = null;
        }
        RemoveIcon();
        _menu?.Dispose();
        _menu = null;
        _icon?.Dispose();
        _icon = null;
    }

    private void RetryRegister()
    {
        if (_disposed) return;
        if (TryRegister())
        {
            _retryTimer.Stop();
            _retryCount = 0;
            return;
        }
        // Back off after the first minute so a permanently blocked shell does not spin.
        _retryCount++;
        if (_retryCount == 30) _retryTimer.Interval = TimeSpan.FromSeconds(30);
    }

    private bool TryRegister()
    {
        if (_disposed) return true;
        try
        {
            if (_menu is null) _menu = BuildMenu();
            _icon ??= LoadAppIcon(Forms.SystemInformation.SmallIconSize);

            var data = CreateData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = CallbackMessage;
            data.hIcon = _icon.Handle;
            data.szTip = Tooltip;
            if (!Shell_NotifyIcon(NIM_ADD, ref data))
            {
                _iconAvailable = false;
                LastError = DescribeFailure("托盘图标注册被系统拒绝");
                CrashLog.Write(new Win32Exception(Marshal.GetLastWin32Error(), LastError), "TrayIcon:Register");
                if (!_retryTimer.IsEnabled) _retryTimer.Start();
                return false;
            }

            // NOTIFYICON_VERSION_4 gives us click coordinates and lets balloons use the
            // modern (toast) presentation.
            var version = CreateData();
            version.uFlags = 0;
            version.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref version);

            _iconAvailable = true;
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _iconAvailable = false;
            LastError = $"0x{ex.HResult:X8} {ex.Message}";
            CrashLog.Write(ex, "TrayIcon:Register");
            if (!_retryTimer.IsEnabled) _retryTimer.Start();
            return false;
        }
    }

    private void RemoveIcon()
    {
        if (!_iconAvailable) return;
        try
        {
            var data = CreateData();
            data.uFlags = 0;
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TrayIcon:Remove");
        }
        finally
        {
            _iconAvailable = false;
        }
    }

    private NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _messageWindow?.Handle ?? IntPtr.Zero,
        uID = _iconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
        guidItem = Guid.Empty
    };

    private static string DescribeFailure(string what)
    {
        int error = Marshal.GetLastWin32Error();
        string detail = error == 0 ? "系统未返回错误码" : new Win32Exception(error).Message + $" (0x{error:X})";
        string integrity = ProcessIntegrity.ToText();
        if (error == 5)
        {
            // ERROR_ACCESS_DENIED: the shell only accepts icons from a process at the same or
            // higher integrity than the taskbar, so a sandboxed/low-integrity run is refused.
            return $"{what}：{detail}（当前进程完整性级别：{integrity}）。" +
                   "任务栏不接受低权限/沙箱方式运行的程序图标；请把 MusicFloat.exe 复制到本地普通目录、" +
                   "在文件属性里勾选“解除锁定”，然后用普通用户双击运行。";
        }
        return $"{what}：{detail}（进程完整性：{integrity}）";
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private void CreateShellMessageWindow()
    {
        try
        {
            // Message-only window: receives tray callbacks and the shell's "TaskbarCreated"
            // broadcast that is sent after Explorer restarts.
            var parameters = new HwndSourceParameters("MusicFloatTrayMessageWindow")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = new IntPtr(HwndMessage)
            };
            _messageWindow = new HwndSource(parameters);
            _messageWindow.AddHook(ShellMessageHook);
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TrayIcon:MessageWindow");
        }
    }

    private IntPtr ShellMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            // Explorer restarted: the old icon is gone, register it again.
            _iconAvailable = false;
            TryRegister();
            return IntPtr.Zero;
        }
        if ((uint)msg != CallbackMessage) return IntPtr.Zero;

        uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
        switch ((int)evt)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                PostToOwner(_openSettings);
                handled = true;
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        try
        {
            _menu ??= BuildMenu();
            // Required so the popup closes when the user clicks elsewhere.
            SetForegroundWindow(_messageWindow?.Handle ?? IntPtr.Zero);
            _menu.Show(Forms.Cursor.Position);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TrayIcon:Menu");
        }
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("打开设置", null, (_, _) => PostToOwner(_openSettings)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => PostToOwner(_exitApplication)));
        return menu;
    }

    private void PostToOwner(Action action)
    {
        if (_disposed || _owner.Dispatcher.HasShutdownStarted || _owner.Dispatcher.HasShutdownFinished) return;
        _owner.Dispatcher.BeginInvoke(action);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => RunOnOwnerThread(() => EnsureVisible());

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // DPI scaling and icon-size changes arrive as a general preference change; reload
        // the icon so the shell gets a frame at the new native size instead of a scaled one.
        if (e.Category == UserPreferenceCategory.General) RunOnOwnerThread(() => EnsureVisible());
    }

    private void RunOnOwnerThread(Action action)
    {
        if (_disposed || _owner.Dispatcher.HasShutdownStarted || _owner.Dispatcher.HasShutdownFinished) return;
        _owner.Dispatcher.BeginInvoke(action);
    }

    private static Icon LoadAppIcon(System.Drawing.Size targetSize)
    {
        return LoadIconFromResource(targetSize)
            ?? LoadIconFromFile(targetSize)
            ?? LoadIconFromExecutable()
            ?? (Icon)SystemIcons.Application.Clone();
    }

    private static Icon? LoadIconFromResource(System.Drawing.Size targetSize)
    {
        try
        {
            var assembly = typeof(TrayIconService).Assembly;
            string? name = assembly.GetManifestResourceNames()
                .FirstOrDefault(candidate => candidate.EndsWith($"Assets.{IconFileName}", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            return CreateSizedIcon(stream, targetSize);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TrayIcon:Resource");
            return null;
        }
    }

    private static Icon? LoadIconFromFile(System.Drawing.Size targetSize)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", IconFileName);
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return CreateSizedIcon(stream, targetSize);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TrayIcon:File");
            return null;
        }
    }

    private static Icon? LoadIconFromExecutable()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath)) return null;
            return Icon.ExtractAssociatedIcon(processPath);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TrayIcon:Executable");
            return null;
        }
    }

    private static Icon CreateSizedIcon(Stream stream, System.Drawing.Size targetSize)
    {
        // Ask for the shell's small-icon size so Windows gets an image with native pixels.
        // A large-only (256 px) image is what makes a tray icon look empty on high DPI.
        return new Icon(stream, targetSize.Width, targetSize.Height);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
