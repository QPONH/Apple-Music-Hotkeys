using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;
using AppleMusicOverlay.ViewModels;
using AppleMusicOverlay.Views;

namespace AppleMusicOverlay;

public partial class MainWindow : Window
{
    private const string HotkeyCapturePrompt = "按下想要的快捷键";
    private const string HotkeyUnsetText = "未设置";
    private const string HotkeyEditingStatus = "快捷键修改中：按下 Ctrl / Alt / Shift / Win + 一个按键，Esc 取消。";
    private const string HotkeyNeedModifierStatus = "请使用 Ctrl / Alt / Shift / Win 组合键。";
    private const string HotkeyNeedMainKeyStatus = "请再按一个字母、数字、方向键或功能键。";
    private const string MaximizeGlyph = "\uE922";
    private const string RestoreGlyph = "\uE923";
    private const double NormalWindowFrameRadius = 20;
    private readonly MainViewModel _viewModel;
    private readonly IMediaSessionService _mediaService;
    private readonly IMediaSessionSourceService _sourceService;
    private readonly TrackMonitor _trackMonitor;
    private readonly OverlayWindow _overlayWindow;
    private readonly ShadowWindow _shadowWindow;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly TrayIconService _trayIconService;
    private bool _isExiting;
    private bool _isRefreshingSources;
    private TextBox? _capturingHotkeyBox;
    private string? _capturingHotkeyOriginalText;
    private string? _pendingHotkeyText;
    private bool _isHotkeyCapturePanelShowing;
    private readonly DispatcherTimer _hotkeyCaptureAutoHideTimer = new();
    private readonly HashSet<Key> _pressedHotkeyKeys = new();

    public MainWindow()
    {
        LogStartup("ctor: before InitializeComponent");
        InitializeComponent();
        LogStartup("ctor: after InitializeComponent");
        _viewModel = new MainViewModel(new OverlaySettingsService());
        LogStartup("ctor: view model ready");
        var mediaService = new SmtcMediaSessionService();
        _mediaService = mediaService;
        _sourceService = mediaService;
        ApplyPreferredSource();
        LogStartup("ctor: media service ready");
        _trackMonitor = new TrackMonitor(_mediaService);
        _overlayWindow = new OverlayWindow();
        _overlayWindow.ApplySettings(_viewModel.Settings);
        _shadowWindow = new ShadowWindow();
        LogStartup("ctor: overlay ready");
        _hotkeyService = new GlobalHotkeyService(this);
        _trayIconService = new TrayIconService(this, () => _ = ShowCurrentTrackOverlayAsync(), ExitApplication);
        LogStartup("ctor: tray ready");
        DataContext = _viewModel;
        DisplayHotkeyBoxValues();
        _hotkeyCaptureAutoHideTimer.Tick += (_, _) =>
        {
            _hotkeyCaptureAutoHideTimer.Stop();
            HideHotkeyCapturePanel();
        };

        Activated += (_, _) =>
        {
            _shadowWindow.ApplyActiveState(true);
            SyncShadowWindow();
        };
        Deactivated += (_, _) =>
        {
            _shadowWindow.ApplyActiveState(false);
            SyncShadowWindow();
        };
        LocationChanged += (_, _) => SyncShadowWindow();
        SizeChanged += (_, _) => SyncShadowWindow();
        IsVisibleChanged += (_, _) => SyncShadowWindow();
        StateChanged += (_, _) =>
        {
            UpdateMaximizeButtonGlyph();
            ApplyWindowShellState();
            SyncShadowWindow();
        };
        SourceInitialized += (_, _) => LogStartup($"source initialized: {new WindowInteropHelper(this).Handle}");
        ContentRendered += (_, _) =>
        {
            LogStartup($"content rendered: visible={IsVisible} state={WindowState}");
            SyncShadowWindow();
        };
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        _trackMonitor.TrackRead += (_, track) => Dispatcher.Invoke(() => _viewModel.ApplyTrack(track));
        _trackMonitor.TrackChanged += (_, track) => Dispatcher.Invoke(() => _ = _overlayWindow.ShowTrackAsync(track));
        _trackMonitor.TrackRefreshed += (_, track) => Dispatcher.Invoke(() => _overlayWindow.UpdateTrack(track));
        _hotkeyService.ActionRequested += HotkeyService_ActionRequested;
        UpdateMaximizeButtonGlyph();
        ApplyWindowShellState();
        LogStartup("ctor: complete");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting && _viewModel.Settings.CloseToTray)
        {
            e.Cancel = true;
            _shadowWindow.Hide();
            Hide();
            _viewModel.SetStatus("已最小化到托盘");
            return;
        }

        base.OnClosing(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LogStartup("loaded: begin");
        await RefreshSourcesAsync();
        LogStartup("loaded: after refresh sources");
        RegisterHotkeys();
        LogStartup("loaded: after register hotkeys");
        _trackMonitor.Start();
        await _trackMonitor.PollOnceAsync();
        LogStartup("loaded: complete");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSourcesAsync();
        await _trackMonitor.PollOnceAsync();
    }

    private async void RefreshSources_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSourcesAsync();
    }

    private async void CaptureSourceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isRefreshingSources)
        {
            return;
        }

        ApplyPreferredSource();
        _viewModel.Save();
        await _trackMonitor.PollOnceAsync();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.PreviousAsync();
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.TogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.NextAsync();
    }

    private async void ShowCurrentTrack_Click(object sender, RoutedEventArgs e)
    {
        await ShowCurrentTrackOverlayAsync();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove can throw when the mouse state changes during window chrome hit testing.
            }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateHotkeyEdits(out string validationMessage))
        {
            ShowHotkeyCapturePanel(validationMessage, isConflict: true);
            return;
        }

        ApplyPendingHotkeyEdits();
        _viewModel.Save();
        ApplyPreferredSource();
        _overlayWindow.ApplySettings(_viewModel.Settings);
        bool registered = RegisterHotkeys();
        DisplayHotkeyBoxValues();

        if (registered)
        {
            ShowHotkeyCapturePanel("快捷键已保存并生效。", autoHide: true, autoHideMilliseconds: 2200);
        }
        else
        {
            ShowHotkeyCapturePanel("部分快捷键未注册，可能已被系统或其他应用占用。", isConflict: true);
        }
    }

    private void SettingsControl_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_overlayWindow == null)
        {
            return;
        }

        _overlayWindow.ApplySettings(_viewModel.Settings);
    }

    private void PauseOverlay_Changed(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null || sender is not CheckBox pauseOverlayToggle)
        {
            return;
        }

        bool isPaused = pauseOverlayToggle.IsChecked == true;
        _viewModel.Settings.PauseOverlay = isPaused;
        _overlayWindow.ApplySettings(_viewModel.Settings);
        if (isPaused)
        {
            TrackInfo? track = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
            if (track != null)
            {
                _ = _overlayWindow.ShowTrackAsync(track);
            }
        }

        _ = Dispatcher.InvokeAsync(() => _viewModel.Save(), DispatcherPriority.Background);
    }

    private void MouseAutoHide_Changed(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null || sender is not CheckBox autoHideToggle)
        {
            return;
        }

        _viewModel.Settings.AutoHideOnMouseNear = autoHideToggle.IsChecked == true;
        _overlayWindow.ApplySettings(_viewModel.Settings);
        _ = Dispatcher.InvokeAsync(() => _viewModel.Save(), DispatcherPriority.Background);
    }

    private void PositionOverlay_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetStatus("悬浮窗位置调整将在下一阶段开放。");
    }

    private void DeleteHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        TextBox? box = GetHotkeyBox(button.Tag as string);
        if (box == null)
        {
            return;
        }

        if (ReferenceEquals(box, _capturingHotkeyBox))
        {
            ClearHotkeyCapture();
        }

        box.Text = HotkeyUnsetText;
        ShowHotkeyCapturePanel("已删除该快捷键绑定。修改完成后请点击保存。");
    }

    private void HotkeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;

        if (!ReferenceEquals(_capturingHotkeyBox, box))
        {
            if (_capturingHotkeyBox != null)
            {
                RestoreHotkeyBox(_capturingHotkeyBox);
                ClearHotkeyCapture();
            }

            BeginHotkeyCapture(box);
        }

        box.Focusable = true;
        box.Focus();
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && ReferenceEquals(box, _capturingHotkeyBox))
        {
            RestoreHotkeyBox(box);
            ClearHotkeyCapture();
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;

        Key key = NormalizeKey(e);
        if (key == Key.Escape)
        {
            RestoreHotkeyBox(box);
            ClearHotkeyCapture();
            Keyboard.ClearFocus();
            return;
        }

        if (key == Key.None)
        {
            return;
        }

        if (e.IsRepeat && _pressedHotkeyKeys.Contains(key))
        {
            return;
        }

        _pressedHotkeyKeys.Add(key);
        UpdateHotkeyCapture(box);
    }

    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;

        Key key = NormalizeKey(e);
        if (key != Key.None)
        {
            _pressedHotkeyKeys.Remove(key);
        }

        if (_pressedHotkeyKeys.Count == 0)
        {
            if (TryCommitPendingHotkey(box))
            {
                ShowHotkeyCapturePanel($"已录入：{box.Text}。修改完成后请点击保存。", autoHide: true, autoHideMilliseconds: 1200);
                ClearHotkeyCapture(hidePanel: false);
                Keyboard.ClearFocus();
            }
            else
            {
                box.Text = HotkeyCapturePrompt;
            }
            return;
        }

        UpdateHotkeyCapture(box);
    }

    private void BeginHotkeyCapture(TextBox box)
    {
        if (_capturingHotkeyBox != null && !ReferenceEquals(_capturingHotkeyBox, box))
        {
            RestoreHotkeyBox(_capturingHotkeyBox);
            ClearHotkeyCapture();
        }

        _capturingHotkeyBox = box;
        _capturingHotkeyOriginalText = box.Text;
        _pendingHotkeyText = null;
        _pressedHotkeyKeys.Clear();
        _hotkeyService.Clear();
        box.Focusable = true;
        box.Text = HotkeyCapturePrompt;
        box.SelectAll();
        ShowHotkeyCapturePanel(HotkeyEditingStatus);
    }

    private void RestoreHotkeyBox(TextBox box)
    {
        if (box.Text == HotkeyCapturePrompt || box.Text == "请按组合键" || _pressedHotkeyKeys.Count > 0)
        {
            box.Text = GetHotkeySetting(box.Tag as string);
        }
    }

    private void ClearHotkeyCapture(bool hidePanel = true)
    {
        TextBox? previousBox = _capturingHotkeyBox;
        _capturingHotkeyBox = null;
        _capturingHotkeyOriginalText = null;
        _pendingHotkeyText = null;
        _pressedHotkeyKeys.Clear();
        if (previousBox != null)
        {
            previousBox.Focusable = false;
        }

        if (hidePanel)
        {
            HideHotkeyCapturePanel();
        }

        RegisterHotkeys(updateStatus: false);
    }

    private void UpdateHotkeyCapture(TextBox box)
    {
        string pressedText = FormatPressedKeys(_pressedHotkeyKeys);
        box.Text = string.IsNullOrWhiteSpace(pressedText) ? HotkeyCapturePrompt : pressedText;

        if (TryCreateHotkeyText(_pressedHotkeyKeys, out string hotkeyText))
        {
            _pendingHotkeyText = hotkeyText;
            ShowHotkeyCapturePanel($"已捕获：{hotkeyText}。松开全部按键后录入，点击保存后生效。");
            return;
        }

        ShowHotkeyCapturePanel(_pressedHotkeyKeys.Any(IsModifierKey)
            ? (_pendingHotkeyText == null ? HotkeyNeedMainKeyStatus : $"松开全部按键后录入：{_pendingHotkeyText}。")
            : HotkeyNeedModifierStatus);
    }

    private bool TryCommitPendingHotkey(TextBox box)
    {
        if (string.IsNullOrWhiteSpace(_pendingHotkeyText))
        {
            ShowHotkeyCapturePanel(HotkeyNeedModifierStatus);
            return false;
        }

        box.Text = _pendingHotkeyText;
        _capturingHotkeyOriginalText = _pendingHotkeyText;
        _pendingHotkeyText = null;
        return true;
    }

    private void SetHotkeySetting(string? key, string hotkeyText)
    {
        switch (key)
        {
            case "KeyboardPrevious":
                _viewModel.Settings.KeyboardPrevious = hotkeyText;
                break;
            case "KeyboardNext":
                _viewModel.Settings.KeyboardNext = hotkeyText;
                break;
            case "KeyboardToggle":
                _viewModel.Settings.KeyboardToggle = hotkeyText;
                break;
            case "KeyboardTestOverlay":
                _viewModel.Settings.KeyboardTestOverlay = hotkeyText;
                break;
        }
    }

    private void ApplyPendingHotkeyEdits()
    {
        ApplyHotkeyEdit("KeyboardPrevious", KeyboardPreviousBox.Text);
        ApplyHotkeyEdit("KeyboardNext", KeyboardNextBox.Text);
        ApplyHotkeyEdit("KeyboardToggle", KeyboardToggleBox.Text);
        ApplyHotkeyEdit("KeyboardTestOverlay", KeyboardTestOverlayBox.Text);
    }

    private bool ValidateHotkeyEdits(out string message)
    {
        (string FirstLabel, string SecondLabel, string Hotkey)? duplicate = FindDuplicateHotkey();
        if (duplicate != null)
        {
            message = $"快捷键重复：{duplicate.Value.Hotkey} 同时用于「{duplicate.Value.FirstLabel}」和「{duplicate.Value.SecondLabel}」。请修改后再保存。";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private (string FirstLabel, string SecondLabel, string Hotkey)? FindDuplicateHotkey()
    {
        Dictionary<string, string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string hotkey) in GetPendingHotkeyEdits())
        {
            if (string.IsNullOrWhiteSpace(hotkey))
            {
                continue;
            }

            if (seen.TryGetValue(hotkey, out string? existingKey))
            {
                return (GetHotkeyLabel(existingKey), GetHotkeyLabel(key), hotkey);
            }

            seen[hotkey] = key;
        }

        return null;
    }

    private Dictionary<string, string> GetPendingHotkeyEdits()
    {
        return new Dictionary<string, string>
        {
            ["KeyboardPrevious"] = NormalizeCapturedHotkeyText(KeyboardPreviousBox.Text, _viewModel.Settings.KeyboardPrevious),
            ["KeyboardNext"] = NormalizeCapturedHotkeyText(KeyboardNextBox.Text, _viewModel.Settings.KeyboardNext),
            ["KeyboardToggle"] = NormalizeCapturedHotkeyText(KeyboardToggleBox.Text, _viewModel.Settings.KeyboardToggle),
            ["KeyboardTestOverlay"] = NormalizeCapturedHotkeyText(KeyboardTestOverlayBox.Text, _viewModel.Settings.KeyboardTestOverlay)
        };
    }

    private void ApplyHotkeyEdit(string key, string text)
    {
        SetHotkeySetting(key, NormalizeCapturedHotkeyText(text, GetHotkeySetting(key)));
    }

    private string GetHotkeySetting(string? key)
    {
        return key switch
        {
            "KeyboardPrevious" => _viewModel.Settings.KeyboardPrevious,
            "KeyboardNext" => _viewModel.Settings.KeyboardNext,
            "KeyboardToggle" => _viewModel.Settings.KeyboardToggle,
            "KeyboardTestOverlay" => _viewModel.Settings.KeyboardTestOverlay,
            _ => string.Empty
        };
    }

    private string? FindHotkeyConflict(string? currentKey, string hotkeyText)
    {
        Dictionary<string, string> hotkeys = new()
        {
            ["KeyboardPrevious"] = GetHotkeyBoxText("KeyboardPrevious"),
            ["KeyboardNext"] = GetHotkeyBoxText("KeyboardNext"),
            ["KeyboardToggle"] = GetHotkeyBoxText("KeyboardToggle"),
            ["KeyboardTestOverlay"] = GetHotkeyBoxText("KeyboardTestOverlay")
        };

        foreach ((string key, string existing) in hotkeys)
        {
            if (key == currentKey)
            {
                continue;
            }

            if (existing.Equals(hotkeyText, StringComparison.OrdinalIgnoreCase))
            {
                return key switch
                {
                    "KeyboardPrevious" => "上一首",
                    "KeyboardNext" => "下一首",
                    "KeyboardToggle" => "播放 / 暂停",
                    "KeyboardTestOverlay" => "测试悬浮窗",
                    _ => "其他操作"
                };
            }
        }

        return null;
    }

    private string GetHotkeyBoxText(string key)
    {
        return key switch
        {
            "KeyboardPrevious" => NormalizeCapturedHotkeyText(KeyboardPreviousBox.Text, _viewModel.Settings.KeyboardPrevious),
            "KeyboardNext" => NormalizeCapturedHotkeyText(KeyboardNextBox.Text, _viewModel.Settings.KeyboardNext),
            "KeyboardToggle" => NormalizeCapturedHotkeyText(KeyboardToggleBox.Text, _viewModel.Settings.KeyboardToggle),
            "KeyboardTestOverlay" => NormalizeCapturedHotkeyText(KeyboardTestOverlayBox.Text, _viewModel.Settings.KeyboardTestOverlay),
            _ => string.Empty
        };
    }

    private static string NormalizeCapturedHotkeyText(string text, string fallback)
    {
        return string.IsNullOrWhiteSpace(text) || text == HotkeyCapturePrompt || text == HotkeyUnsetText ? string.Empty : text.Trim();
    }

    private TextBox? GetHotkeyBox(string? key)
    {
        return key switch
        {
            "KeyboardPrevious" => KeyboardPreviousBox,
            "KeyboardNext" => KeyboardNextBox,
            "KeyboardToggle" => KeyboardToggleBox,
            "KeyboardTestOverlay" => KeyboardTestOverlayBox,
            _ => null
        };
    }

    private static string GetHotkeyLabel(string key)
    {
        return key switch
        {
            "KeyboardPrevious" => "上一首",
            "KeyboardNext" => "下一首",
            "KeyboardToggle" => "播放 / 暂停",
            "KeyboardTestOverlay" => "测试悬浮窗",
            _ => "其他操作"
        };
    }

    private void DisplayHotkeyBoxValues()
    {
        KeyboardPreviousBox.Text = DisplayHotkeyText(_viewModel.Settings.KeyboardPrevious);
        KeyboardNextBox.Text = DisplayHotkeyText(_viewModel.Settings.KeyboardNext);
        KeyboardToggleBox.Text = DisplayHotkeyText(_viewModel.Settings.KeyboardToggle);
        KeyboardTestOverlayBox.Text = DisplayHotkeyText(_viewModel.Settings.KeyboardTestOverlay);
    }

    private static string DisplayHotkeyText(string hotkeyText)
    {
        return string.IsNullOrWhiteSpace(hotkeyText) ? HotkeyUnsetText : hotkeyText;
    }

    private void ShowHotkeyCapturePanel(string statusText, bool isConflict = false, bool autoHide = false, int autoHideMilliseconds = 0)
    {
        _hotkeyCaptureAutoHideTimer.Stop();
        HotkeyCaptureTitleText.Text = isConflict ? "快捷键提示" : "快捷键修改";
        HotkeyCaptureStatusText.Text = statusText;
        HotkeyCaptureTitleText.Foreground = isConflict
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xB4))
            : (Brush)FindResource("TextPrimaryBrush");
        HotkeyCaptureStatusText.Foreground = isConflict
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0xC4))
            : (Brush)FindResource("TextSecondaryBrush");
        BeginHotkeyCapturePanelAnimation(true);

        if (autoHide)
        {
            _hotkeyCaptureAutoHideTimer.Interval = TimeSpan.FromMilliseconds(autoHideMilliseconds <= 0 ? 2200 : autoHideMilliseconds);
            _hotkeyCaptureAutoHideTimer.Start();
        }
    }

    private void HideHotkeyCapturePanel()
    {
        _hotkeyCaptureAutoHideTimer.Stop();
        BeginHotkeyCapturePanelAnimation(false);
    }

    private void BeginHotkeyCapturePanelAnimation(bool show)
    {
        bool wasVisible = HotkeyCapturePanel.Visibility == Visibility.Visible && _isHotkeyCapturePanelShowing;
        _isHotkeyCapturePanelShowing = show;
        Duration duration = new(TimeSpan.FromMilliseconds(show ? 220 : 150));
        IEasingFunction easing = new CubicEase
        {
            EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        if (show)
        {
            HotkeyCapturePanel.Visibility = Visibility.Visible;
            HotkeyCaptureContent.Measure(new Size(HotkeyCapturePanel.ActualWidth > 0 ? HotkeyCapturePanel.ActualWidth : 152, double.PositiveInfinity));
        }

        double startHeight = wasVisible ? Math.Max(HotkeyCapturePanel.ActualHeight, HotkeyCapturePanel.MaxHeight) : 0;
        double targetHeight = show ? Math.Ceiling(HotkeyCaptureContent.DesiredSize.Height) : 0;
        if (show && !wasVisible)
        {
            HotkeyCapturePanel.MaxHeight = startHeight;
        }

        DoubleAnimation opacityAnimation = new(show ? 1 : 0, duration)
        {
            EasingFunction = easing
        };
        DoubleAnimation heightAnimation = new(startHeight, targetHeight, duration)
        {
            EasingFunction = easing
        };

        if (!show)
        {
            heightAnimation.Completed += (_, _) =>
            {
                if (!_isHotkeyCapturePanelShowing)
                {
                    HotkeyCapturePanel.Visibility = Visibility.Collapsed;
                    HotkeyCaptureStatusText.Text = string.Empty;
                    HotkeyCapturePanel.MaxHeight = 0;
                }
            };
        }

        HotkeyCapturePanel.BeginAnimation(FrameworkElement.MaxHeightProperty, heightAnimation);
        HotkeyCapturePanel.BeginAnimation(OpacityProperty, opacityAnimation);
        HotkeyCaptureScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = easing });
        HotkeyCaptureScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(show ? 1 : 0.88, duration) { EasingFunction = easing });
        HotkeyCaptureTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(show ? 0 : -8, duration) { EasingFunction = easing });
    }

    private void HotkeyPage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        CancelHotkeyCaptureIfClickOutside(e.OriginalSource as DependencyObject);
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        CancelHotkeyCaptureIfClickOutside(e.OriginalSource as DependencyObject);
    }

    private void CancelHotkeyCaptureIfClickOutside(DependencyObject? source)
    {
        if (_capturingHotkeyBox == null || IsClickInsideCurrentHotkeyBox(source))
        {
            return;
        }

        RestoreHotkeyBox(_capturingHotkeyBox);
        ClearHotkeyCapture();
        Keyboard.ClearFocus();
    }

    private bool IsClickInsideCurrentHotkeyBox(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, _capturingHotkeyBox))
            {
                return true;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    }

    private static Key NormalizeKey(KeyEventArgs e)
    {
        return e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key
        };
    }

    private static bool TryCreateHotkeyText(IEnumerable<Key> keys, out string hotkeyText)
    {
        Key[] keyArray = keys.ToArray();
        bool hasModifier = keyArray.Any(IsModifierKey);
        Key? mainKey = keyArray.FirstOrDefault(key => !IsModifierKey(key));
        if (!hasModifier || mainKey is null or Key.None)
        {
            hotkeyText = string.Empty;
            return false;
        }

        var modifiers = ModifierKeys.None;
        if (keyArray.Any(key => key is Key.LeftCtrl or Key.RightCtrl))
        {
            modifiers |= ModifierKeys.Control;
        }

        if (keyArray.Any(key => key is Key.LeftShift or Key.RightShift))
        {
            modifiers |= ModifierKeys.Shift;
        }

        if (keyArray.Any(key => key is Key.LeftAlt or Key.RightAlt))
        {
            modifiers |= ModifierKeys.Alt;
        }

        if (keyArray.Any(key => key is Key.LWin or Key.RWin))
        {
            modifiers |= ModifierKeys.Windows;
        }

        hotkeyText = FormatHotkey(modifiers, mainKey.Value);
        return true;
    }

    private static string FormatPressedKeys(IEnumerable<Key> keys)
    {
        List<string> parts = new();
        Key[] keyArray = keys.ToArray();
        if (keyArray.Any(key => key is Key.LeftCtrl or Key.RightCtrl))
        {
            parts.Add("Ctrl");
        }

        if (keyArray.Any(key => key is Key.LeftShift or Key.RightShift))
        {
            parts.Add("Shift");
        }

        if (keyArray.Any(key => key is Key.LeftAlt or Key.RightAlt))
        {
            parts.Add("Alt");
        }

        if (keyArray.Any(key => key is Key.LWin or Key.RWin))
        {
            parts.Add("Win");
        }

        parts.AddRange(keyArray.Where(key => !IsModifierKey(key)).Select(FormatKey));
        return string.Join("+", parts);
    }

    private static string FormatHotkey(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(key));
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key)
    {
        int keyValue = (int)key;
        if (keyValue >= (int)Key.A && keyValue <= (int)Key.Z)
        {
            return key.ToString();
        }

        if (keyValue >= (int)Key.D0 && keyValue <= (int)Key.D9)
        {
            return (keyValue - (int)Key.D0).ToString();
        }

        if (keyValue >= (int)Key.NumPad0 && keyValue <= (int)Key.NumPad9)
        {
            return (keyValue - (int)Key.NumPad0).ToString();
        }

        return key switch
        {
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            _ => key.ToString()
        };
    }

    private bool RegisterHotkeys(bool updateStatus = true)
    {
        _hotkeyService.Clear();
        bool previous = RegisterOptionalHotkey(AppAction.PreviousTrack, _viewModel.Settings.KeyboardPrevious);
        bool next = RegisterOptionalHotkey(AppAction.NextTrack, _viewModel.Settings.KeyboardNext);
        bool toggle = RegisterOptionalHotkey(AppAction.TogglePlayPause, _viewModel.Settings.KeyboardToggle);
        bool showCurrent = RegisterOptionalHotkey(AppAction.ShowCurrentTrack, _viewModel.Settings.KeyboardTestOverlay);

        if (updateStatus)
        {
            _viewModel.SetStatus(previous && next && toggle && showCurrent
                ? "快捷键已保存并生效。"
                : "部分快捷键未注册，请检查组合键是否被占用。");
        }

        return previous && next && toggle && showCurrent;
    }

    private bool RegisterOptionalHotkey(AppAction action, string hotkeyText)
    {
        return string.IsNullOrWhiteSpace(hotkeyText) || _hotkeyService.Register(action, hotkeyText);
    }

    private async Task RefreshSourcesAsync()
    {
        try
        {
            _isRefreshingSources = true;
            IReadOnlyList<MediaSessionCandidate> sessions = await _sourceService.ListSessionsAsync();
            _viewModel.ReplaceCaptureSources(sessions);
            ApplyPreferredSource();
            _viewModel.SetStatus(sessions.Count == 0 ? "未检测到媒体源" : $"检测到 {sessions.Count} 个媒体源");
        }
        catch
        {
            _viewModel.SetStatus("媒体源刷新失败");
        }
        finally
        {
            _isRefreshingSources = false;
        }
    }

    private void ApplyPreferredSource()
    {
        _sourceService.PreferredSourceAppUserModelId = _viewModel.Settings.CaptureSourceAppUserModelId;
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
            case AppAction.ShowCurrentTrack:
                await ShowCurrentTrackOverlayAsync();
                break;
        }
    }

    private async Task ShowCurrentTrackOverlayAsync()
    {
        TrackInfo? track = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        if (track == null)
        {
            await _trackMonitor.PollOnceAsync();
            track = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        }

        if (track == null)
        {
            _viewModel.SetStatus("未读取到当前播放歌曲");
            return;
        }

        await _overlayWindow.ShowTrackAsync(track);
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateMaximizeButtonGlyph()
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? RestoreGlyph : MaximizeGlyph;
    }

    private void ApplyWindowShellState()
    {
        WindowFrame.Margin = new Thickness(0);
        WindowFrame.Effect = null;

        if (WindowState == WindowState.Maximized)
        {
            WindowFrame.CornerRadius = new CornerRadius(0);
            WindowFrame.BorderThickness = new Thickness(0);
            return;
        }

        WindowFrame.CornerRadius = new CornerRadius(NormalWindowFrameRadius);
        WindowFrame.BorderThickness = new Thickness(1);
    }

    private void SyncShadowWindow()
    {
        if (!IsVisible || WindowState is WindowState.Minimized or WindowState.Maximized)
        {
            _shadowWindow.Hide();
            return;
        }

        _shadowWindow.SyncWith(this);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        LogStartup("closed");
        _trackMonitor.Dispose();
        _hotkeyService.Dispose();
        _trayIconService.Dispose();
        _overlayWindow.Close();
        _shadowWindow.Close();
    }

    private static void LogStartup(string message)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "musicfloat-startup.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
