using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;
using Forms = System.Windows.Forms;

namespace AppleMusicOverlay;

public partial class MainWindow : Window, IHotkeySnapshotRegistrar, INotifyPropertyChanged
{
    private readonly OverlaySettingsService _settingsService = new();
    private readonly OverlaySettings _settings;
    private OverlaySettings _draftSettings;
    private string _appliedSnapshot = string.Empty;
    private bool _discardingChanges;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly SmtcMediaSessionService _mediaService = new();
    private readonly LyricsService _lyricsService;
    private readonly AppleMusicVolumeService _volumeService = new();
    private readonly StartupPlaybackRecoveryService _startupPlaybackRecoveryService;
    private CancellationTokenSource? _startupRecoveryCancellation;
    private readonly TrayIconService _trayIconService;
    private Views.LyricsWindow? _lyricsWindow;
    private TextBox? _capturingBox;
    private bool _isExiting;
    private bool _isLoadingSettings;
    private bool _startupInitialized;
    private string _statusText = "快捷键已加载。";

    public event PropertyChangedEventHandler? PropertyChanged;
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public MainWindow()
    {
        _settings = _settingsService.Load();
        _draftSettings = CloneSettings(_settings);
        NormalizeDraftValues();
        _lyricsService = new LyricsService(_mediaService);
        _startupPlaybackRecoveryService = new StartupPlaybackRecoveryService(_mediaService);
        // XAML default values (Text="0", IsChecked="True", ...) raise the change handlers while
        // InitializeComponent runs, so the loading flag has to be set before it. Without this
        // the staged settings silently drift away from the loaded ones and the window reports
        // "未应用的更改" even though nothing was touched.
        _isLoadingSettings = true;
        try
        {
            DataContext = this;
            InitializeComponent();
            LoadSettingsToUi();
            UpdateVersionText();
        }
        finally
        {
            _isLoadingSettings = false;
        }
        // Snapshot after the UI has been populated so any value normalised by the UI counts as
        // already applied.
        _appliedSnapshot = SerializeSettings(_draftSettings);
        _hotkeyService = new GlobalHotkeyService(this);
        _hotkeyService.ActionRequested += HotkeyService_ActionRequested;
        _trayIconService = new TrayIconService(this, ShowSettings, () => RequestApplicationExit());
        Loaded += MainWindow_Loaded;
        UpdateMaximizeButtonGlyph();
    }

    /// <summary>Clamps the loaded values into the ranges the UI accepts, so the first render
    /// never looks like a user edit.</summary>
    private void NormalizeDraftValues()
    {
        _draftSettings.LyricsFontSize = double.IsNaN(_draftSettings.LyricsFontSize) ? 26 : Math.Clamp(_draftSettings.LyricsFontSize, 10, 120);
        _draftSettings.LyricsOpacity = LyricStyle.ClampOpacity(_draftSettings.LyricsOpacity);
        _draftSettings.LyricsColor = LyricStyle.Normalize(_draftSettings.LyricsColor);
        _draftSettings.LyricsOffsetMilliseconds = Math.Clamp(_draftSettings.LyricsOffsetMilliseconds, -5000, 5000);
        _draftSettings.LyricsAlignment = _draftSettings.LyricsAlignment is "Left" or "Right" ? _draftSettings.LyricsAlignment : "Center";
        _draftSettings.LyricsOrientation = _draftSettings.LyricsOrientation == "Vertical" ? "Vertical" : "Horizontal";
        _draftSettings.LyricsVerticalAlignment = _draftSettings.LyricsVerticalAlignment is "Top" or "Bottom" ? _draftSettings.LyricsVerticalAlignment : "Center";
        _draftSettings.LyricsVerticalAnchor = _draftSettings.LyricsVerticalAnchor is "Left" or "Right" ? _draftSettings.LyricsVerticalAnchor : "Center";
        _draftSettings.LyricsWindowWidth = Math.Max(180, _draftSettings.LyricsWindowWidth);
        _draftSettings.LyricsWindowHeight = Math.Max(70, _draftSettings.LyricsWindowHeight);
    }

    /// <summary>
    /// Runs the startup work and tells the caller whether the settings window should be shown.
    /// When the app has to start hidden the window is never shown, so it cannot flash first.
    /// </summary>
    public bool PrepareStartup()
    {
        RunStartupInitialization();
        bool startHidden = _settings.HideWindowOnStartup && _trayIconService.IsIconAvailable;
        if (!startHidden) return true;

        // Create the handle without showing the window: hotkeys, the tray icon and the lyric
        // window all need a live HWND, but nothing needs the window to be visible.
        try { new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle(); }
        catch (Exception ex) { CrashLog.Write(ex, "PrepareStartup:EnsureHandle"); return true; }
        return false;
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        // App decides up front whether to show the window (hidden starts never get here), so a
        // loaded window is always the one the user is looking at; never hide it again here or
        // the tray's "打开设置" would immediately disappear.
        RunStartupInitialization();
    }

    private void RunStartupInitialization()
    {
        if (_startupInitialized) return;
        _startupInitialized = true;

        RegisterHotkeys();
        // The shell can silently drop an icon registered before the taskbar is ready, so
        // register it again once the window exists.
        _trayIconService.EnsureVisible();
        if (_settings.LyricsEnabled) ShowLyricsWindow();
        if (_settings.HideWindowOnStartup && !_trayIconService.IsIconAvailable)
            StatusText = "托盘图标未能注册，已保持设置窗口可见；原因见“通用设置 → 任务栏图标”。";
        UpdateTrayStatus();
        if (_settings.HideWindowOnStartup) ShowTrayHintIfNeeded();
        if (!_trayIconService.IsIconAvailable && ProcessIntegrity.IsLowOrBelow) ShowLowIntegrityWarning();
        if (_settings.StartupPlaybackRecovery)
        {
            _startupRecoveryCancellation = new CancellationTokenSource();
            _ = RunStartupPlaybackRecoveryAsync(_startupRecoveryCancellation.Token);
        }
    }

    /// <summary>Pushes the staged settings into the controls. The caller owns
    /// <see cref="_isLoadingSettings"/> so nested loads (reset, constructor) stay quiet.</summary>
    private void LoadSettingsToUi()
    {
        {
        PreviousBox.Text = _draftSettings.KeyboardPrevious;
        NextBox.Text = _draftSettings.KeyboardNext;
        ToggleBox.Text = _draftSettings.KeyboardToggle;
        VolumeUpBox.Text = _draftSettings.KeyboardVolumeUp;
        VolumeDownBox.Text = _draftSettings.KeyboardVolumeDown;
        CloseToTrayCheckBox.IsChecked = _draftSettings.CloseToTray;
        HideOnStartupCheckBox.IsChecked = _draftSettings.HideWindowOnStartup;
        StartWithWindowsCheckBox.IsChecked = _draftSettings.StartWithWindows;
        StartupPlaybackRecoveryCheckBox.IsChecked = _draftSettings.StartupPlaybackRecovery;
        RecoveryWaitSecondsBox.Text = _draftSettings.RecoveryWaitSeconds.ToString();
        RecoveryQueueXBox.Text = _draftSettings.RecoveryQueueXPercent.ToString();
        RecoveryQueueYBox.Text = _draftSettings.RecoveryQueueYPercent.ToString();
        RecoverySongXBox.Text = _draftSettings.RecoverySongXPercent.ToString();
        RecoverySongYBox.Text = _draftSettings.RecoverySongYPercent.ToString();
        RecoveryQueueMethodBox.SelectedValue = _draftSettings.RecoveryQueueMethod;
        RecoverySongMethodBox.SelectedValue = _draftSettings.RecoverySongMethod;
        LyricsEnabledCheckBox.IsChecked = _draftSettings.LyricsEnabled;
        LyricsFontSizeBox.Text = _draftSettings.LyricsFontSize.ToString("0");
        LyricsOpacityBox.Text = (_draftSettings.LyricsOpacity * 100).ToString("0");
        LyricsColorBox.Text = _draftSettings.LyricsColor;
        UpdateLyricsColorSwatch();
        LyricsAlignmentBox.SelectedValue = _draftSettings.LyricsAlignment;
        LyricsOrientationBox.SelectedValue = _draftSettings.LyricsOrientation;
        LyricsVerticalAlignmentBox.SelectedValue = _draftSettings.LyricsVerticalAlignment;
        LyricsOffsetBox.Text = _draftSettings.LyricsOffsetMilliseconds.ToString();
        LyricsVerticalAlignmentBox.IsEnabled = _draftSettings.LyricsOrientation == "Vertical";
        LyricsVerticalAnchorBox.SelectedValue = _draftSettings.LyricsVerticalAnchor;
        LyricsVerticalAnchorBox.IsEnabled = _draftSettings.LyricsOrientation == "Vertical";
        LyricsBackgroundCheckBox.IsChecked = _draftSettings.LyricsBackgroundEnabled;
        }
    }

    private void HotkeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box) return;

        // Windows consumes registered global hotkey chords before WPF can capture them.
        // Temporarily unregister all shortcuts while recording a new binding.
        _capturingBox = box;
        box.Focus();
        box.SelectAll();
        StatusText = "正在设置快捷键：按住 Ctrl / Alt / Shift / Win，再按一个按键；按 Esc 取消。";
        e.Handled = true;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || _capturingBox != box) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            box.Text = GetDraftSettingValue((string)box.Tag);
            _capturingBox = null;
            StatusText = "已取消快捷键设置。";
            e.Handled = true;
            return;
        }

        // Modifier keys are only part of the chord; wait for the main key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            StatusText = "请先按住 Ctrl、Alt、Shift 或 Win，再按主按键。";
            e.Handled = true;
            return;
        }

        string mainKey = FormatKey(key);
        if (string.IsNullOrWhiteSpace(mainKey))
        {
            e.Handled = true;
            return;
        }

        string hotkey = FormatHotkey(modifiers, mainKey);
        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(
            _draftSettings, ParseAction((string)box.Tag), hotkey, DraftHotkeyRegistrar.Instance);
        StatusText = result.Message;

        if (result.Success)
        {
            box.Text = hotkey;
            MarkDraftChanged();
            _capturingBox = null;
        }
        else
        {
            // Keep capture active after a conflict, and keep the OS from swallowing
            // the next attempt with the old registered shortcut.
        }

        e.Handled = true;
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box || _capturingBox != box) return;

        box.Text = GetDraftSettingValue((string)box.Tag);
        _capturingBox = null;
        StatusText = "快捷键设置已取消。";
    }

    private void NavigationTabs_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingBox != null || e.Key is not (Key.Up or Key.Down)) return;
        if (sender is not TabControl tabs) return;

        // Keep Up/Down available for scrolling within page content. Only suppress
        // vertical selection changes when focus is on one of the left navigation tabs.
        for (int i = 0; i < tabs.Items.Count; i++)
        {
            if (tabs.ItemContainerGenerator.ContainerFromIndex(i) is TabItem item
                && item.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void DeleteHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        string tag = (string)button.Tag;
        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(_draftSettings, ParseAction(tag), string.Empty, DraftHotkeyRegistrar.Instance);
        if (result.Success)
        {
            SetBoxText(tag, string.Empty);
            MarkDraftChanged();
        }
        StatusText = result.Message;
    }

    public bool TryRegisterSnapshot(IReadOnlyDictionary<AppAction, string> hotkeys, AppAction requiredAction)
        => RegisterHotkeySnapshotDirect(hotkeys, requiredAction);

    private bool RegisterHotkeys()
        => RegisterHotkeySnapshotDirect(KeyboardHotkeyBindingManager.CreateSnapshot(_settings), requiredAction: null);

    private bool RegisterHotkeySnapshotDirect(
        IReadOnlyDictionary<AppAction, string> hotkeys,
        AppAction? requiredAction)
    {
        _hotkeyService.Clear();
        var failedActions = new List<AppAction>();

        foreach ((AppAction action, string hotkeyText) in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(hotkeyText)) continue;
            if (_hotkeyService.Register(action, hotkeyText)) continue;

            failedActions.Add(action);
            if (requiredAction == action)
            {
                // The newly requested shortcut itself is unavailable. Restore the
                // previous configuration on a best-effort basis before reporting failure.
                _hotkeyService.Clear();
                RegisterHotkeySnapshotDirect(
                    KeyboardHotkeyBindingManager.CreateSnapshot(_settings),
                    requiredAction: null);
                return false;
            }
        }

        if (failedActions.Count > 0 && _capturingBox == null)
        {
            StatusText = "部分快捷键未能注册（可能被系统或其他程序占用）：" +
                         string.Join("、", failedActions.Select(GetActionLabel)) +
                         "。仍可继续自定义这些快捷键。";
        }

        return true;
    }

    private static string GetActionLabel(AppAction action) => action switch
    {
        AppAction.PreviousTrack => "上一曲",
        AppAction.NextTrack => "下一曲",
        AppAction.TogglePlayPause => "播放/暂停",
        AppAction.VolumeUp => "音量+",
        AppAction.VolumeDown => "音量-",
        _ => action.ToString()
    };

    private async void HotkeyService_ActionRequested(object? sender, AppAction action)
    {
        try
        {
            switch (action)
            {
                case AppAction.PreviousTrack: await _mediaService.PreviousAsync(); break;
                case AppAction.NextTrack: await _mediaService.NextAsync(); break;
                case AppAction.TogglePlayPause: await _mediaService.TogglePlayPauseAsync(); break;
                case AppAction.VolumeUp: ChangeVolume(0.05f); break;
                case AppAction.VolumeDown: ChangeVolume(-0.05f); break;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, $"GlobalHotkey:{action}");
            StatusText = $"快捷键执行失败：{ex.Message}";
        }
    }

    private void ChangeVolume(float delta)
    {
        if (_volumeService.TryChange(delta, out float volume))
            StatusText = $"Apple Music 音量：{volume:P0}";
        else
            StatusText = "没有找到正在输出声音的 Apple Music 音频会话。请先播放音乐。";
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await RunMediaActionSafelyAsync(() => _mediaService.PreviousAsync(), "上一曲");
    private async void Next_Click(object sender, RoutedEventArgs e) => await RunMediaActionSafelyAsync(() => _mediaService.NextAsync(), "下一曲");
    private async void Toggle_Click(object sender, RoutedEventArgs e) => await RunMediaActionSafelyAsync(() => _mediaService.TogglePlayPauseAsync(), "播放/暂停");

    private async void TestMedia_Click(object sender, RoutedEventArgs e)
    {
        if (await RunMediaActionSafelyAsync(() => _mediaService.TogglePlayPauseAsync(), "播放/暂停测试"))
            StatusText = "已发送播放 / 暂停指令到 Apple Music。";
    }

    private void UpdateVersionText()
    {
        // Showing the executable build time makes it obvious which binary is running.
        string buildTime = "-";
        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                buildTime = System.IO.File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        }
        catch { }
        VersionText.Text = $"v14.25 · 构建 {buildTime} · {Environment.OSVersion.Version}";
    }

    private bool _lowIntegrityWarningShown;

    /// <summary>
    /// A low-integrity (sandboxed) process cannot register a tray icon at all; explain the
    /// reason instead of leaving the user with a silently missing icon.
    /// </summary>
    private void ShowLowIntegrityWarning()
    {
        if (_lowIntegrityWarningShown) return;
        _lowIntegrityWarningShown = true;
        try
        {
            // No owner: the main window may be hidden at startup, and a modal dialog owned by
            // a hidden window can end up invisible.
            MessageBox.Show(
                $"程序当前以「{ProcessIntegrity.ToText()}」权限运行，Windows 会拒绝注册任务栏图标，也会拒绝写入开机启动项。\n\n" +
                "常见原因：通过“低权限运行/沙箱运行”的启动器启动、被安全软件隔离运行，或启动方式被限制。\n\n" +
                "建议：把 MusicFloat.exe 复制到本地普通目录（例如 D:\\Tools），右键文件 → 属性 → 勾选“解除锁定”，" +
                "然后用普通用户双击运行。\n\n托盘图标不可用期间，关闭设置窗口只会最小化到任务栏，仍可从任务栏恢复。",
                "任务栏图标无法注册", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText = "低权限进程无法注册托盘图标；已改为最小化到任务栏。";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "LowIntegrityWarning");
        }
    }

    private void UpdateTrayStatus()
    {
        if (TrayStatusText is null) return;
        if (_trayIconService.IsIconAvailable)
        {
            TrayStatusText.Text = "已注册";
            TrayStatusText.ToolTip = null;
            return;
        }
        string reason = string.IsNullOrWhiteSpace(_trayIconService.LastError) ? "未知原因" : _trayIconService.LastError;
        TrayStatusText.Text = "未注册：" + reason;
        TrayStatusText.ToolTip = reason;
    }

    private void ReRegisterTrayIcon_Click(object sender, RoutedEventArgs e)
    {
        _trayIconService.EnsureVisible();
        UpdateTrayStatus();
        if (_trayIconService.IsIconAvailable)
        {
            StatusText = "已重新注册任务栏图标。";
            return;
        }
        string reason = string.IsNullOrWhiteSpace(_trayIconService.LastError) ? "未知原因" : _trayIconService.LastError;
        StatusText = "任务栏图标注册失败：" + reason;
        MessageBox.Show(this, reason, "任务栏图标注册失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenTaskbarSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });
            StatusText = "已打开任务栏设置：在“其他系统托盘图标”里把 MusicFloat 设为“开”。";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TrayIcon:OpenTaskbarSettings");
            StatusText = "无法打开任务栏设置，请手动到“设置 → 个性化 → 任务栏”。";
        }
    }

    /// <summary>
    /// When the app starts hidden the tray icon is the only entry point, and Windows 10/11
    /// keeps new icons in the "hidden icons" overflow by default. Explain this once.
    /// </summary>
    private void ShowTrayHintIfNeeded()
    {
        if (_settings.TrayHintShown || !_trayIconService.IsIconAvailable) return;
        if (!_trayIconService.TryShowBalloon("MusicFloat 正在后台运行",
                "如果任务栏通知区域没有直接看到图标，请点击「显示隐藏的图标」把它拖到任务栏上；" +
                "也可以在下次启动后打开设置窗口。")) return;
        _settings.TrayHintShown = true;
        // The hint is cosmetic: a failed save must not break startup.
        try
        {
            _settingsService.Save(_settings);
            _draftSettings = CloneSettings(_settings);
            _appliedSnapshot = SerializeSettings(_draftSettings);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TrayIcon:Hint");
        }
    }

    private void ShowLyricsWindow()
    {
        if (_lyricsWindow is { IsVisible: true }) return;
        _lyricsWindow = new Views.LyricsWindow(_lyricsService, _settings, ShowSettings, () => RequestApplicationExit());
        _lyricsWindow.Closed += (_, _) => _lyricsWindow = null;
        _lyricsWindow.Show();
    }

    private void LyricsEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _draftSettings.LyricsEnabled = LyricsEnabledCheckBox.IsChecked == true;
        MarkDraftChanged();
    }

    private void LyricsOffsetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings || _draftSettings is null) return;
        // Do not rewrite the TextBox while the user is typing; accept signed integer
        // milliseconds and clamp only the value staged for application.
        if (int.TryParse(LyricsOffsetBox.Text, out int offset))
            _draftSettings.LyricsOffsetMilliseconds = Math.Clamp(offset, -5000, 5000);
        MarkDraftChanged();
    }

    private void LyricsOpacityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings || _draftSettings is null) return;
        // Accepted while typing: "80", "80%" or "0.8". The staged value is clamped to 5% - 100%.
        _draftSettings.LyricsOpacity = LyricStyle.ParseOpacityPercent(LyricsOpacityBox.Text, _draftSettings.LyricsOpacity);
        MarkDraftChanged();
    }

    private void LyricsOpacityBox_LostFocus(object sender, RoutedEventArgs e)
    {
        LyricsOpacityBox.Text = (_draftSettings.LyricsOpacity * 100).ToString("0");
    }

    private void LyricsColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings || _draftSettings is null) return;
        // Partial input such as "#F" or "#FF12" stays untouched until it forms a full color.
        if (!LyricStyle.TryParseColor(LyricsColorBox.Text, out _)) return;
        _draftSettings.LyricsColor = LyricStyle.Normalize(LyricsColorBox.Text);
        UpdateLyricsColorSwatch();
        MarkDraftChanged();
    }

    private void LyricsColorBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _draftSettings.LyricsColor = LyricStyle.Normalize(LyricsColorBox.Text);
        LyricsColorBox.Text = _draftSettings.LyricsColor;
        UpdateLyricsColorSwatch();
        MarkDraftChanged();
    }

    private void LyricsPickColor_Click(object sender, RoutedEventArgs e)
    {
        var current = LyricStyle.ParseColor(_draftSettings.LyricsColor);
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
        };
        if (dialog.ShowDialog(new Win32WindowHandle(new WindowInteropHelper(this).Handle)) != Forms.DialogResult.OK) return;
        var picked = System.Windows.Media.Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        _draftSettings.LyricsColor = LyricStyle.ToHex(picked);
        LyricsColorBox.Text = _draftSettings.LyricsColor;
        UpdateLyricsColorSwatch();
        MarkDraftChanged();
    }

    private void UpdateLyricsColorSwatch() => LyricsColorSwatch.Background = LyricStyle.CreateBrush(_draftSettings.LyricsColor);

    /// <summary>
    /// Commits whatever is currently typed in the opacity / color boxes. Needed because the
    /// Apply button is not focusable, so a still-focused box may never raise LostFocus.
    /// </summary>
    private void NormalizeLyricsInputs()
    {
        _draftSettings.LyricsOpacity = LyricStyle.ParseOpacityPercent(LyricsOpacityBox.Text, _draftSettings.LyricsOpacity);
        LyricsOpacityBox.Text = (_draftSettings.LyricsOpacity * 100).ToString("0");
        _draftSettings.LyricsColor = LyricStyle.Normalize(LyricsColorBox.Text);
        LyricsColorBox.Text = _draftSettings.LyricsColor;
        UpdateLyricsColorSwatch();
    }

    private sealed class Win32WindowHandle : Forms.IWin32Window
    {
        public Win32WindowHandle(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    private void LyricsSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;

        _draftSettings.LyricsFontSize = double.TryParse(LyricsFontSizeBox.Text, out double size) ? Math.Clamp(size, 10, 120) : 26;
        _draftSettings.LyricsAlignment = LyricsAlignmentBox.SelectedValue?.ToString() ?? "Center";
        _draftSettings.LyricsOrientation = LyricsOrientationBox.SelectedValue?.ToString() ?? "Horizontal";
        _draftSettings.LyricsVerticalAlignment = LyricsVerticalAlignmentBox.SelectedValue?.ToString() ?? "Center";
        _draftSettings.LyricsVerticalAnchor = LyricsVerticalAnchorBox.SelectedValue?.ToString() ?? "Center";
        if (int.TryParse(LyricsOffsetBox.Text, out int offset)) _draftSettings.LyricsOffsetMilliseconds = Math.Clamp(offset, -5000, 5000);
        LyricsVerticalAlignmentBox.IsEnabled = _draftSettings.LyricsOrientation == "Vertical";
        LyricsVerticalAnchorBox.IsEnabled = _draftSettings.LyricsOrientation == "Vertical";
        _draftSettings.LyricsBackgroundEnabled = LyricsBackgroundCheckBox.IsChecked == true;
        if (double.TryParse(LyricsFontSizeBox.Text, out size)) LyricsFontSizeBox.Text = _draftSettings.LyricsFontSize.ToString("0");
        MarkDraftChanged();
    }

    private async Task<bool> RunMediaActionSafelyAsync(Func<Task> action, string label)
    {
        try { await action(); return true; }
        catch (Exception ex)
        {
            CrashLog.Write(ex, $"MediaAction:{label}");
            StatusText = $"{label}失败：{ex.Message}";
            return false;
        }
    }

    private void CloseToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _draftSettings.CloseToTray = CloseToTrayCheckBox.IsChecked == true;
        MarkDraftChanged();
    }

    private void HideOnStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _draftSettings.HideWindowOnStartup = HideOnStartupCheckBox.IsChecked == true;
        MarkDraftChanged();
    }

    private void StartupPlaybackRecovery_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _draftSettings.StartupPlaybackRecovery = StartupPlaybackRecoveryCheckBox.IsChecked == true;
        MarkDraftChanged();
    }

    private async void CaptureQueuePosition_Click(object sender, RoutedEventArgs e)
    {
        await CaptureAppleMusicPositionAsync(true);
    }

    private async void CaptureSongPosition_Click(object sender, RoutedEventArgs e)
    {
        await CaptureAppleMusicPositionAsync(false);
    }

    private async Task CaptureAppleMusicPositionAsync(bool queueButton)
    {
        StatusText = "请在 Apple Music 中单击要捕获的位置；这次点击会被拦截，不会操作界面。";
        try
        {
            using var capture = new MousePositionCaptureService();
            var position = await capture.Result;
            if (queueButton)
            {
                _draftSettings.RecoveryQueueXPercent = position.x;
                _draftSettings.RecoveryQueueYPercent = position.y;
                RecoveryQueueXBox.Text = position.x.ToString();
                RecoveryQueueYBox.Text = position.y.ToString();
            }
            else
            {
                _draftSettings.RecoverySongXPercent = position.x;
                _draftSettings.RecoverySongYPercent = position.y;
                RecoverySongXBox.Text = position.x.ToString();
                RecoverySongYBox.Text = position.y.ToString();
            }
            MarkDraftChanged();
            StatusText = $"位置已捕获：X={position.x}%，Y={position.y}%。点击“应用”后生效。";
        }
        catch (Exception ex)
        {
            StatusText = $"捕获位置失败：{ex.Message}";
            MessageBox.Show(this, ex.Message, "捕获鼠标位置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    private void RecoveryMethod_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || RecoveryQueueMethodBox.SelectedValue is null || RecoverySongMethodBox.SelectedValue is null) return;
        if (_isLoadingSettings) return;
        _draftSettings.RecoveryQueueMethod = RecoveryQueueMethodBox.SelectedValue.ToString() ?? "Keyboard";
        _draftSettings.RecoverySongMethod = RecoverySongMethodBox.SelectedValue.ToString() ?? "Mouse";
        MarkDraftChanged();
    }

    private void RecoveryWaitSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _draftSettings.RecoveryWaitSeconds = int.TryParse(RecoveryWaitSecondsBox.Text, out int seconds)
            ? Math.Clamp(seconds, 0, 600) : 2;
        RecoveryWaitSecondsBox.Text = _draftSettings.RecoveryWaitSeconds.ToString();
        MarkDraftChanged();
    }

    private void RecoveryCoordinates_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _draftSettings.RecoveryQueueXPercent = ReadPercent(RecoveryQueueXBox, 85);
        _draftSettings.RecoveryQueueYPercent = ReadPercent(RecoveryQueueYBox, 3);
        _draftSettings.RecoverySongXPercent = ReadPercent(RecoverySongXBox, 24);
        _draftSettings.RecoverySongYPercent = ReadPercent(RecoverySongYBox, 12);
        RecoveryQueueXBox.Text = _draftSettings.RecoveryQueueXPercent.ToString();
        RecoveryQueueYBox.Text = _draftSettings.RecoveryQueueYPercent.ToString();
        RecoverySongXBox.Text = _draftSettings.RecoverySongXPercent.ToString();
        RecoverySongYBox.Text = _draftSettings.RecoverySongYPercent.ToString();
        MarkDraftChanged();
    }

    private static int ReadPercent(TextBox box, int fallback) =>
        int.TryParse(box.Text, out int value) ? Math.Clamp(value, 0, 100) : fallback;

    private void CancelStartupPlaybackRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (_startupRecoveryCancellation is null || _startupRecoveryCancellation.IsCancellationRequested)
        {
            StatusText = "当前没有正在运行的后台开机播放检测。";
            return;
        }

        _startupRecoveryCancellation.Cancel();
        StatusText = "已请求停止后台开机播放检测。";
    }

    private async void TestStartupPlaybackRecovery_Click(object sender, RoutedEventArgs e)
    {
        RecoveryCoordinates_LostFocus(RecoveryQueueXBox, new RoutedEventArgs());
        StatusText = "正在手动测试开机自动播放流程……请勿操作鼠标。";
        try
        {
            await _startupPlaybackRecoveryService.RunOnceAsync(
                _settings.RecoveryQueueXPercent, _settings.RecoveryQueueYPercent,
                _settings.RecoverySongXPercent, _settings.RecoverySongYPercent,
                _settings.RecoveryQueueMethod, _settings.RecoverySongMethod, _settings.RecoveryWaitSeconds);
            StatusText = "测试流程已执行一次。请检查 Apple Music 是否打开队列并开始播放第一首歌曲。";
        }
        catch (Exception ex)
        {
            StatusText = $"测试失败：{ex.Message}";
            MessageBox.Show(this, ex.Message, "开机自动播放测试失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RunStartupPlaybackRecoveryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _startupPlaybackRecoveryService.RunAsync(_settings.RecoveryQueueXPercent, _settings.RecoveryQueueYPercent, _settings.RecoverySongXPercent, _settings.RecoverySongYPercent, _settings.RecoveryQueueMethod, _settings.RecoverySongMethod, _settings.RecoveryWaitSeconds, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(() => StatusText = "已执行开机恢复播放操作；请确认歌曲和队列是否正确。 ");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "StartupPlaybackRecovery");
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(() => StatusText = $"开机恢复播放操作失败：{ex.Message}");
        }
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _draftSettings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        MarkDraftChanged();
    }

    private void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingBox != null)
        {
            StatusText = "请先完成或取消快捷键录入，再应用设置。";
            return;
        }
        try
        {
            NormalizeLyricsInputs();
            CopySettings(_draftSettings, _settings);
            if (!RegisterHotkeys())
            {
                StatusText = "部分快捷键无法注册，请检查冲突后重试。";
                return;
            }
            // Persist the settings first: the startup registration below is best-effort and
            // must not be able to fail an otherwise valid apply (the Run key can be locked
            // down by policy or security software).
            _settingsService.Save(_settings);
            bool startupOk = StartupService.TrySetEnabled(_settings.StartWithWindows, out string? startupError);
            _draftSettings = CloneSettings(_settings);
            _appliedSnapshot = SerializeSettings(_draftSettings);
            _lyricsWindow?.Close();
            _lyricsWindow = null;
            if (_settings.LyricsEnabled && _startupInitialized) ShowLyricsWindow();
            if (_settings.StartupPlaybackRecovery &&
                (_startupRecoveryCancellation is null || _startupRecoveryCancellation.IsCancellationRequested))
            {
                _startupRecoveryCancellation?.Dispose();
                _startupRecoveryCancellation = new CancellationTokenSource();
                _ = RunStartupPlaybackRecoveryAsync(_startupRecoveryCancellation.Token);
            }
            else if (!_settings.StartupPlaybackRecovery)
            {
                _startupRecoveryCancellation?.Cancel();
            }
            ApplyButton.IsEnabled = false;
            StatusText = startupOk
                ? "设置已应用并保存。"
                : $"设置已应用并保存；开机启动未能写入注册表（{startupError}），其余设置已生效。";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "ApplySettings");
            StatusText = $"应用设置失败：{ex.Message}";
            MessageBox.Show(this, ex.Message, "应用设置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MarkDraftChanged()
    {
        if (_isLoadingSettings || _discardingChanges) return;
        ApplyButton.IsEnabled = !string.Equals(SerializeSettings(_draftSettings), _appliedSnapshot, StringComparison.Ordinal);
        StatusText = ApplyButton.IsEnabled ? "有未应用的更改。" : "设置与已应用内容一致。";
    }

    private static OverlaySettings CloneSettings(OverlaySettings source) =>
        System.Text.Json.JsonSerializer.Deserialize<OverlaySettings>(System.Text.Json.JsonSerializer.Serialize(source)) ?? new OverlaySettings();

    private static string SerializeSettings(OverlaySettings settings) => System.Text.Json.JsonSerializer.Serialize(settings);

    private static void CopySettings(OverlaySettings source, OverlaySettings destination)
    {
        foreach (var property in typeof(OverlaySettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
            property.SetValue(destination, property.GetValue(source));
    }

    private string GetDraftSettingValue(string tag) => tag switch
    {
        "PreviousTrack" => _draftSettings.KeyboardPrevious,
        "NextTrack" => _draftSettings.KeyboardNext,
        "TogglePlayPause" => _draftSettings.KeyboardToggle,
        "VolumeUp" => _draftSettings.KeyboardVolumeUp,
        "VolumeDown" => _draftSettings.KeyboardVolumeDown,
        _ => string.Empty
    };

    private sealed class DraftHotkeyRegistrar : IHotkeySnapshotRegistrar
    {
        public static DraftHotkeyRegistrar Instance { get; } = new();
        public bool TryRegisterSnapshot(IReadOnlyDictionary<AppAction, string> hotkeys, AppAction requiredAction) => true;
    }

    private void ResetDraftSettings()
    {
        _discardingChanges = true;
        _isLoadingSettings = true;
        try
        {
            _draftSettings = CloneSettings(_settings);
            NormalizeDraftValues();
            LoadSettingsToUi();
            _appliedSnapshot = SerializeSettings(_draftSettings);
            ApplyButton.IsEnabled = false;
        }
        finally { _discardingChanges = false; _isLoadingSettings = false; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        if (!string.Equals(SerializeSettings(_draftSettings), _appliedSnapshot, StringComparison.Ordinal))
        {
            var choice = MessageBox.Show(this, "存在尚未应用的更改。\n\n是：应用并继续关闭\n否：放弃更改并继续关闭\n取消：返回设置窗口", "未应用的更改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (choice == MessageBoxResult.Yes)
            {
                ApplySettings_Click(this, new RoutedEventArgs());
                if (!string.Equals(SerializeSettings(_draftSettings), _appliedSnapshot, StringComparison.Ordinal)) { e.Cancel = true; return; }
            }
            else ResetDraftSettings();
        }
        if (_settings.CloseToTray && _trayIconService.IsIconAvailable)
        {
            e.Cancel = true;
            Hide();
            StatusText = "已隐藏到系统托盘。";
            return;
        }
        if (_settings.CloseToTray)
        {
            // No tray icon to restore from: keep the app reachable from the taskbar instead
            // of hiding it into nothing.
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            StatusText = "托盘图标未注册，已最小化到任务栏。";
            return;
        }
        RequestApplicationExit();
        e.Cancel = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeButtonGlyph();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeButtonGlyph() => MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

    public void ShowSettings()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void RequestApplicationExit(bool isSessionEnding = false)
    {
        if (_isExiting) return;
        _isExiting = true;
        try { _startupRecoveryCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _startupRecoveryCancellation?.Dispose();
        _hotkeyService.Dispose();
        _volumeService.Dispose();
        _lyricsWindow?.Close();
        _trayIconService.Dispose();
        Application.Current.Shutdown();
    }

    private string GetSettingValue(string tag) => tag switch
    {
        "PreviousTrack" => _settings.KeyboardPrevious,
        "NextTrack" => _settings.KeyboardNext,
        "TogglePlayPause" => _settings.KeyboardToggle,
        "VolumeUp" => _settings.KeyboardVolumeUp,
        "VolumeDown" => _settings.KeyboardVolumeDown,
        _ => string.Empty
    };

    private void SetBoxText(string tag, string value)
    {
        switch (tag)
        {
            case "PreviousTrack": PreviousBox.Text = value; break;
            case "NextTrack": NextBox.Text = value; break;
            case "TogglePlayPause": ToggleBox.Text = value; break;
            case "VolumeUp": VolumeUpBox.Text = value; break;
            case "VolumeDown": VolumeDownBox.Text = value; break;
        }
    }

    private static AppAction ParseAction(string tag) => tag switch
    {
        "PreviousTrack" => AppAction.PreviousTrack,
        "NextTrack" => AppAction.NextTrack,
        "TogglePlayPause" => AppAction.TogglePlayPause,
        "VolumeUp" => AppAction.VolumeUp,
        "VolumeDown" => AppAction.VolumeDown,
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, null)
    };

    private static string FormatHotkey(ModifierKeys modifiers, string key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key) => key switch
    {
        Key.Space => "Space",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Enter => "Enter",
        Key.Escape => "Esc",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Home => "Home",
        Key.End => "End",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        _ when key >= Key.F1 && key <= Key.F24 => key.ToString(),
        _ => key.ToString()
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
