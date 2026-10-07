using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay;

public partial class MainWindow : Window, IHotkeySnapshotRegistrar
{
    private readonly OverlaySettingsService _settingsService = new();
    private readonly OverlaySettings _settings;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly SmtcMediaSessionService _mediaService = new();
    private readonly AppleMusicVolumeService _volumeService = new();
    private readonly TrayIconService _trayIconService;
    private TextBox? _capturingBox;
    private bool _isExiting;

    public MainWindow()
    {
        // Load settings before InitializeComponent because the CheckBox Checked/Unchecked
        // handlers can fire while WPF is applying XAML values.
        _settings = _settingsService.Load();
        InitializeComponent();
        _hotkeyService = new GlobalHotkeyService(this);
        _hotkeyService.ActionRequested += HotkeyService_ActionRequested;
        _trayIconService = new TrayIconService(this, ShowSettings, () => RequestApplicationExit());

        LoadSettingsToUi();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RegisterHotkeys();
        if (_settings.HideWindowOnStartup)
        {
            Hide();
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
    }

    private void HotkeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        _capturingBox = box;
        box.Focus();
        box.SelectAll();
        StatusText.Text = "正在设置快捷键：按下 Ctrl / Alt / Shift / Win + 一个按键；松开后自动保存。";
        e.Handled = true;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || _capturingBox != box)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            box.Text = GetSettingValue((string)box.Tag);
            _capturingBox = null;
            StatusText.Text = "已取消。";
            e.Handled = true;
            return;
        }

        e.Handled = true;
    }

    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || _capturingBox != box)
        {
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        string? mainKey = FormatKey(key);
        if (string.IsNullOrWhiteSpace(mainKey) || modifiers == ModifierKeys.None)
        {
            return;
        }

        string hotkey = FormatHotkey(modifiers, mainKey);
        AppAction action = ParseAction((string)box.Tag);
        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(_settings, action, hotkey, this);
        if (result.Success)
        {
            box.Text = hotkey;
            _settingsService.Save(_settings);
            StatusText.Text = $"已保存：{hotkey}";
            _capturingBox = null;
        }
        else
        {
            StatusText.Text = result.Message;
        }

        e.Handled = true;
    }

    private void DeleteHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        string tag = (string)button.Tag;
        AppAction action = ParseAction(tag);
        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(_settings, action, string.Empty, this);
        if (result.Success)
        {
            SetBoxText(tag, string.Empty);
            _settingsService.Save(_settings);
            StatusText.Text = "快捷键已清除。";
        }
        else
        {
            StatusText.Text = result.Message;
        }
    }

    public bool TryRegisterSnapshot(IReadOnlyDictionary<AppAction, string> hotkeys)
    {
        return RegisterHotkeySnapshotDirect(hotkeys);
    }

    private bool RegisterHotkeys()
    {
        return RegisterHotkeySnapshotDirect(KeyboardHotkeyBindingManager.CreateSnapshot(_settings));
    }

    private bool RegisterHotkeySnapshotDirect(IReadOnlyDictionary<AppAction, string> hotkeys)
    {
        _hotkeyService.Clear();
        foreach ((AppAction action, string hotkeyText) in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(hotkeyText))
            {
                continue;
            }

            if (!_hotkeyService.Register(action, hotkeyText))
            {
                _hotkeyService.Clear();
                return false;
            }
        }

        return true;
    }

    private async void HotkeyService_ActionRequested(object? sender, AppAction action)
    {
        switch (action)
        {
            case AppAction.PreviousTrack:
                await _mediaService.PreviousAsync();
                break;
            case AppAction.NextTrack:
                await _mediaService.NextAsync();
                break;
            case AppAction.TogglePlayPause:
                await _mediaService.TogglePlayPauseAsync();
                break;
            case AppAction.VolumeUp:
                ChangeVolume(0.05f);
                break;
            case AppAction.VolumeDown:
                ChangeVolume(-0.05f);
                break;
        }
    }

    private void ChangeVolume(float delta)
    {
        if (_volumeService.TryChange(delta, out float volume))
        {
            StatusText.Text = $"Apple Music 音量：{volume:P0}";
        }
        else
        {
            StatusText.Text = "没有找到正在输出声音的 Apple Music 音频会话。请先播放音乐。";
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

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        if (_settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        RequestApplicationExit();
        e.Cancel = true;
    }

    public void ShowSettings()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void RequestApplicationExit(bool isSessionEnding = false)
    {
        if (_isExiting)
        {
            return;
        }

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

    private static string? FormatKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return key.ToString()[1..];
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return key.ToString();
        }

        return key switch
        {
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Escape => "Esc",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            _ => null
        };
    }

    private static string FormatHotkey(ModifierKeys modifiers, string key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key);
        return string.Join('+', parts);
    }
}
