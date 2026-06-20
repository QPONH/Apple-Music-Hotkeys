using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;
using AppleMusicOverlay.ViewModels;
using AppleMusicOverlay.Views;

namespace AppleMusicOverlay;

public partial class MainWindow : Window, IHotkeySnapshotRegistrar
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
    private readonly GamepadInputService _gamepadService;
    private readonly GamepadShortcutRuntime _gamepadShortcutRuntime = new();
    private readonly object _gamepadRuntimeLock = new();
    private GamepadBindingSet _gamepadRuntimeBindings = new();
    private readonly TrayIconService _trayIconService;
    private bool _isExiting;
    private bool _isSessionEnding;
    private bool _hasCleanedUpForExit;
    private bool _isRefreshingSources;
    private TextBox? _capturingHotkeyBox;
    private string? _capturingHotkeyOriginalText;
    private string? _pendingHotkeyText;
    private KeyboardHotkeyConflict? _keyboardHotkeyConflict;
    private TextBox? _capturingGamepadBox;
    private AppAction? _capturingGamepadAction;
    private GamepadDeviceKind _capturingGamepadKind;
    private GamepadBindingCaptureSession? _gamepadCaptureSession;
    private bool _isHotkeyCapturePanelShowing;
    private readonly DispatcherTimer _hotkeyCaptureAutoHideTimer = new();
    private readonly DispatcherTimer _gamepadCaptureTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _overlaySettingsSaveDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly HashSet<Key> _pressedHotkeyKeys = new();
    private DateTimeOffset _lastGamepadCaptureUpdate = DateTimeOffset.Now;
    private DateTimeOffset _lastGamepadRuntimeUpdate = DateTimeOffset.Now;
    private bool _gamepadRuntimeCaptureActive;
    private bool IsKeyboardHotkeyInputSuppressed => _capturingHotkeyBox != null || _keyboardHotkeyConflict != null;

    private sealed record KeyboardHotkeyConflict(TextBox Box, AppAction TargetAction, AppAction ConflictAction, string HotkeyText);

    public MainWindow()
    {
        LogStartup("ctor: before InitializeComponent");
        InitializeComponent();
        LogStartup("ctor: after InitializeComponent");
        ApplyAppIcon();
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
        _gamepadService = new GamepadInputService();
        _gamepadService.DevicesChanged += GamepadService_DevicesChanged;
        _gamepadService.SelectedButtonsChanged += GamepadService_SelectedButtonsChanged;
        _trayIconService = new TrayIconService(this, () => _ = ShowCurrentTrackOverlayAsync(), () => RequestApplicationExit());
        LogStartup("ctor: tray ready");
        DataContext = _viewModel;
        DisplayHotkeyBoxValues();
        DisplayGamepadBindingBoxValues();
        _hotkeyCaptureAutoHideTimer.Tick += (_, _) =>
        {
            _hotkeyCaptureAutoHideTimer.Stop();
            HideHotkeyCapturePanel();
        };
        _gamepadCaptureTimer.Tick += (_, _) => UpdateGamepadCaptureFromCurrentButtons();
        _overlaySettingsSaveDebounceTimer.Tick += (_, _) =>
        {
            _overlaySettingsSaveDebounceTimer.Stop();
            SaveOverlaySettingsNow();
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
        _trackMonitor.TrackRead += (_, track) => PostToDispatcher(() => _viewModel.ApplyTrack(track));
        _trackMonitor.TrackChanged += (_, track) => PostToDispatcher(() => _ = _overlayWindow.ShowTrackAsync(track));
        _trackMonitor.TrackRefreshed += (_, track) => PostToDispatcher(() => _overlayWindow.UpdateTrack(track));
        _hotkeyService.HotkeyPressed += HotkeyService_HotkeyPressed;
        UpdateMaximizeButtonGlyph();
        ApplyWindowShellState();
        LogStartup("ctor: complete");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            base.OnClosing(e);
            return;
        }

        FlushOverlaySettingsAutoSave();
        if (_viewModel.Settings.CloseToTray)
        {
            e.Cancel = true;
            _shadowWindow.Hide();
            Hide();
            _viewModel.SetStatus("已最小化到托盘");
            return;
        }

        base.OnClosing(e);
    }

    public void RequestApplicationExit(bool isSessionEnding = false)
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _isSessionEnding = isSessionEnding;
        _trayIconService.PrepareForExit();

        if (Dispatcher.CheckAccess())
        {
            Close();
            return;
        }

        PostToDispatcher(Close);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LogStartup("loaded: begin");
        await RefreshSourcesAsync();
        LogStartup("loaded: after refresh sources");
        RegisterHotkeys();
        LogStartup("loaded: after register hotkeys");
        _gamepadService.Start();
        UpdateGamepadDeviceUi();
        RefreshGamepadRuntimeBindings();
        LogStartup("loaded: after gamepad service start");
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

    private void OverlaySettingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_overlayWindow == null)
        {
            return;
        }

        _overlayWindow.ApplySettings(_viewModel.Settings);
        QueueOverlaySettingsAutoSave(debounce: true);
    }

    private void OverlayTextOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null)
        {
            return;
        }

        _overlayWindow.ApplySettings(_viewModel.Settings);
        TrackInfo? track = _trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack;
        if (track != null)
        {
            _overlayWindow.UpdateTrack(track);
        }

        QueueOverlaySettingsAutoSave(debounce: false);
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

        QueueOverlaySettingsAutoSave(debounce: false);
    }

    private void MouseAutoHide_Changed(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null || sender is not CheckBox autoHideToggle)
        {
            return;
        }

        _viewModel.Settings.AutoHideOnMouseNear = autoHideToggle.IsChecked == true;
        _overlayWindow.ApplySettings(_viewModel.Settings);
        QueueOverlaySettingsAutoSave(debounce: false);
    }

    private void PositionOverlay_Click(object sender, RoutedEventArgs e)
    {
        _overlayWindow.BeginPositionEdit(_viewModel.Settings, SaveOverlaySettingsNow);
    }

    private void RefreshGamepads_Click(object sender, RoutedEventArgs e)
    {
        _gamepadService.RefreshDevices();
    }

    private void GamepadDeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GamepadDeviceSelector.SelectedItem is GamepadDeviceInfo device)
        {
            if (_capturingGamepadBox != null && _gamepadService.SelectedRuntimeId != device.RuntimeId)
            {
                CancelGamepadCapture(hidePanel: true, restoreBox: true);
            }

            _gamepadService.SelectDevice(device.RuntimeId);
            RefreshGamepadRuntimeBindings();
        }
    }

    private void GamepadService_DevicesChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateGamepadDeviceUi();
            if (_capturingGamepadBox != null && _gamepadService.SelectedDevice == null)
            {
                HandleGamepadCaptureDeviceDisconnected();
            }

            if (_gamepadService.SelectedDevice == null)
            {
                ResetGamepadRuntime();
            }
            else
            {
                RefreshGamepadRuntimeBindings();
            }
        });
    }

    private void GamepadService_SelectedButtonsChanged(object? sender, GamepadButtonsChangedEventArgs e)
    {
        HandleGamepadShortcutButtons(e.Buttons);
        _ = Dispatcher.InvokeAsync(UpdateGamepadCaptureFromCurrentButtons, DispatcherPriority.Input);
    }

    private void UpdateGamepadDeviceUi()
    {
        IReadOnlyList<GamepadDeviceInfo> devices = _gamepadService.Devices;
        GamepadDeviceInfo? selected = _gamepadService.SelectedDevice ?? devices.FirstOrDefault();
        bool hasDevice = selected != null;
        bool hasMultipleDevices = devices.Count > 1;

        GamepadStatusTitleText.Text = hasDevice
            ? hasMultipleDevices ? "已连接多个手柄" : selected!.StatusText
            : "未检测到手柄";
        GamepadStatusDescriptionText.Text = hasDevice
            ? $"当前设备：{selected!.DisplayName}"
            : "请通过 USB 或蓝牙连接 Xbox 或 DualSense 手柄。";

        GamepadDeviceSelector.ItemsSource = devices;
        GamepadDeviceSelector.SelectedItem = selected;
        GamepadDeviceSelector.Visibility = hasMultipleDevices ? Visibility.Visible : Visibility.Collapsed;
        DisplayGamepadBindingBoxValues();
        AnimateGamepadConnectionState(hasDevice);
    }

    private void AnimateGamepadConnectionState(bool connected)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Duration duration = TimeSpan.FromMilliseconds(connected ? 180 : 140);
        GamepadConnectedDot.BeginAnimation(OpacityProperty, new DoubleAnimation(connected ? 1 : 0, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);

        if (connected && GamepadBindingsHost.Visibility != Visibility.Visible)
        {
            GamepadBindingsHost.Visibility = Visibility.Visible;
        }

        GamepadBindingsHost.BeginAnimation(OpacityProperty, new DoubleAnimation(connected ? 1 : 0, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        GamepadBindingsHostScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(connected ? 1 : 0.96, duration) { EasingFunction = ease },
            HandoffBehavior.SnapshotAndReplace);

        if (!connected)
        {
            var collapseAnimation = new DoubleAnimation(GamepadBindingsHost.Opacity, 0, duration) { EasingFunction = ease };
            collapseAnimation.Completed += (_, _) =>
            {
                if (_gamepadService.Devices.Count == 0)
                {
                    GamepadBindingsHost.Visibility = Visibility.Collapsed;
                }
            };
            GamepadBindingsHost.BeginAnimation(OpacityProperty, collapseAnimation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void GamepadBindingBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || !TryGetAppAction(box.Tag as string, out AppAction action))
        {
            return;
        }

        e.Handled = true;
        BeginGamepadBindingCapture(box, action);
        box.Focusable = true;
        box.Focus();
    }

    private void GamepadBindingBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingGamepadBox == null)
        {
            return;
        }

        if (NormalizeKey(e) == Key.Escape)
        {
            e.Handled = true;
            CancelGamepadCapture(hidePanel: true, restoreBox: true);
            Keyboard.ClearFocus();
        }
    }

    private void ClearGamepadBinding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !TryGetAppAction(button.Tag as string, out AppAction action))
        {
            return;
        }

        GamepadBindingSet bindings = GetCurrentGamepadBindings();
        bindings.Clear(action);
        SaveGamepadBindings();
        DisplayGamepadBindingBoxValues();
    }

    private void BeginGamepadBindingCapture(TextBox box, AppAction action)
    {
        if (_capturingHotkeyBox != null)
        {
            RestoreHotkeyBox(_capturingHotkeyBox);
            ClearHotkeyCapture(hidePanel: false);
        }

        if (_capturingGamepadBox != null && !ReferenceEquals(_capturingGamepadBox, box))
        {
            CancelGamepadCapture(hidePanel: false, restoreBox: true);
        }

        GamepadDeviceInfo? selected = _gamepadService.SelectedDevice;
        _capturingGamepadBox = box;
        _capturingGamepadAction = action;
        _capturingGamepadKind = selected?.Kind ?? GamepadDeviceKind.Compatible;
        SetGamepadRuntimeCaptureActive(true);
        box.Focusable = true;
        box.Text = "正在监听";
        ShowHotkeyCapturePanel(
            "按下要绑定的手柄按键或组合键，松开所有按键后完成，Esc 取消。",
            title: "手柄快捷键修改");

        if (selected == null || !selected.HasStandardGamepad)
        {
            _gamepadCaptureSession = null;
            ShowHotkeyCapturePanel(
                selected == null ? "未检测到可用手柄。" : "当前设备暂不支持按键录入。",
                isConflict: true,
                title: "手柄快捷键修改");
            return;
        }

        _gamepadCaptureSession = new GamepadBindingCaptureSession(_capturingGamepadKind, action, GetCurrentGamepadBindings());
        _lastGamepadCaptureUpdate = DateTimeOffset.Now;
        _gamepadCaptureTimer.Stop();
        _gamepadCaptureTimer.Start();
        UpdateGamepadCaptureFromCurrentButtons();
    }

    private void UpdateGamepadCaptureFromCurrentButtons()
    {
        if (_gamepadCaptureSession == null || _capturingGamepadBox == null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan elapsed = now - _lastGamepadCaptureUpdate;
        _lastGamepadCaptureUpdate = now;
        _gamepadCaptureSession.Update(_gamepadService.CurrentSelectedButtons, elapsed);
        RenderGamepadCaptureState();
    }

    private void HandleGamepadShortcutButtons(IReadOnlySet<GamepadButton> buttons)
    {
        AppAction? action;
        lock (_gamepadRuntimeLock)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            TimeSpan elapsed = now - _lastGamepadRuntimeUpdate;
            _lastGamepadRuntimeUpdate = now;

            action = _gamepadShortcutRuntime.Update(
                buttons,
                elapsed,
                _gamepadRuntimeBindings,
                isCaptureActive: _gamepadRuntimeCaptureActive);
        }

        if (action != null)
        {
            _ = Dispatcher.InvokeAsync(() => _ = ExecuteAppActionAsync(action.Value), DispatcherPriority.Normal);
        }
    }

    private void RenderGamepadCaptureState()
    {
        if (_gamepadCaptureSession == null || _capturingGamepadBox == null)
        {
            return;
        }

        string detectedText = _gamepadCaptureSession.DisplayText;
        switch (_gamepadCaptureSession.State)
        {
            case GamepadCaptureState.WaitingForNeutral:
                _capturingGamepadBox.Text = "正在监听";
                ShowHotkeyCapturePanel("请先松开手柄上的所有按键。", title: "手柄快捷键修改");
                break;
            case GamepadCaptureState.Listening:
                _capturingGamepadBox.Text = "正在监听";
                ShowHotkeyCapturePanel("按下要绑定的手柄按键或组合键，松开所有按键后完成，Esc 取消。", title: "手柄快捷键修改");
                break;
            case GamepadCaptureState.Capturing:
                _capturingGamepadBox.Text = string.IsNullOrWhiteSpace(detectedText) ? "正在监听" : detectedText;
                ShowHotkeyCapturePanel($"已检测：{detectedText}\n松开所有按键后完成，Esc 取消。", title: "手柄快捷键修改");
                break;
            case GamepadCaptureState.TooManyButtons:
                _capturingGamepadBox.Text = "点击绑定";
                ShowHotkeyCapturePanel(
                    "最多可以绑定 3 个手柄按键，请重新录入。",
                    isConflict: true,
                    title: "手柄快捷键修改",
                    primaryAction: "重新录入",
                    secondaryAction: "取消");
                break;
            case GamepadCaptureState.SingleButtonWarning:
                _capturingGamepadBox.Text = detectedText;
                ShowHotkeyCapturePanel(
                    "单个按键可能与游戏操作冲突，推荐使用组合键。",
                    isConflict: true,
                    title: "手柄快捷键修改",
                    primaryAction: "仍然使用",
                    secondaryAction: "重新录入");
                break;
            case GamepadCaptureState.Conflict:
                _capturingGamepadBox.Text = detectedText;
                ShowHotkeyCapturePanel(
                    _gamepadCaptureSession.Message,
                    isConflict: true,
                    title: "手柄快捷键修改",
                    primaryAction: "替换原绑定",
                    secondaryAction: "取消");
                break;
            case GamepadCaptureState.Completed:
                CompleteGamepadCaptureAndSave();
                break;
            case GamepadCaptureState.DeviceDisconnected:
                ShowHotkeyCapturePanel("手柄已断开，请重新连接后再录入。", isConflict: true, title: "手柄快捷键修改");
                _gamepadCaptureTimer.Stop();
                break;
            case GamepadCaptureState.Cancelled:
                CancelGamepadCapture(hidePanel: true, restoreBox: true);
                break;
        }
    }

    private void CompleteGamepadCaptureAndSave()
    {
        if (_gamepadCaptureSession == null || _capturingGamepadBox == null)
        {
            return;
        }

        GamepadBindingSet bindings = GetCurrentGamepadBindings();
        _gamepadCaptureSession.ApplyCompleted(bindings);
        SaveGamepadBindings();
        DisplayGamepadBindingBoxValues();
        string savedText = _gamepadCaptureSession.PendingBinding == null
            ? string.Empty
            : GamepadBindingFormatter.Format(_gamepadCaptureSession.PendingBinding, _capturingGamepadKind);
        ShowHotkeyCapturePanel($"已自动保存：{savedText}", autoHide: true, autoHideMilliseconds: 650, title: "手柄快捷键修改");
        ClearGamepadCaptureState(restoreBox: false);
    }

    private void GamepadCapturePrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_keyboardHotkeyConflict != null)
        {
            HandleKeyboardHotkeyConflictPrimary();
            return;
        }

        if (_gamepadCaptureSession == null)
        {
            return;
        }

        GamepadBindingSet bindings = GetCurrentGamepadBindings();
        switch (_gamepadCaptureSession.State)
        {
            case GamepadCaptureState.SingleButtonWarning:
                _gamepadCaptureSession.ConfirmSingleButton();
                RenderGamepadCaptureState();
                break;
            case GamepadCaptureState.Conflict:
                _gamepadCaptureSession.ReplaceConflict(bindings);
                SaveGamepadBindings();
                DisplayGamepadBindingBoxValues();
                string savedText = _gamepadCaptureSession.PendingBinding == null
                    ? string.Empty
                    : GamepadBindingFormatter.Format(_gamepadCaptureSession.PendingBinding, _capturingGamepadKind);
                ShowHotkeyCapturePanel($"已自动保存：{savedText}", autoHide: true, autoHideMilliseconds: 650, title: "手柄快捷键修改");
                ClearGamepadCaptureState(restoreBox: false);
                break;
            case GamepadCaptureState.TooManyButtons:
                _gamepadCaptureSession.Retry();
                _lastGamepadCaptureUpdate = DateTimeOffset.Now;
                _gamepadCaptureTimer.Start();
                RenderGamepadCaptureState();
                break;
        }
    }

    private void GamepadCaptureSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_keyboardHotkeyConflict != null)
        {
            HandleKeyboardHotkeyConflictSecondary();
            return;
        }

        if (_gamepadCaptureSession == null)
        {
            return;
        }

        switch (_gamepadCaptureSession.State)
        {
            case GamepadCaptureState.SingleButtonWarning:
                _gamepadCaptureSession.Retry();
                _lastGamepadCaptureUpdate = DateTimeOffset.Now;
                _gamepadCaptureTimer.Start();
                RenderGamepadCaptureState();
                break;
            default:
                CancelGamepadCapture(hidePanel: true, restoreBox: true);
                break;
        }
    }

    private void HandleKeyboardHotkeyConflictPrimary()
    {
        if (_keyboardHotkeyConflict == null)
        {
            return;
        }

        KeyboardHotkeyConflict conflict = _keyboardHotkeyConflict;
        HotkeyApplyResult result = KeyboardHotkeyBindingManager.ApplyReplacingConflict(
            _viewModel.Settings,
            conflict.TargetAction,
            conflict.ConflictAction,
            conflict.HotkeyText,
            this);

        if (!result.Success)
        {
            ShowHotkeyCapturePanel(result.Message, isConflict: true, title: "键盘快捷键修改");
            DisplayHotkeyBoxValues();
            conflict.Box.Text = HotkeyCapturePrompt;
            _pendingHotkeyText = null;
            _keyboardHotkeyConflict = null;
            return;
        }

        _viewModel.Save();
        DisplayHotkeyBoxValues();
        conflict.Box.Text = DisplayHotkeyText(GetHotkeySetting(conflict.Box.Tag as string));
        _capturingHotkeyOriginalText = conflict.Box.Text;
        _pendingHotkeyText = null;
        _keyboardHotkeyConflict = null;
        ShowHotkeyCapturePanel(result.Message, autoHide: true, autoHideMilliseconds: 650, title: "键盘快捷键修改");
        ClearHotkeyCapture(hidePanel: false);
        Keyboard.ClearFocus();
    }

    private void HandleKeyboardHotkeyConflictSecondary()
    {
        _keyboardHotkeyConflict = null;
        DisplayHotkeyBoxValues();
        ClearHotkeyCapture();
        Keyboard.ClearFocus();
    }

    private void HandleGamepadCaptureDeviceDisconnected()
    {
        if (_gamepadCaptureSession == null)
        {
            return;
        }

        _gamepadCaptureSession.Disconnect();
        RenderGamepadCaptureState();
    }

    private void CancelGamepadCapture(bool hidePanel, bool restoreBox)
    {
        if (_gamepadCaptureSession != null)
        {
            _gamepadCaptureSession.Cancel();
        }

        ClearGamepadCaptureState(restoreBox);
        if (restoreBox)
        {
            DisplayGamepadBindingBoxValues();
        }

        if (hidePanel)
        {
            HideHotkeyCapturePanel();
        }
    }

    private void ClearGamepadCaptureState(bool restoreBox)
    {
        TextBox? previousBox = _capturingGamepadBox;
        if (restoreBox && previousBox != null)
        {
            previousBox.Text = DisplayGamepadBindingText(_capturingGamepadAction);
        }

        if (previousBox != null)
        {
            previousBox.Focusable = false;
        }

        _capturingGamepadBox = null;
        _capturingGamepadAction = null;
        _gamepadCaptureSession = null;
        _gamepadCaptureTimer.Stop();
        ResumeGamepadRuntimeAfterCapture(_gamepadService.CurrentSelectedButtons);
    }

    private void DisplayGamepadBindingBoxValues()
    {
        if (GamepadPreviousBox == null)
        {
            return;
        }

        GamepadPreviousBox.Text = DisplayGamepadBindingText(AppAction.PreviousTrack);
        GamepadNextBox.Text = DisplayGamepadBindingText(AppAction.NextTrack);
        GamepadToggleBox.Text = DisplayGamepadBindingText(AppAction.TogglePlayPause);
        GamepadShowCurrentBox.Text = DisplayGamepadBindingText(AppAction.ShowCurrentTrack);
    }

    private string DisplayGamepadBindingText(AppAction? action)
    {
        if (action == null)
        {
            return "点击绑定";
        }

        GamepadBinding binding = GetCurrentGamepadBindings().GetBinding(action.Value);
        return binding.IsEmpty ? "点击绑定" : GamepadBindingFormatter.Format(binding, GetCurrentGamepadKind());
    }

    private GamepadBindingSet GetCurrentGamepadBindings()
    {
        return GetGamepadBindingsForKind(GetCurrentGamepadKind());
    }

    private GamepadBindingSet GetGamepadBindingsForKind(GamepadDeviceKind kind)
    {
        return kind switch
        {
            GamepadDeviceKind.Xbox => _viewModel.Settings.XboxGamepadBindings,
            GamepadDeviceKind.DualSense => _viewModel.Settings.DualSenseGamepadBindings,
            _ => _viewModel.Settings.CompatibleGamepadBindings
        };
    }

    private GamepadDeviceKind GetCurrentGamepadKind()
    {
        return _gamepadService.SelectedDevice?.Kind ?? _capturingGamepadKind;
    }

    private void SaveGamepadBindings()
    {
        _viewModel.Save();
        RefreshGamepadRuntimeBindings();
    }

    private void RefreshGamepadRuntimeBindings()
    {
        GamepadBindingSet bindings = _gamepadService.SelectedDevice is { } device
            ? CloneGamepadBindings(GetGamepadBindingsForKind(device.Kind))
            : new GamepadBindingSet();

        lock (_gamepadRuntimeLock)
        {
            _gamepadRuntimeBindings = bindings;
            _gamepadShortcutRuntime.Reset();
            _lastGamepadRuntimeUpdate = DateTimeOffset.Now;
        }
    }

    private void ResetGamepadRuntime()
    {
        lock (_gamepadRuntimeLock)
        {
            _gamepadRuntimeBindings = new GamepadBindingSet();
            _gamepadRuntimeCaptureActive = false;
            _gamepadShortcutRuntime.Reset();
            _lastGamepadRuntimeUpdate = DateTimeOffset.Now;
        }
    }

    private void SetGamepadRuntimeCaptureActive(bool isActive)
    {
        lock (_gamepadRuntimeLock)
        {
            _gamepadRuntimeCaptureActive = isActive;
            _gamepadShortcutRuntime.Reset();
            _lastGamepadRuntimeUpdate = DateTimeOffset.Now;
        }
    }

    private void ResumeGamepadRuntimeAfterCapture(IReadOnlySet<GamepadButton> currentButtons)
    {
        lock (_gamepadRuntimeLock)
        {
            _gamepadRuntimeCaptureActive = false;
            _gamepadShortcutRuntime.ResumeAfterCapture(currentButtons);
            _lastGamepadRuntimeUpdate = DateTimeOffset.Now;
        }
    }

    private static GamepadBindingSet CloneGamepadBindings(GamepadBindingSet source)
    {
        return new GamepadBindingSet
        {
            Previous = CloneGamepadBinding(source.Previous),
            Next = CloneGamepadBinding(source.Next),
            Toggle = CloneGamepadBinding(source.Toggle),
            ShowCurrent = CloneGamepadBinding(source.ShowCurrent)
        };
    }

    private static GamepadBinding CloneGamepadBinding(GamepadBinding source)
    {
        return GamepadBinding.FromButtons(source.Buttons);
    }

    private static bool TryGetAppAction(string? value, out AppAction action)
    {
        return Enum.TryParse(value, ignoreCase: false, out action) && GamepadBindingActions.SupportedActions.Contains(action);
    }

    private static bool TryGetKeyboardAction(string? key, out AppAction action)
    {
        switch (key)
        {
            case "KeyboardPrevious":
                action = AppAction.PreviousTrack;
                return true;
            case "KeyboardNext":
                action = AppAction.NextTrack;
                return true;
            case "KeyboardToggle":
                action = AppAction.TogglePlayPause;
                return true;
            case "KeyboardTestOverlay":
                action = AppAction.ShowCurrentTrack;
                return true;
            default:
                action = default;
                return false;
        }
    }

    private void QueueOverlaySettingsAutoSave(bool debounce)
    {
        if (debounce)
        {
            _overlaySettingsSaveDebounceTimer.Stop();
            _overlaySettingsSaveDebounceTimer.Start();
            return;
        }

        _overlaySettingsSaveDebounceTimer.Stop();
        _ = Dispatcher.InvokeAsync(SaveOverlaySettingsNow, DispatcherPriority.Background);
    }

    private void FlushOverlaySettingsAutoSave()
    {
        if (!_overlaySettingsSaveDebounceTimer.IsEnabled)
        {
            return;
        }

        _overlaySettingsSaveDebounceTimer.Stop();
        SaveOverlaySettingsNow();
    }

    private void SaveOverlaySettingsNow()
    {
        _viewModel.Save();
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

        if (!TryGetKeyboardAction(button.Tag as string, out AppAction action))
        {
            return;
        }

        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(_viewModel.Settings, action, string.Empty, this);
        if (!result.Success)
        {
            ShowHotkeyCapturePanel(result.Message, isConflict: true);
            DisplayHotkeyBoxValues();
            return;
        }

        _viewModel.Save();
        DisplayHotkeyBoxValues();
        ShowHotkeyCapturePanel(result.Message, autoHide: true, autoHideMilliseconds: 650);
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
        if (_keyboardHotkeyConflict != null)
        {
            return;
        }

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
            if (_keyboardHotkeyConflict != null)
            {
                DisplayHotkeyBoxValues();
            }
            else
            {
                RestoreHotkeyBox(box);
            }

            ClearHotkeyCapture();
            Keyboard.ClearFocus();
            return;
        }

        if (_keyboardHotkeyConflict != null)
        {
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

        if (_keyboardHotkeyConflict != null)
        {
            return;
        }

        Key key = NormalizeKey(e);
        if (key != Key.None)
        {
            _pressedHotkeyKeys.Remove(key);
        }

        if (_pressedHotkeyKeys.Count == 0)
        {
            if (TryCommitPendingHotkey(box))
            {
                ClearHotkeyCapture(hidePanel: false);
                Keyboard.ClearFocus();
            }
            else if (_keyboardHotkeyConflict == null)
            {
                box.Text = HotkeyCapturePrompt;
            }
            return;
        }

        UpdateHotkeyCapture(box);
    }

    private void BeginHotkeyCapture(TextBox box)
    {
        if (_capturingGamepadBox != null)
        {
            CancelGamepadCapture(hidePanel: false, restoreBox: true);
        }

        if (_capturingHotkeyBox != null && !ReferenceEquals(_capturingHotkeyBox, box))
        {
            RestoreHotkeyBox(_capturingHotkeyBox);
            ClearHotkeyCapture();
        }

        _capturingHotkeyBox = box;
        _capturingHotkeyOriginalText = box.Text;
        _pendingHotkeyText = null;
        _keyboardHotkeyConflict = null;
        _pressedHotkeyKeys.Clear();
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
        _keyboardHotkeyConflict = null;
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
            ShowHotkeyCapturePanel($"已检测：{hotkeyText}。松开全部按键后自动保存。");
            return;
        }

        ShowHotkeyCapturePanel(_pressedHotkeyKeys.Any(IsModifierKey)
            ? (_pendingHotkeyText == null ? HotkeyNeedMainKeyStatus : $"松开全部按键后自动保存：{_pendingHotkeyText}。")
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
        if (!TryGetKeyboardAction(box.Tag as string, out AppAction action))
        {
            return false;
        }

        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(_viewModel.Settings, action, _pendingHotkeyText, this);
        if (!result.Success)
        {
            if (result.FailureKind == HotkeyApplyFailureKind.InternalConflict && result.ConflictAction != null)
            {
                _keyboardHotkeyConflict = new KeyboardHotkeyConflict(box, action, result.ConflictAction.Value, result.HotkeyText);
                ShowHotkeyCapturePanel(
                    result.Message,
                    isConflict: true,
                    title: "键盘快捷键修改",
                    primaryAction: "替换原绑定",
                    secondaryAction: "取消");
                box.Text = result.HotkeyText;
                _pendingHotkeyText = null;
                return false;
            }

            ShowHotkeyCapturePanel(result.Message, isConflict: true, title: "键盘快捷键修改");
            box.Text = HotkeyCapturePrompt;
            _pendingHotkeyText = null;
            DisplayHotkeyBoxValues();
            return false;
        }

        _viewModel.Save();
        DisplayHotkeyBoxValues();
        box.Text = DisplayHotkeyText(GetHotkeySetting(box.Tag as string));
        _capturingHotkeyOriginalText = box.Text;
        _pendingHotkeyText = null;
        ShowHotkeyCapturePanel(result.Message, autoHide: true, autoHideMilliseconds: 650);
        return true;
    }

    private bool TryCommitRegisteredHotkeyCandidate(string hotkeyText)
    {
        if (_capturingHotkeyBox == null || _keyboardHotkeyConflict != null)
        {
            return false;
        }

        _pressedHotkeyKeys.Clear();
        _pendingHotkeyText = hotkeyText;
        _capturingHotkeyBox.Text = hotkeyText;
        if (!TryCommitPendingHotkey(_capturingHotkeyBox))
        {
            return false;
        }

        ClearHotkeyCapture(hidePanel: false);
        Keyboard.ClearFocus();
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

    private void ShowHotkeyCapturePanel(
        string statusText,
        bool isConflict = false,
        bool autoHide = false,
        int autoHideMilliseconds = 0,
        string? title = null,
        string? primaryAction = null,
        string? secondaryAction = null)
    {
        _hotkeyCaptureAutoHideTimer.Stop();
        HotkeyCaptureTitleText.Text = isConflict ? "快捷键提示" : "快捷键修改";
        HotkeyCaptureStatusText.Text = statusText;
        if (!string.IsNullOrWhiteSpace(title))
        {
            HotkeyCaptureTitleText.Text = title;
        }

        bool hasActions = !string.IsNullOrWhiteSpace(primaryAction) || !string.IsNullOrWhiteSpace(secondaryAction);
        HotkeyCaptureActionsPanel.Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
        GamepadCapturePrimaryButton.Visibility = string.IsNullOrWhiteSpace(primaryAction) ? Visibility.Collapsed : Visibility.Visible;
        GamepadCaptureSecondaryButton.Visibility = string.IsNullOrWhiteSpace(secondaryAction) ? Visibility.Collapsed : Visibility.Visible;
        GamepadCapturePrimaryButton.Content = primaryAction ?? string.Empty;
        GamepadCaptureSecondaryButton.Content = secondaryAction ?? string.Empty;
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
                    HotkeyCaptureActionsPanel.Visibility = Visibility.Collapsed;
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
        if (IsClickInsideCurrentHotkeyBox(source))
        {
            return;
        }

        if (_capturingHotkeyBox != null)
        {
            if (_keyboardHotkeyConflict != null)
            {
                DisplayHotkeyBoxValues();
            }
            else
            {
                RestoreHotkeyBox(_capturingHotkeyBox);
            }

            ClearHotkeyCapture();
            Keyboard.ClearFocus();
        }

        if (_capturingGamepadBox != null)
        {
            CancelGamepadCapture(hidePanel: true, restoreBox: true);
            Keyboard.ClearFocus();
        }
    }

    private bool IsClickInsideCurrentHotkeyBox(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, _capturingHotkeyBox) ||
                ReferenceEquals(source, _capturingGamepadBox) ||
                ReferenceEquals(source, HotkeyCapturePanel) ||
                ReferenceEquals(source, HotkeyCaptureActionsPanel))
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
        bool registered = RegisterHotkeySnapshotDirect(KeyboardHotkeyBindingManager.CreateSnapshot(_viewModel.Settings));

        if (updateStatus)
        {
            _viewModel.SetStatus(registered
                ? "快捷键已保存并生效。"
                : "部分快捷键未注册，请检查组合键是否被占用。");
        }

        return registered;
    }

    public bool TryRegisterSnapshot(IReadOnlyDictionary<AppAction, string> hotkeys)
    {
        IReadOnlyDictionary<AppAction, string> previous = KeyboardHotkeyBindingManager.CreateSnapshot(_viewModel.Settings);
        if (RegisterHotkeySnapshotDirect(hotkeys))
        {
            return true;
        }

        RegisterHotkeySnapshotDirect(previous);
        return false;
    }

    private bool RegisterHotkeySnapshotDirect(IReadOnlyDictionary<AppAction, string> hotkeys)
    {
        _hotkeyService.Clear();
        foreach ((AppAction action, string hotkeyText) in hotkeys)
        {
            if (!RegisterOptionalHotkey(action, hotkeyText))
            {
                _hotkeyService.Clear();
                return false;
            }
        }

        return true;
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

    private async void HotkeyService_HotkeyPressed(object? sender, GlobalHotkeyEventArgs e)
    {
        if (IsKeyboardHotkeyInputSuppressed)
        {
            TryCommitRegisteredHotkeyCandidate(e.HotkeyText);
            return;
        }

        await ExecuteAppActionAsync(e.Action);
    }

    private async Task ExecuteAppActionAsync(AppAction action)
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
        RequestApplicationExit();
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
        CleanupForExit();
    }

    private void CleanupForExit()
    {
        if (_hasCleanedUpForExit)
        {
            return;
        }

        _hasCleanedUpForExit = true;
        _hotkeyCaptureAutoHideTimer.Stop();
        _gamepadCaptureTimer.Stop();
        _overlaySettingsSaveDebounceTimer.Stop();

        if (!_isSessionEnding)
        {
            FlushOverlaySettingsAutoSave();
        }

        SafeDispose(_trackMonitor);
        SafeDispose(_hotkeyService);
        SafeDispose(_gamepadService);
        _trayIconService.PrepareForExit();
        SafeDispose(_trayIconService);
        SafeClose(_overlayWindow);
        SafeClose(_shadowWindow);
    }

    private void PostToDispatcher(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    private static void SafeDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void SafeClose(Window window)
    {
        try
        {
            window.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ApplyAppIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MusicFloat.ico");
            if (File.Exists(iconPath))
            {
                Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
            }
        }
        catch
        {
        }
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
