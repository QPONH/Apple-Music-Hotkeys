using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay;

public partial class MainWindow : Window, IHotkeySnapshotRegistrar, INotifyPropertyChanged
{
    private readonly OverlaySettingsService _settingsService = new();
    private readonly OverlaySettings _settings;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly SmtcMediaSessionService _mediaService = new();
    private readonly AppleMusicVolumeService _volumeService = new();
    private readonly StartupPlaybackRecoveryService _startupPlaybackRecoveryService = new();
    private CancellationTokenSource? _startupRecoveryCancellation;
    private readonly TrayIconService _trayIconService;
    private TextBox? _capturingBox;
    private bool _isExiting;
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
        DataContext = this;
        InitializeComponent();
        _hotkeyService = new GlobalHotkeyService(this);
        _hotkeyService.ActionRequested += HotkeyService_ActionRequested;
        _trayIconService = new TrayIconService(this, ShowSettings, () => RequestApplicationExit());
        LoadSettingsToUi();
        Loaded += MainWindow_Loaded;
        UpdateMaximizeButtonGlyph();
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        RegisterHotkeys();
        if (_settings.HideWindowOnStartup) Hide();
        if (_settings.StartupPlaybackRecovery)
        {
            _startupRecoveryCancellation = new CancellationTokenSource();
            _ = RunStartupPlaybackRecoveryAsync(_startupRecoveryCancellation.Token);
        }
    }

    private void LoadSettingsToUi()
    {
        PreviousBox.Text = _settings.KeyboardPrevious;
        NextBox.Text = _settings.KeyboardNext;
        ToggleBox.Text = _settings.KeyboardToggle;
        VolumeUpBox.Text = _settings.KeyboardVolumeUp;
        VolumeDownBox.Text = _settings.KeyboardVolumeDown;
        CloseToTrayCheckBox.IsChecked = _settings.CloseToTray;
        HideOnStartupCheckBox.IsChecked = _settings.HideWindowOnStartup;
        StartWithWindowsCheckBox.IsChecked = StartupService.IsEnabled;
        StartupPlaybackRecoveryCheckBox.IsChecked = _settings.StartupPlaybackRecovery;
        RecoveryWaitSecondsBox.Text = _settings.RecoveryWaitSeconds.ToString();
        RecoveryQueueXBox.Text = _settings.RecoveryQueueXPercent.ToString();
        RecoveryQueueYBox.Text = _settings.RecoveryQueueYPercent.ToString();
        RecoverySongXBox.Text = _settings.RecoverySongXPercent.ToString();
        RecoverySongYBox.Text = _settings.RecoverySongYPercent.ToString();
        RecoveryQueueMethodBox.SelectedValue = _settings.RecoveryQueueMethod;
        RecoverySongMethodBox.SelectedValue = _settings.RecoverySongMethod;
    }

    private void HotkeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box) return;

        // Windows consumes registered global hotkey chords before WPF can capture them.
        // Temporarily unregister all shortcuts while recording a new binding.
        _hotkeyService.Clear();
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
            box.Text = GetSettingValue((string)box.Tag);
            _capturingBox = null;
            RegisterHotkeys();
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
            _settings, ParseAction((string)box.Tag), hotkey, this);
        StatusText = result.Message;

        if (result.Success)
        {
            box.Text = hotkey;
            _settingsService.Save(_settings);
            _capturingBox = null;
        }
        else
        {
            // Keep capture active after a conflict, and keep the OS from swallowing
            // the next attempt with the old registered shortcut.
            _hotkeyService.Clear();
        }

        e.Handled = true;
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box || _capturingBox != box) return;

        box.Text = GetSettingValue((string)box.Tag);
        _capturingBox = null;
        RegisterHotkeys();
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
        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(_settings, ParseAction(tag), string.Empty, this);
        if (result.Success)
        {
            SetBoxText(tag, string.Empty);
            _settingsService.Save(_settings);
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
        _settings.CloseToTray = CloseToTrayCheckBox.IsChecked == true;
        _settingsService.Save(_settings);
    }

    private void HideOnStartup_Changed(object sender, RoutedEventArgs e)
    {
        _settings.HideWindowOnStartup = HideOnStartupCheckBox.IsChecked == true;
        _settingsService.Save(_settings);
    }

    private void StartupPlaybackRecovery_Changed(object sender, RoutedEventArgs e)
    {
        _settings.StartupPlaybackRecovery = StartupPlaybackRecoveryCheckBox.IsChecked == true;
        _settingsService.Save(_settings);
        StatusText = _settings.StartupPlaybackRecovery
            ? "已启用开机恢复播放。请确保 Apple Music 登录 Windows 后自动启动，并保持最大化窗口。"
            : "已关闭开机恢复播放。";
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
                _settings.RecoveryQueueXPercent = position.x;
                _settings.RecoveryQueueYPercent = position.y;
                RecoveryQueueXBox.Text = position.x.ToString();
                RecoveryQueueYBox.Text = position.y.ToString();
            }
            else
            {
                _settings.RecoverySongXPercent = position.x;
                _settings.RecoverySongYPercent = position.y;
                RecoverySongXBox.Text = position.x.ToString();
                RecoverySongYBox.Text = position.y.ToString();
            }
            _settingsService.Save(_settings);
            StatusText = $"位置已捕获并保存：X={position.x}%，Y={position.y}%。";
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
        _settings.RecoveryQueueMethod = RecoveryQueueMethodBox.SelectedValue.ToString() ?? "Keyboard";
        _settings.RecoverySongMethod = RecoverySongMethodBox.SelectedValue.ToString() ?? "Mouse";
        _settingsService.Save(_settings);
    }

    private void RecoveryWaitSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        _settings.RecoveryWaitSeconds = int.TryParse(RecoveryWaitSecondsBox.Text, out int seconds)
            ? Math.Clamp(seconds, 0, 600) : 2;
        RecoveryWaitSecondsBox.Text = _settings.RecoveryWaitSeconds.ToString();
        _settingsService.Save(_settings);
    }

    private void RecoveryCoordinates_LostFocus(object sender, RoutedEventArgs e)
    {
        _settings.RecoveryQueueXPercent = ReadPercent(RecoveryQueueXBox, 85);
        _settings.RecoveryQueueYPercent = ReadPercent(RecoveryQueueYBox, 3);
        _settings.RecoverySongXPercent = ReadPercent(RecoverySongXBox, 24);
        _settings.RecoverySongYPercent = ReadPercent(RecoverySongYBox, 12);
        RecoveryQueueXBox.Text = _settings.RecoveryQueueXPercent.ToString();
        RecoveryQueueYBox.Text = _settings.RecoveryQueueYPercent.ToString();
        RecoverySongXBox.Text = _settings.RecoverySongXPercent.ToString();
        RecoverySongYBox.Text = _settings.RecoverySongYPercent.ToString();
        _settingsService.Save(_settings);
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
<<<<<<< HEAD
            if (!cancellationToken.IsCancellationRequested && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(() => StatusText = "已执行开机恢复播放操作；请确认歌曲和队列是否正确。 ");
=======
            if (!cancellationToken.IsCancellationRequested)
                Dispatcher.Invoke(() => StatusText = "已执行开机恢复播放操作；请确认歌曲和队列是否正确。 ");
>>>>>>> 9c4da6fa662428a2fa5a53422d751ceae9329a8a
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
<<<<<<< HEAD
            CrashLog.Write(ex, "StartupPlaybackRecovery");
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(() => StatusText = $"开机恢复播放操作失败：{ex.Message}");
=======
            Dispatcher.Invoke(() => StatusText = $"开机恢复播放操作失败：{ex.Message}");
>>>>>>> 9c4da6fa662428a2fa5a53422d751ceae9329a8a
        }
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = StartWithWindowsCheckBox.IsChecked == true;
        try
        {
            StartupService.SetEnabled(enabled);
            _settings.StartWithWindows = enabled;
            _settingsService.Save(_settings);
            StatusText = enabled ? "已设置为 Windows 开机自动启动。" : "已取消 Windows 开机自动启动。";
        }
        catch (Exception ex)
        {
            StartWithWindowsCheckBox.IsChecked = !enabled;
            StatusText = $"设置开机自启动失败：{ex.Message}";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        if (_settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            StatusText = "已隐藏到系统托盘。";
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
<<<<<<< HEAD
        try { _startupRecoveryCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _startupRecoveryCancellation?.Dispose();
=======
        _startupRecoveryCancellation?.Cancel();
>>>>>>> 9c4da6fa662428a2fa5a53422d751ceae9329a8a
        _hotkeyService.Dispose();
        _volumeService.Dispose();
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
