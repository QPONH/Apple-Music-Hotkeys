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
        switch (action)
        {
            case AppAction.PreviousTrack: await _mediaService.PreviousAsync(); break;
            case AppAction.NextTrack: await _mediaService.NextAsync(); break;
            case AppAction.TogglePlayPause: await _mediaService.TogglePlayPauseAsync(); break;
            case AppAction.VolumeUp: ChangeVolume(0.05f); break;
            case AppAction.VolumeDown: ChangeVolume(-0.05f); break;
        }
    }

    private void ChangeVolume(float delta)
    {
        if (_volumeService.TryChange(delta, out float volume))
            StatusText = $"Apple Music 音量：{volume:P0}";
        else
            StatusText = "没有找到正在输出声音的 Apple Music 音频会话。请先播放音乐。";
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await _mediaService.PreviousAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _mediaService.NextAsync();
    private async void Toggle_Click(object sender, RoutedEventArgs e) => await _mediaService.TogglePlayPauseAsync();

    private async void TestMedia_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.TogglePlayPauseAsync();
        StatusText = "已发送播放 / 暂停指令到 Apple Music。";
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
