using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private string _startupRegistrationWarning = string.Empty;
    private string _hotkeyRegistrationWarning = string.Empty;
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
        NormalizeSettings(_settings);
        _draftSettings = CloneSettings(_settings);
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
            AttachComboBoxPlacementHandlers(this);
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

    /// <summary>Remove the text caret/selection when the user clicks an unused part of the settings window.</summary>
    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBoxBase) return;

        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null && node != this)
        {
            // Keep normal interaction with inputs and clickable controls unchanged. A click on
            // the blank card/page background should dismiss keyboard focus from the TextBox.
            if (node is TextBoxBase or ComboBox or ButtonBase or Slider or ScrollBar)
                return;
            node = GetParentForFocusDismissal(node);
        }
        Keyboard.ClearFocus();
    }

    private static DependencyObject? GetParentForFocusDismissal(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(child);
        if (child is FrameworkContentElement frameworkContent)
            return frameworkContent.Parent;
        if (child is ContentElement content)
            return ContentOperations.GetParent(content);
        return LogicalTreeHelper.GetParent(child);
    }

    private void AttachComboBoxPlacementHandlers(DependencyObject root)
    {
        if (root is ComboBox comboBox)
        {
            // Configure placement before WPF opens the Popup. DropDownOpened alone is too late
            // on some DPI/scaling combinations: the popup is already clipped before it can move.
            comboBox.PreviewMouseDown += ComboBox_PreviewMouseDown;
            comboBox.PreviewKeyDown += ComboBox_PreviewKeyDown;
            comboBox.PreviewMouseWheel += ComboBox_PreviewMouseWheel;
            comboBox.DropDownOpened += ComboBox_DropDownOpened;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dependencyChild)
                AttachComboBoxPlacementHandlers(dependencyChild);
        }
    }

    private void ComboBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ComboBox comboBox && !comboBox.IsDropDownOpen)
            ConfigureComboBoxPopup(comboBox);
    }

    private void ComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.IsDropDownOpen) return;
        bool opensDropDown = e.Key is Key.F4 or Key.Space or Key.Enter
            || (e.Key == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        if (opensDropDown) ConfigureComboBoxPopup(comboBox);
    }

    /// <summary>
    /// When a ComboBox menu is closed, do not let the mouse wheel silently change
    /// the selected option, even if the control still has keyboard focus. Route the wheel to the containing page's
    /// outer ScrollViewer instead, so the page remains scrollable using the same scrollbar.
    /// </summary>
    private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.IsDropDownOpen)
            return;

        ScrollViewer? pageScrollViewer = FindAncestorScrollViewer(comboBox);
        if (pageScrollViewer is not null)
        {
            double wheelLines = SystemParameters.WheelScrollLines;
            if (wheelLines <= 0) wheelLines = 3;
            double offsetDelta = -(e.Delta / 120.0) * wheelLines * 16.0;
            pageScrollViewer.ScrollToVerticalOffset(pageScrollViewer.VerticalOffset + offsetDelta);
        }

        // Suppress ComboBox's built-in wheel-to-selection behavior while its menu is closed.
        e.Handled = true;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject child)
    {
        DependencyObject? node = GetParentForFocusDismissal(child);
        while (node is not null)
        {
            if (node is ScrollViewer scrollViewer)
                return scrollViewer;
            node = GetParentForFocusDismissal(node);
        }
        return null;
    }

    private void ComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        // PreviewMouseDown / PreviewKeyDown prepare placement before the native popup opens.
        // Only apply a fallback here if an unusual opening path skipped those preview events;
        // changing placement after the popup is visible can itself cause clipping.
        if (sender is ComboBox comboBox
            && comboBox.Template.FindName("PART_Popup", comboBox) is Popup popup
            && popup.Placement != PlacementMode.Custom)
        {
            ConfigureComboBoxPopup(comboBox);
        }
    }

    /// <summary>
    /// Positions the drop-down against the monitor work area before it opens. The menu height
    /// is constrained to the room on the chosen side, and the popup callback chooses above or
    /// below using the actual measured popup height. This avoids relying on a post-open move,
    /// which was too late when the Lyrics controls were near the bottom edge of the window.
    /// </summary>
    private void ConfigureComboBoxPopup(ComboBox comboBox)
    {
        if (comboBox.Template.FindName("PART_Popup", comboBox) is not Popup popup)
            return;

        const double gap = 6;
        const double maxMenuHeight = 230;
        const double borderAllowance = 4;

        var spaces = GetComboBoxPopupSpace(comboBox);
        // Estimate the natural menu height to avoid opening upward for small lists that fit below.
        double estimatedHeight = Math.Min(maxMenuHeight, Math.Max(44, comboBox.Items.Count * 37.0 + 14));
        bool placeAbove = spaces.Below < estimatedHeight + gap && spaces.Above > spaces.Below;
        if (spaces.Below >= estimatedHeight + gap)
            placeAbove = false;
        else if (spaces.Above >= estimatedHeight + gap)
            placeAbove = true;

        double available = Math.Max(18, (placeAbove ? spaces.Above : spaces.Below) - gap - borderAllowance);
        double maxHeight = Math.Min(maxMenuHeight, available);
        comboBox.MaxDropDownHeight = maxHeight;
        popup.PlacementTarget = comboBox;
        popup.HorizontalOffset = 0;
        popup.VerticalOffset = 0;
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
        {
            var currentSpaces = GetComboBoxPopupSpace(comboBox);
            bool shouldPlaceAbove;
            if (currentSpaces.Below >= popupSize.Height + gap)
                shouldPlaceAbove = false;
            else if (currentSpaces.Above >= popupSize.Height + gap)
                shouldPlaceAbove = true;
            else
                shouldPlaceAbove = currentSpaces.Above > currentSpaces.Below;

            var position = shouldPlaceAbove
                ? new Point(0, -popupSize.Height - gap)
                : new Point(0, targetSize.Height + gap);
            return new[] { new CustomPopupPlacement(position, PopupPrimaryAxis.Vertical) };
        };
        popup.Placement = PlacementMode.Custom;

        if (popup.Child is FrameworkElement popupContent)
        {
            popupContent.MaxHeight = maxHeight;
            if (popupContent is Border border && border.Child is ScrollViewer scrollViewer)
            {
                scrollViewer.MaxHeight = Math.Max(12, maxHeight - border.Padding.Top - border.Padding.Bottom - 2);
                scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
        }
    }

    /// <summary>Returns the DIP distance from a ComboBox to the monitor's top and bottom work-area edges.</summary>
    private static (double Above, double Below) GetComboBoxPopupSpace(ComboBox comboBox)
    {
        try
        {
            Point screenPoint = comboBox.PointToScreen(new Point(0, 0));
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point(
                (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
            var workArea = screen.WorkingArea;
            double dpiScaleY = Math.Max(0.5, VisualTreeHelper.GetDpi(comboBox).DpiScaleY);
            double targetBottom = screenPoint.Y + comboBox.ActualHeight * dpiScaleY;
            double above = (screenPoint.Y - workArea.Top) / dpiScaleY;
            double below = (workArea.Bottom - targetBottom) / dpiScaleY;
            return (Math.Max(0, above), Math.Max(0, below));
        }
        catch
        {
            // Keep a usable default if the control is not connected to a presentation source yet.
            return (1000, 1000);
        }
    }

    /// <summary>Clamps the loaded values into the ranges the UI accepts, so the first render
    /// never looks like a user edit.</summary>
    private void NormalizeDraftValues() => NormalizeSettings(_draftSettings);

    /// <summary>Validate persisted values before either the UI or services use them.</summary>
    private static void NormalizeSettings(OverlaySettings settings)
    {
        settings.KeyboardPrevious ??= "Ctrl+Shift+Left";
        settings.KeyboardNext ??= "Ctrl+Shift+Right";
        settings.KeyboardToggle ??= "Ctrl+Shift+Space";
        settings.KeyboardVolumeUp ??= "Ctrl+Shift+Up";
        settings.KeyboardVolumeDown ??= "Ctrl+Shift+Down";

        settings.RecoveryWaitSeconds = Math.Clamp(settings.RecoveryWaitSeconds, 0, 600);
        settings.RecoveryQueueXPercent = Math.Clamp(settings.RecoveryQueueXPercent, 0, 100);
        settings.RecoveryQueueYPercent = Math.Clamp(settings.RecoveryQueueYPercent, 0, 100);
        settings.RecoverySongXPercent = Math.Clamp(settings.RecoverySongXPercent, 0, 100);
        settings.RecoverySongYPercent = Math.Clamp(settings.RecoverySongYPercent, 0, 100);
        settings.RecoveryQueueMethod = settings.RecoveryQueueMethod is "Keyboard" or "Mouse" ? settings.RecoveryQueueMethod : "Keyboard";
        settings.RecoverySongMethod = settings.RecoverySongMethod is "Keyboard" or "Mouse" ? settings.RecoverySongMethod : "Mouse";

        settings.LyricsFontSize = !double.IsFinite(settings.LyricsFontSize) ? 26 : Math.Clamp(settings.LyricsFontSize, 10, 120);
        settings.LyricsTranslationFontSize = !double.IsFinite(settings.LyricsTranslationFontSize) ? 16 : Math.Clamp(settings.LyricsTranslationFontSize, 8, 96);
        settings.LyricsOpacity = LyricStyle.ClampOpacity(settings.LyricsOpacity);
        settings.LyricsColor = LyricStyle.Normalize(settings.LyricsColor);
        settings.LyricsCurrentColor = LyricStyle.Normalize(settings.LyricsCurrentColor);
        settings.LyricsNextColor = LyricStyle.Normalize(settings.LyricsNextColor);
        settings.LyricsHorizontalTranslationLayout = settings.LyricsHorizontalTranslationLayout is "LeftLarge" or "RightLarge" or "Diagonal" ? settings.LyricsHorizontalTranslationLayout : "BottomRightSmall";
        settings.LyricsOffsetMilliseconds = Math.Clamp(settings.LyricsOffsetMilliseconds, -5000, 5000);
        settings.LyricsAlignment = settings.LyricsAlignment is "Left" or "Right" ? settings.LyricsAlignment : "Center";
        settings.LyricsOrientation = settings.LyricsOrientation == "Vertical" ? "Vertical" : "Horizontal";
        settings.LyricsVerticalAlignment = settings.LyricsVerticalAlignment is "Top" or "Bottom" ? settings.LyricsVerticalAlignment : "Center";
        settings.LyricsVerticalAnchor = settings.LyricsVerticalAnchor is "Left" or "Right" ? settings.LyricsVerticalAnchor : "Center";
        settings.LyricsWordVerticalLayout = settings.LyricsWordVerticalLayout is "Diagonal" or "RightAligned" ? settings.LyricsWordVerticalLayout : "RightStacked";
        settings.LyricsNonWordVerticalLayout = settings.LyricsNonWordVerticalLayout is "RightStacked" or "RightAligned" ? settings.LyricsNonWordVerticalLayout : "Diagonal";
        settings.LyricsNextLineMode = settings.LyricsNextLineMode is "Hidden" or "Translation" ? settings.LyricsNextLineMode : "NextLine";
        settings.LyricsVerticalTranslationLayout = settings.LyricsVerticalTranslationLayout is "LeftVertical" or "RightVertical" ? settings.LyricsVerticalTranslationLayout : "BottomSmall";
        settings.LyricsWindowWidth = double.IsFinite(settings.LyricsWindowWidth) ? Math.Clamp(settings.LyricsWindowWidth, 180, 3000) : 620;
        settings.LyricsWindowHeight = double.IsFinite(settings.LyricsWindowHeight) ? Math.Clamp(settings.LyricsWindowHeight, 70, 2000) : 180;
        if (settings.LyricsWindowLeft is double left && !double.IsFinite(left)) settings.LyricsWindowLeft = null;
        if (settings.LyricsWindowTop is double top && !double.IsFinite(top)) settings.LyricsWindowTop = null;
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

        // Keep the actual Windows startup registration in sync with the persisted setting. The
        // comparison skips the registry work (and keeps a manual Windows "disabled" state only as
        // long as it matches) unless the registration is missing, stale or out of sync.
        if (_settings.StartWithWindows != StartupService.IsEnabled)
        {
            bool startupRegistrationOk = StartupService.TrySetEnabled(_settings.StartWithWindows, out string? startupError);
            _startupRegistrationWarning = startupRegistrationOk
                ? startupError ?? string.Empty
                : $"开机启动同步失败：{startupError}";
            if (!startupRegistrationOk)
                CrashLog.Write(new InvalidOperationException(_startupRegistrationWarning), "Startup:SynchronizeOnLaunch");
        }

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
        if (!string.IsNullOrWhiteSpace(_startupRegistrationWarning))
            StatusText = _startupRegistrationWarning;
    }

    /// <summary>Pushes the staged settings into the controls. The caller owns
    /// <see cref="_isLoadingSettings"/> so nested loads (reset, constructor) stay quiet.</summary>
    private void LoadSettingsToUi()
    {
        PreviousBox.Text = _draftSettings.KeyboardPrevious;
        NextBox.Text = _draftSettings.KeyboardNext;
        ToggleBox.Text = _draftSettings.KeyboardToggle;
        VolumeUpBox.Text = _draftSettings.KeyboardVolumeUp;
        VolumeDownBox.Text = _draftSettings.KeyboardVolumeDown;
        CloseToTrayCheckBox.IsChecked = _draftSettings.CloseToTray;
        HideOnStartupCheckBox.IsChecked = _draftSettings.HideWindowOnStartup;
        StartWithWindowsCheckBox.IsChecked = _draftSettings.StartWithWindows;
        SoftwareRenderingCheckBox.IsChecked = _draftSettings.SoftwareRendering;
        StartupPlaybackRecoveryCheckBox.IsChecked = _draftSettings.StartupPlaybackRecovery;
        RecoveryWaitSecondsBox.Text = _draftSettings.RecoveryWaitSeconds.ToString();
        RecoveryQueueXBox.Text = _draftSettings.RecoveryQueueXPercent.ToString();
        RecoveryQueueYBox.Text = _draftSettings.RecoveryQueueYPercent.ToString();
        RecoverySongXBox.Text = _draftSettings.RecoverySongXPercent.ToString();
        RecoverySongYBox.Text = _draftSettings.RecoverySongYPercent.ToString();
        RecoveryQueueMethodBox.SelectedValue = _draftSettings.RecoveryQueueMethod;
        RecoverySongMethodBox.SelectedValue = _draftSettings.RecoverySongMethod;
        LyricsEnabledCheckBox.IsChecked = _draftSettings.LyricsEnabled;
        LyricsFontSizeBox.Text = _draftSettings.LyricsFontSize.ToString("0.##", CultureInfo.CurrentCulture);
        LyricsTranslationFontSizeBox.Text = _draftSettings.LyricsTranslationFontSize.ToString("0.##", CultureInfo.CurrentCulture);
        LyricsOpacityBox.Text = (_draftSettings.LyricsOpacity * 100).ToString("0.##", CultureInfo.CurrentCulture);
        LyricsColorBox.Text = _draftSettings.LyricsColor;
        LyricsCurrentColorBox.Text = _draftSettings.LyricsCurrentColor;
        LyricsNextColorBox.Text = _draftSettings.LyricsNextColor;
        LyricsHorizontalTranslationLayoutBox.SelectedValue = _draftSettings.LyricsHorizontalTranslationLayout;
        UpdateLyricsColorSwatch();
        LyricsAlignmentBox.SelectedValue = _draftSettings.LyricsAlignment;
        LyricsOrientationBox.SelectedValue = _draftSettings.LyricsOrientation;
        LyricsVerticalAlignmentBox.SelectedValue = _draftSettings.LyricsVerticalAlignment;
        LyricsOffsetBox.Text = _draftSettings.LyricsOffsetMilliseconds.ToString();
        LyricsVerticalAnchorBox.SelectedValue = _draftSettings.LyricsVerticalAnchor;
        LyricsWordVerticalLayoutBox.SelectedValue = _draftSettings.LyricsWordVerticalLayout;
        LyricsNonWordVerticalLayoutBox.SelectedValue = _draftSettings.LyricsNonWordVerticalLayout;
        LyricsNextLineModeBox.SelectedValue = _draftSettings.LyricsNextLineMode;
        LyricsVerticalTranslationLayoutBox.SelectedValue = _draftSettings.LyricsVerticalTranslationLayout;
        LyricsTranslationCheckBox.IsChecked = _draftSettings.LyricsTranslationEnabled;
        LyricsBackgroundCheckBox.IsChecked = _draftSettings.LyricsBackgroundEnabled;
        UpdateVerticalControlsEnabled();
    }

    private void UpdateVerticalControlsEnabled()
    {
        bool enabled = _draftSettings.LyricsOrientation == "Vertical";
        LyricsVerticalAlignmentBox.IsEnabled = enabled;
        LyricsVerticalAnchorBox.IsEnabled = enabled;
        LyricsWordVerticalLayoutBox.IsEnabled = enabled;
        LyricsNonWordVerticalLayoutBox.IsEnabled = enabled;
        LyricsVerticalTranslationLayoutBox.IsEnabled = enabled;
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
        IReadOnlyDictionary<AppAction, string> previousSnapshot = requiredAction.HasValue
            ? KeyboardHotkeyBindingManager.CreateSnapshot(_draftSettings)
            : KeyboardHotkeyBindingManager.CreateSnapshot(_settings);

        try
        {
            _hotkeyService.Clear();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Hotkeys:ClearBeforeRegister");
            _hotkeyRegistrationWarning = "无法重置现有快捷键，因此本次快捷键更改未生效。";
            if (_capturingBox is null) StatusText = _hotkeyRegistrationWarning;
            return false;
        }

        _hotkeyRegistrationWarning = string.Empty;
        var failedActions = new List<AppAction>();
        var registeredCombinations = new Dictionary<HotkeyDefinition, AppAction>();

        foreach ((AppAction action, string hotkeyText) in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(hotkeyText)) continue;

            // Settings from older versions or a hand-edited JSON file may contain the same
            // chord under different spellings (for example, "Ctrl+Esc" and "Control+Escape").
            // The low-level volume hook can accept both, but then only the first action fires.
            if (HotkeyParser.TryParse(hotkeyText, out HotkeyDefinition parsed)
                && registeredCombinations.TryGetValue(parsed, out _))
            {
                failedActions.Add(action);
                if (requiredAction == action)
                {
                    // Roll back to the snapshot that was active before this attempt. During
                    // hotkey capture that is the draft snapshot, not necessarily saved settings.
                    RegisterHotkeySnapshotDirect(previousSnapshot, requiredAction: null);
                    return false;
                }
                continue;
            }

            bool registered;
            try
            {
                registered = _hotkeyService.Register(action, hotkeyText);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, $"Hotkeys:Register:{action}");
                registered = false;
            }

            if (registered)
            {
                if (HotkeyParser.TryParse(hotkeyText, out parsed))
                    registeredCombinations.TryAdd(parsed, action);
                continue;
            }

            failedActions.Add(action);
            if (requiredAction == action)
            {
                // The newly requested shortcut itself is unavailable. Restore the snapshot
                // active before this attempt, then report failure to the binding editor.
                RegisterHotkeySnapshotDirect(previousSnapshot, requiredAction: null);
                return false;
            }
        }

        if (failedActions.Count > 0 && _capturingBox == null)
        {
            _hotkeyRegistrationWarning = "部分快捷键未能注册或与其他快捷键重复：" +
                                         string.Join("、", failedActions.Distinct().Select(GetActionLabel)) +
                                         "。其余功能已应用；请更换冲突的快捷键。";
            StatusText = _hotkeyRegistrationWarning;
        }

        return true;
    }

    private static string GetActionLabel(AppAction action) => KeyboardHotkeyBindingManager.GetUiLabel(action);

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
        VersionText.Text = $"v14.50 · 构建 {buildTime} · {Environment.OSVersion.Version}";
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

    private void BooleanSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || sender is not CheckBox box || box.Tag is not string setting) return;
        bool value = box.IsChecked == true;
        switch (setting)
        {
            case nameof(OverlaySettings.LyricsEnabled): _draftSettings.LyricsEnabled = value; break;
            case nameof(OverlaySettings.CloseToTray): _draftSettings.CloseToTray = value; break;
            case nameof(OverlaySettings.HideWindowOnStartup): _draftSettings.HideWindowOnStartup = value; break;
            case nameof(OverlaySettings.StartWithWindows): _draftSettings.StartWithWindows = value; break;
            case nameof(OverlaySettings.SoftwareRendering): _draftSettings.SoftwareRendering = value; break;
            case nameof(OverlaySettings.StartupPlaybackRecovery): _draftSettings.StartupPlaybackRecovery = value; break;
            default: return;
        }
        MarkDraftChanged();
    }

    private void LyricsRetry_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.LyricsEnabled)
        {
            StatusText = "请先勾选「启用」并点击「应用」，再重新查找歌词。";
            return;
        }

        if (_lyricsWindow is not { IsVisible: true }) ShowLyricsWindow();
        _lyricsWindow?.RequestManualReload();
        StatusText = "正在重新查找当前歌曲的歌词……";
    }

    private void ClearLyricsCache_Click(object sender, RoutedEventArgs e)
    {
        _lyricsService.ClearCache();
        if (_settings.LyricsEnabled)
        {
            if (_lyricsWindow is not { IsVisible: true }) ShowLyricsWindow();
            _lyricsWindow?.RequestManualReload();
            StatusText = "歌词缓存已清除，正在重新查找当前歌曲的歌词。";
        }
        else
        {
            StatusText = "歌词缓存已清除。勾选「启用」并应用后即可重新查找。";
        }
    }

    /// <summary>
    /// Live commit for numeric lyric/recovery inputs: typing must light up 「应用」 immediately, while
    /// the text itself is only normalised on LostFocus/Apply so a half-typed value is never rewritten.
    /// </summary>
    private void LyricsNumberBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings || _discardingChanges || _draftSettings is null || sender is not TextBox box) return;
        switch (box.Name)
        {
            case nameof(LyricsFontSizeBox):
                if (TryParseFiniteNumber(box.Text, out double fontSize)) _draftSettings.LyricsFontSize = Math.Clamp(fontSize, 10, 120);
                break;
            case nameof(LyricsTranslationFontSizeBox):
                if (TryParseFiniteNumber(box.Text, out double translationSize)) _draftSettings.LyricsTranslationFontSize = Math.Clamp(translationSize, 8, 96);
                break;
            case nameof(RecoveryWaitSecondsBox):
                if (int.TryParse(box.Text, out int seconds)) _draftSettings.RecoveryWaitSeconds = Math.Clamp(seconds, 0, 600);
                break;
            case nameof(RecoveryQueueXBox):
                if (int.TryParse(box.Text, out int queueX)) _draftSettings.RecoveryQueueXPercent = Math.Clamp(queueX, 0, 100);
                break;
            case nameof(RecoveryQueueYBox):
                if (int.TryParse(box.Text, out int queueY)) _draftSettings.RecoveryQueueYPercent = Math.Clamp(queueY, 0, 100);
                break;
            case nameof(RecoverySongXBox):
                if (int.TryParse(box.Text, out int songX)) _draftSettings.RecoverySongXPercent = Math.Clamp(songX, 0, 100);
                break;
            case nameof(RecoverySongYBox):
                if (int.TryParse(box.Text, out int songY)) _draftSettings.RecoverySongYPercent = Math.Clamp(songY, 0, 100);
                break;
        }
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
        LyricsOpacityBox.Text = (_draftSettings.LyricsOpacity * 100).ToString("0.##", CultureInfo.CurrentCulture);
    }

    private void LyricsColor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings || _draftSettings is null || sender is not TextBox box
            || !LyricStyle.TryParseColor(box.Text, out _)) return;
        SetLyricsColor(box, LyricStyle.Normalize(box.Text));
        if (box == LyricsColorBox) UpdateLyricsColorSwatch();
        MarkDraftChanged();
    }

    private void LyricsColor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        string previous = GetLyricsColor(box);
        string color = LyricStyle.TryParseColor(box.Text, out _) ? LyricStyle.Normalize(box.Text) : previous;
        box.Text = color;
        SetLyricsColor(box, color);
        if (box == LyricsColorBox) UpdateLyricsColorSwatch();
        MarkDraftChanged();
    }

    private string GetLyricsColor(TextBox box) => box.Name switch
    {
        nameof(LyricsCurrentColorBox) => _draftSettings.LyricsCurrentColor,
        nameof(LyricsNextColorBox) => _draftSettings.LyricsNextColor,
        _ => _draftSettings.LyricsColor
    };

    private void SetLyricsColor(TextBox box, string color)
    {
        switch (box.Name)
        {
            case nameof(LyricsCurrentColorBox): _draftSettings.LyricsCurrentColor = color; break;
            case nameof(LyricsNextColorBox): _draftSettings.LyricsNextColor = color; break;
            default: _draftSettings.LyricsColor = color; break;
        }
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
        // ApplyButton is deliberately not focusable; a TextBox may retain focus when clicked,
        // so LostFocus-only handlers are not a reliable commit boundary.
        _draftSettings.LyricsFontSize = ParseFiniteNumber(LyricsFontSizeBox.Text, _draftSettings.LyricsFontSize, 10, 120);
        LyricsFontSizeBox.Text = _draftSettings.LyricsFontSize.ToString("0.##");

        _draftSettings.LyricsTranslationFontSize = ParseFiniteNumber(LyricsTranslationFontSizeBox.Text, _draftSettings.LyricsTranslationFontSize, 8, 96);
        LyricsTranslationFontSizeBox.Text = _draftSettings.LyricsTranslationFontSize.ToString("0.##");

        _draftSettings.LyricsOffsetMilliseconds = int.TryParse(LyricsOffsetBox.Text, out int offset)
            ? Math.Clamp(offset, -5000, 5000) : _draftSettings.LyricsOffsetMilliseconds;
        LyricsOffsetBox.Text = _draftSettings.LyricsOffsetMilliseconds.ToString();

        NormalizeRecoveryWaitSeconds();
        NormalizeRecoveryCoordinates();

        _draftSettings.LyricsOpacity = LyricStyle.ParseOpacityPercent(LyricsOpacityBox.Text, _draftSettings.LyricsOpacity);
        LyricsOpacityBox.Text = (_draftSettings.LyricsOpacity * 100).ToString("0.##", CultureInfo.CurrentCulture);
        _draftSettings.LyricsColor = NormalizeColorOrKeep(LyricsColorBox.Text, _draftSettings.LyricsColor);
        _draftSettings.LyricsCurrentColor = NormalizeColorOrKeep(LyricsCurrentColorBox.Text, _draftSettings.LyricsCurrentColor);
        _draftSettings.LyricsNextColor = NormalizeColorOrKeep(LyricsNextColorBox.Text, _draftSettings.LyricsNextColor);
        LyricsColorBox.Text = _draftSettings.LyricsColor;
        LyricsCurrentColorBox.Text = _draftSettings.LyricsCurrentColor;
        LyricsNextColorBox.Text = _draftSettings.LyricsNextColor;
        UpdateLyricsColorSwatch();
    }

    private static bool TryParseFiniteNumber(string text, out double value)
    {
        if ((double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value))
            return true;
        value = 0;
        return false;
    }

    private static double ParseFiniteNumber(string text, double fallback, double minimum, double maximum) =>
        TryParseFiniteNumber(text, out double value)
            ? Math.Clamp(value, minimum, maximum)
            : Math.Clamp(fallback, minimum, maximum);

    private static string NormalizeColorOrKeep(string text, string previous) =>
        LyricStyle.TryParseColor(text, out _) ? LyricStyle.Normalize(text) : previous;

    private sealed class Win32WindowHandle : Forms.IWin32Window
    {
        public Win32WindowHandle(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    private void LyricsSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;

        _draftSettings.LyricsFontSize = TryParseFiniteNumber(LyricsFontSizeBox.Text, out double size)
            ? Math.Clamp(size, 10, 120) : _draftSettings.LyricsFontSize;
        _draftSettings.LyricsTranslationFontSize = TryParseFiniteNumber(LyricsTranslationFontSizeBox.Text, out double translationSize)
            ? Math.Clamp(translationSize, 8, 96) : _draftSettings.LyricsTranslationFontSize;
        _draftSettings.LyricsAlignment = LyricsAlignmentBox.SelectedValue?.ToString() ?? "Center";
        _draftSettings.LyricsOrientation = LyricsOrientationBox.SelectedValue?.ToString() ?? "Horizontal";
        _draftSettings.LyricsVerticalAlignment = LyricsVerticalAlignmentBox.SelectedValue?.ToString() ?? "Center";
        _draftSettings.LyricsVerticalAnchor = LyricsVerticalAnchorBox.SelectedValue?.ToString() ?? "Center";
        _draftSettings.LyricsWordVerticalLayout = LyricsWordVerticalLayoutBox.SelectedValue?.ToString() ?? "RightStacked";
        _draftSettings.LyricsNonWordVerticalLayout = LyricsNonWordVerticalLayoutBox.SelectedValue?.ToString() ?? "Diagonal";
        _draftSettings.LyricsNextLineMode = LyricsNextLineModeBox.SelectedValue?.ToString() ?? "NextLine";
        _draftSettings.LyricsVerticalTranslationLayout = LyricsVerticalTranslationLayoutBox.SelectedValue?.ToString() ?? "BottomSmall";
        _draftSettings.LyricsTranslationEnabled = LyricsTranslationCheckBox.IsChecked == true;
        _draftSettings.LyricsHorizontalTranslationLayout = LyricsHorizontalTranslationLayoutBox.SelectedValue?.ToString() ?? "BottomRightSmall";
        if (int.TryParse(LyricsOffsetBox.Text, out int offset)) _draftSettings.LyricsOffsetMilliseconds = Math.Clamp(offset, -5000, 5000);
        UpdateVerticalControlsEnabled();
        _draftSettings.LyricsBackgroundEnabled = LyricsBackgroundCheckBox.IsChecked == true;
        if (TryParseFiniteNumber(LyricsFontSizeBox.Text, out _)) LyricsFontSizeBox.Text = _draftSettings.LyricsFontSize.ToString("0.##", CultureInfo.CurrentCulture);
        if (TryParseFiniteNumber(LyricsTranslationFontSizeBox.Text, out _)) LyricsTranslationFontSizeBox.Text = _draftSettings.LyricsTranslationFontSize.ToString("0.##", CultureInfo.CurrentCulture);
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
        NormalizeRecoveryWaitSeconds(useDefaultOnInvalid: true);
        MarkDraftChanged();
    }

    private void RecoveryCoordinates_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        NormalizeRecoveryCoordinates(useDefaultsOnInvalid: true);
        MarkDraftChanged();
    }

    private void NormalizeRecoveryWaitSeconds(bool useDefaultOnInvalid = false)
    {
        _draftSettings.RecoveryWaitSeconds = int.TryParse(RecoveryWaitSecondsBox.Text, out int seconds)
            ? Math.Clamp(seconds, 0, 600) : useDefaultOnInvalid ? 2 : _draftSettings.RecoveryWaitSeconds;
        RecoveryWaitSecondsBox.Text = _draftSettings.RecoveryWaitSeconds.ToString();
    }

    private void NormalizeRecoveryCoordinates(bool useDefaultsOnInvalid = false)
    {
        _draftSettings.RecoveryQueueXPercent = ReadPercent(RecoveryQueueXBox, useDefaultsOnInvalid ? 85 : _draftSettings.RecoveryQueueXPercent);
        _draftSettings.RecoveryQueueYPercent = ReadPercent(RecoveryQueueYBox, useDefaultsOnInvalid ? 3 : _draftSettings.RecoveryQueueYPercent);
        _draftSettings.RecoverySongXPercent = ReadPercent(RecoverySongXBox, useDefaultsOnInvalid ? 24 : _draftSettings.RecoverySongXPercent);
        _draftSettings.RecoverySongYPercent = ReadPercent(RecoverySongYBox, useDefaultsOnInvalid ? 12 : _draftSettings.RecoverySongYPercent);
        RecoveryQueueXBox.Text = _draftSettings.RecoveryQueueXPercent.ToString();
        RecoveryQueueYBox.Text = _draftSettings.RecoveryQueueYPercent.ToString();
        RecoverySongXBox.Text = _draftSettings.RecoverySongXPercent.ToString();
        RecoverySongYBox.Text = _draftSettings.RecoverySongYPercent.ToString();
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
                _ = Dispatcher.BeginInvoke(() => StatusText = "已执行开机恢复播放操作；请确认歌曲和队列是否正确。 ");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "StartupPlaybackRecovery");
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.BeginInvoke(() => StatusText = $"开机恢复播放操作失败：{ex.Message}");
        }
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
            OverlaySettings previouslyApplied = CloneSettings(_settings);
            CopySettings(_draftSettings, _settings);
            if (!RegisterHotkeys())
            {
                // Keep the last applied settings intact if the hotkey service cannot be reset.
                // The edited draft remains in the UI so the user can retry without losing work.
                CopySettings(previouslyApplied, _settings);
                StatusText = string.IsNullOrWhiteSpace(_hotkeyRegistrationWarning)
                    ? "快捷键无法重新注册，设置尚未应用。"
                    : _hotkeyRegistrationWarning;
                return;
            }
            // Persist the settings first: the startup registration below is best-effort and
            // must not be able to fail an otherwise valid apply (the Run key can be locked
            // down by policy or security software).
            _settingsService.Save(_settings);
            // Only touch the registry when the stored state disagrees with reality: applying an
            // unrelated lyric change must not re-enable an entry the user disabled in Windows.
            string? startupError = null;
            bool startupOk = _settings.StartWithWindows == StartupService.IsEnabled
                || StartupService.TrySetEnabled(_settings.StartWithWindows, out startupError);
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
            string applyStatus = startupOk
                ? string.IsNullOrWhiteSpace(startupError)
                    ? "设置已应用并保存。"
                    : $"设置已应用并保存；{startupError}"
                : $"设置已应用并保存；开机启动未能启用（{startupError}），其余设置已生效。";
            StatusText = string.IsNullOrWhiteSpace(_hotkeyRegistrationWarning)
                ? applyStatus
                : applyStatus + Environment.NewLine + _hotkeyRegistrationWarning;
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

    private string GetDraftSettingValue(string tag) => KeyboardHotkeyBindingManager.GetSettingValue(_draftSettings, ParseAction(tag));

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
        // A tray/menu command can still be queued while the app is shutting down. Showing a
        // window that has already been closed throws "关闭窗口后，无法设置可见性…", which used to
        // surface as an "运行异常" dialog on exit. A closed window is gone from Application.Windows.
        if (_isExiting || Application.Current is null || !Application.Current.Windows.Cast<Window>().Contains(this)) return;
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

    private static AppAction ParseAction(string tag) => Enum.TryParse(tag, out AppAction action) && Enum.IsDefined(action)
        ? action : throw new ArgumentOutOfRangeException(nameof(tag), tag, null);

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
