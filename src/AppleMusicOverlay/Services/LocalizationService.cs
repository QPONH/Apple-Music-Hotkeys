using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;

namespace AppleMusicOverlay.Services;

public sealed class LanguageOption(string languageCode, string displayName)
{
    public string LanguageCode { get; } = languageCode;
    public string DisplayName { get; } = displayName;
}

public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string DefaultLanguageCode = "zh-CN";
    public const string EnglishLanguageCode = "en-US";

    private static readonly Dictionary<string, string> ZhCn = new(StringComparer.Ordinal)
    {
        ["AppSubtitle"] = "桌面音乐悬浮工具",
        ["ControlPanelTitle"] = "控制面板",
        ["ControlPanelDescription"] = "状态、悬浮窗、快捷键、设置",
        ["CurrentStatus"] = "当前状态",
        ["Refresh"] = "刷新",
        ["TestOverlay"] = "测试悬浮窗",
        ["NavCurrent"] = "当前播放",
        ["NavOverlay"] = "悬浮窗",
        ["NavShortcuts"] = "快捷键",
        ["NavGeneral"] = "常用选项",
        ["CurrentPageTitle"] = "当前播放",
        ["CurrentPageDescription"] = "检查媒体源、控制播放，并确认悬浮窗当前状态。",
        ["RefreshSources"] = "刷新源",
        ["NoTrackTitle"] = "未检测到歌曲",
        ["NoTrackArtist"] = "播放音乐后自动同步",
        ["PreviousTrack"] = "上一首",
        ["NextTrack"] = "下一首",
        ["Play"] = "播放",
        ["Pause"] = "暂停",
        ["CaptureSource"] = "抓取源",
        ["CaptureSourceDescription"] = "当前通过 Windows SMTC 读取 Apple Music PWA 或浏览器媒体会话。",
        ["CaptureSourceHint"] = "如果切歌间隙被其他应用抢占，可固定到检测到的 Edge / Chrome 会话。",
        ["AutoSelect"] = "自动选择",
        ["UnknownMedia"] = "未知媒体",
        ["UndetectedSuffix"] = "未检测到",
        ["OverlayPageTitle"] = "悬浮窗设置",
        ["OverlayPageDescription"] = "这里只调整封面悬浮窗的显示参数，不改变悬浮窗本身的 UI 或动效。",
        ["CoverShadowSize"] = "封面阴影大小",
        ["CoverShadowSizeDescription"] = "调整封面投影向外扩散的范围，保持柔和渐隐。",
        ["DisplaySeconds"] = "显示时长",
        ["DisplaySecondsDescription"] = "控制切歌提示停留多久。",
        ["SecondsSuffix"] = "{0:0} 秒",
        ["ScalePercent"] = "缩放大小",
        ["ScalePercentDescription"] = "按百分比调整悬浮窗尺寸。",
        ["DisplayBehavior"] = "显示行为",
        ["DisplayBehaviorDescription"] = "控制悬浮窗是否常驻显示。",
        ["PinnedBadge"] = "常驻",
        ["PauseOverlay"] = "常驻显示悬浮窗",
        ["PauseOverlayRequired"] = "开启常驻显示悬浮窗后可用",
        ["AutoHideOnMouseNear"] = "鼠标靠近时自动隐藏",
        ["AutoHideOnMouseNearDescription"] = "鼠标靠近封面时，悬浮窗会暂时淡出。",
        ["OverlayPosition"] = "悬浮窗位置",
        ["OverlayPositionDescription"] = "调整悬浮窗在屏幕上的显示位置。",
        ["AdjustPosition"] = "调整位置",
        ["ShowTitle"] = "悬浮窗显示歌名",
        ["ShowArtist"] = "悬浮窗显示歌手",
        ["ShortcutsPageTitle"] = "快捷键设置",
        ["ShortcutsPageDescription"] = "点击任一快捷键框后，按下想要的快捷键；Esc 取消。录入成功后会自动保存。",
        ["PreviousTrackDescription"] = "切到上一首歌曲",
        ["NextTrackDescription"] = "切到下一首歌曲",
        ["TogglePlayPause"] = "播放 / 暂停",
        ["TogglePlayPauseDescription"] = "切换当前媒体播放状态",
        ["ShowOverlay"] = "显示悬浮窗",
        ["ShowOverlayDescription"] = "用当前歌曲显示一次悬浮窗",
        ["Delete"] = "删除",
        ["Clear"] = "清除",
        ["RefreshGamepads"] = "重新检测",
        ["GamepadNotDetected"] = "未检测到手柄",
        ["GamepadNotDetectedDescription"] = "请通过 USB 或蓝牙连接 Xbox 或 DualSense 手柄。",
        ["GamepadShortcuts"] = "手柄快捷键",
        ["GamepadShortcutsDescription"] = "建议使用两个或以上手柄按键组合，可以降低游戏中的误触概率。",
        ["GamepadFutureDescription"] = "已检测到手柄。下一阶段会在这里配置单键与组合键。",
        ["GeneralPageTitle"] = "常用选项",
        ["GeneralPageDescription"] = "管理应用语言和全局行为。",
        ["Language"] = "应用语言",
        ["LanguageDescription"] = "选择 MusicFloat 的界面语言。",
        ["WindowBehavior"] = "窗口行为",
        ["CloseToTray"] = "关闭窗口时最小化到托盘",
        ["CloseToTrayDescription"] = "点击关闭按钮时保持 MusicFloat 在后台运行。",
        ["CurrentStatusWaiting"] = "等待播放源",
        ["CurrentStatusNoPlayingMusic"] = "未检测到正在播放的音乐",
        ["CurrentStatusConnectedTemplate"] = "已连接：{0}",
        ["CurrentStatusMinimizedToTray"] = "已最小化到托盘",
        ["CurrentStatusNoMediaSource"] = "未检测到媒体源",
        ["CurrentStatusMediaSourceCountTemplate"] = "检测到 {0} 个媒体源",
        ["CurrentStatusMediaRefreshFailed"] = "媒体源刷新失败",
        ["CurrentStatusNoCurrentTrack"] = "未读取到当前播放歌曲",
        ["CurrentRefreshInProgressTitle"] = "正在刷新",
        ["CurrentRefreshInProgressMessage"] = "正在重新检测媒体会话和播放信息……",
        ["CurrentRefreshCompleteTitle"] = "刷新完成",
        ["CurrentRefreshCompleteMessage"] = "已更新当前播放信息。",
        ["CurrentNoMusicTitle"] = "未检测到音乐",
        ["CurrentNoMusicMessage"] = "暂未找到正在播放的媒体会话。",
        ["OverlayShownTitle"] = "已显示悬浮窗",
        ["OverlayShownMessage"] = "正在使用当前设置进行预览。",
        ["OverlaySavedTitle"] = "设置已保存",
        ["OverlaySavedMessage"] = "悬浮窗设置已自动更新。",
        ["OverlayAdjustingTitle"] = "正在调整位置",
        ["OverlayAdjustingMessage"] = "拖动悬浮窗到合适位置，然后选择完成或取消。",
        ["OverlayPositionSavedTitle"] = "位置已保存",
        ["OverlayPositionSavedMessage"] = "悬浮窗位置已自动保存。",
        ["OverlayPositionCancelledTitle"] = "已取消调整",
        ["OverlayPositionCancelledMessage"] = "悬浮窗已恢复到调整前的位置。",
        ["GeneralLanguageChangedTitle"] = "语言已切换",
        ["GeneralLanguageChangedMessage"] = "界面语言已切换为简体中文。",
        ["GeneralSavedTitle"] = "设置已保存",
        ["GeneralSavedMessage"] = "常用选项已自动更新。",
        ["HotkeyCapturePrompt"] = "按下想要的快捷键",
        ["HotkeyUnset"] = "未设置",
        ["HotkeyEditingStatus"] = "快捷键修改中：按下 Ctrl / Alt / Shift / Win + 一个按键，Esc 取消。",
        ["HotkeyNeedModifier"] = "请使用 Ctrl / Alt / Shift / Win 组合键。",
        ["HotkeyNeedMainKey"] = "请再按一个字母、数字、方向键或功能键。",
        ["HotkeyPromptTitle"] = "快捷键提示",
        ["HotkeyEditTitle"] = "快捷键修改",
        ["KeyboardHotkeyEditTitle"] = "键盘快捷键修改",
        ["GamepadHotkeyEditTitle"] = "手柄快捷键修改",
        ["HotkeySaved"] = "快捷键已保存并生效。",
        ["HotkeyRegisterPartialFailed"] = "部分快捷键未注册，可能已被系统或其他应用占用。",
        ["HotkeyPressCombination"] = "请按组合键",
        ["HotkeyDetectedTemplate"] = "已检测：{0}。松开全部按键后自动保存。",
        ["HotkeyReleaseToSaveTemplate"] = "松开全部按键后自动保存：{0}。",
        ["HotkeyDuplicateTemplate"] = "快捷键重复：{0} 同时用于「{1}」和「{2}」。请修改后再保存。",
        ["HotkeyConflictTemplate"] = "{0} 已用于‘{1}’。",
        ["HotkeyRegistrationFailed"] = "该快捷键无法注册，可能已被系统或其他程序占用，请重新选择。",
        ["AutoSavedUnset"] = "已自动保存：未设置",
        ["AutoSavedTemplate"] = "已自动保存：{0}",
        ["Listening"] = "正在监听",
        ["ClickToBind"] = "点击绑定",
        ["GamepadReleaseAllButtons"] = "请先松开手柄上的所有按键。",
        ["GamepadListenInstruction"] = "按下要绑定的手柄按键或组合键，松开所有按键后完成，Esc 取消。",
        ["GamepadDetectedTemplate"] = "已检测：{0}\n松开所有按键后完成，Esc 取消。",
        ["GamepadNoAvailableDevice"] = "未检测到可用手柄。",
        ["GamepadUnsupportedDevice"] = "当前设备暂不支持按键录入。",
        ["GamepadTooManyButtons"] = "最多可以绑定 3 个手柄按键，请重新录入。",
        ["GamepadSingleButtonWarning"] = "单个按键可能与游戏操作冲突，推荐使用组合键。",
        ["GamepadDisconnectedCapture"] = "手柄已断开，请重新连接后再录入。",
        ["GamepadDisconnectedCancelled"] = "手柄已断开，录入已取消。",
        ["Retry"] = "重新录入",
        ["Cancel"] = "取消",
        ["UseAnyway"] = "仍然使用",
        ["ReplaceOriginalBinding"] = "替换原绑定",
        ["ConnectedMultipleGamepads"] = "已连接多个手柄",
        ["CurrentDeviceTemplate"] = "当前设备：{0}",
        ["XboxGamepad"] = "Xbox 手柄",
        ["DualSenseGamepad"] = "DualSense 手柄",
        ["CompatibleGamepad"] = "兼容手柄",
        ["ConnectedGamepadTemplate"] = "已连接 {0}",
        ["GamepadButtonSouth"] = "按键下",
        ["GamepadButtonEast"] = "按键右",
        ["GamepadButtonWest"] = "按键左",
        ["GamepadButtonNorth"] = "按键上",
        ["OtherAction"] = "其他操作",
        ["PositionEditBarTitle"] = "正在调整位置",
        ["Done"] = "完成",
        ["TrayOpen"] = "打开 MusicFloat",
        ["TrayShowOverlay"] = "显示悬浮窗",
        ["TrayExit"] = "退出 MusicFloat"
    };

    private static readonly Dictionary<string, string> EnUs = new(StringComparer.Ordinal)
    {
        ["AppSubtitle"] = "Desktop music overlay",
        ["ControlPanelTitle"] = "Control Panel",
        ["ControlPanelDescription"] = "Status, overlay, shortcuts, settings",
        ["CurrentStatus"] = "Current Status",
        ["Refresh"] = "Refresh",
        ["TestOverlay"] = "Test Overlay",
        ["NavCurrent"] = "Now Playing",
        ["NavOverlay"] = "Overlay",
        ["NavShortcuts"] = "Shortcuts",
        ["NavGeneral"] = "General",
        ["CurrentPageTitle"] = "Now Playing",
        ["CurrentPageDescription"] = "Check media sources, control playback, and review the overlay state.",
        ["RefreshSources"] = "Refresh Sources",
        ["NoTrackTitle"] = "No song detected",
        ["NoTrackArtist"] = "Music will sync after playback starts",
        ["PreviousTrack"] = "Previous",
        ["NextTrack"] = "Next",
        ["Play"] = "Play",
        ["Pause"] = "Pause",
        ["CaptureSource"] = "Capture Source",
        ["CaptureSourceDescription"] = "Reads Apple Music PWA or browser media sessions through Windows SMTC.",
        ["CaptureSourceHint"] = "Pin a detected Edge or Chrome session if another app takes focus between songs.",
        ["AutoSelect"] = "Auto select",
        ["UnknownMedia"] = "Unknown media",
        ["UndetectedSuffix"] = "not detected",
        ["OverlayPageTitle"] = "Overlay Settings",
        ["OverlayPageDescription"] = "Adjust only the cover overlay display parameters without changing its UI or motion.",
        ["CoverShadowSize"] = "Cover shadow size",
        ["CoverShadowSizeDescription"] = "Adjust how far the cover shadow spreads while keeping the fade soft.",
        ["DisplaySeconds"] = "Display duration",
        ["DisplaySecondsDescription"] = "Controls how long the song-change overlay stays visible.",
        ["SecondsSuffix"] = "{0:0} sec",
        ["ScalePercent"] = "Scale",
        ["ScalePercentDescription"] = "Resize the overlay by percentage.",
        ["DisplayBehavior"] = "Display behavior",
        ["DisplayBehaviorDescription"] = "Controls whether the overlay stays visible.",
        ["PinnedBadge"] = "Pinned",
        ["PauseOverlay"] = "Keep overlay visible",
        ["PauseOverlayRequired"] = "Available after keeping the overlay visible",
        ["AutoHideOnMouseNear"] = "Auto-hide near pointer",
        ["AutoHideOnMouseNearDescription"] = "Temporarily fades the overlay when the pointer approaches the cover.",
        ["OverlayPosition"] = "Overlay position",
        ["OverlayPositionDescription"] = "Adjust where the overlay appears on screen.",
        ["AdjustPosition"] = "Adjust Position",
        ["ShowTitle"] = "Show song title",
        ["ShowArtist"] = "Show artist",
        ["ShortcutsPageTitle"] = "Shortcut Settings",
        ["ShortcutsPageDescription"] = "Click a shortcut field, press the desired keys, and use Esc to cancel. Successful input is saved automatically.",
        ["PreviousTrackDescription"] = "Skip to the previous song",
        ["NextTrackDescription"] = "Skip to the next song",
        ["TogglePlayPause"] = "Play / Pause",
        ["TogglePlayPauseDescription"] = "Toggle current media playback",
        ["ShowOverlay"] = "Show overlay",
        ["ShowOverlayDescription"] = "Show the overlay once with the current song",
        ["Delete"] = "Delete",
        ["Clear"] = "Clear",
        ["RefreshGamepads"] = "Refresh",
        ["GamepadNotDetected"] = "No gamepad detected",
        ["GamepadNotDetectedDescription"] = "Connect an Xbox or DualSense gamepad through USB or Bluetooth.",
        ["GamepadShortcuts"] = "Gamepad shortcuts",
        ["GamepadShortcutsDescription"] = "Use two or more gamepad buttons to reduce accidental input in games.",
        ["GamepadFutureDescription"] = "A gamepad is detected. Single-button and combo binding will be configured here next.",
        ["GeneralPageTitle"] = "General",
        ["GeneralPageDescription"] = "Manage the app language and general behavior.",
        ["Language"] = "Language",
        ["LanguageDescription"] = "Choose the display language for MusicFloat.",
        ["WindowBehavior"] = "Window behavior",
        ["CloseToTray"] = "Minimize to tray when closing",
        ["CloseToTrayDescription"] = "Keep MusicFloat running in the background when the window is closed.",
        ["CurrentStatusWaiting"] = "Waiting for playback source",
        ["CurrentStatusNoPlayingMusic"] = "No playing music detected",
        ["CurrentStatusConnectedTemplate"] = "Connected: {0}",
        ["CurrentStatusMinimizedToTray"] = "Minimized to tray",
        ["CurrentStatusNoMediaSource"] = "No media source detected",
        ["CurrentStatusMediaSourceCountTemplate"] = "Detected {0} media sources",
        ["CurrentStatusMediaRefreshFailed"] = "Media source refresh failed",
        ["CurrentStatusNoCurrentTrack"] = "No current song was read",
        ["CurrentRefreshInProgressTitle"] = "Refreshing",
        ["CurrentRefreshInProgressMessage"] = "Checking media sessions and playback information again...",
        ["CurrentRefreshCompleteTitle"] = "Refresh complete",
        ["CurrentRefreshCompleteMessage"] = "Current playback information has been updated.",
        ["CurrentNoMusicTitle"] = "No music detected",
        ["CurrentNoMusicMessage"] = "No active media session was found.",
        ["OverlayShownTitle"] = "Overlay shown",
        ["OverlayShownMessage"] = "Previewing with the current settings.",
        ["OverlaySavedTitle"] = "Settings saved",
        ["OverlaySavedMessage"] = "Overlay settings have been updated.",
        ["OverlayAdjustingTitle"] = "Adjusting position",
        ["OverlayAdjustingMessage"] = "Drag the overlay to a suitable position, then choose done or cancel.",
        ["OverlayPositionSavedTitle"] = "Position saved",
        ["OverlayPositionSavedMessage"] = "Overlay position has been saved.",
        ["OverlayPositionCancelledTitle"] = "Adjustment cancelled",
        ["OverlayPositionCancelledMessage"] = "The overlay has returned to its previous position.",
        ["GeneralLanguageChangedTitle"] = "Language changed",
        ["GeneralLanguageChangedMessage"] = "The interface language is now English.",
        ["GeneralSavedTitle"] = "Settings saved",
        ["GeneralSavedMessage"] = "General settings have been updated.",
        ["HotkeyCapturePrompt"] = "Press the shortcut keys",
        ["HotkeyUnset"] = "Not set",
        ["HotkeyEditingStatus"] = "Editing shortcut: press Ctrl / Alt / Shift / Win plus one key. Esc cancels.",
        ["HotkeyNeedModifier"] = "Use a Ctrl / Alt / Shift / Win key combination.",
        ["HotkeyNeedMainKey"] = "Press one more letter, number, arrow key, or function key.",
        ["HotkeyPromptTitle"] = "Shortcut notice",
        ["HotkeyEditTitle"] = "Edit shortcut",
        ["KeyboardHotkeyEditTitle"] = "Edit keyboard shortcut",
        ["GamepadHotkeyEditTitle"] = "Edit gamepad shortcut",
        ["HotkeySaved"] = "Shortcut saved and applied.",
        ["HotkeyRegisterPartialFailed"] = "Some shortcuts were not registered. They may be used by the system or another app.",
        ["HotkeyPressCombination"] = "Press a combination",
        ["HotkeyDetectedTemplate"] = "Detected: {0}. Release all keys to save automatically.",
        ["HotkeyReleaseToSaveTemplate"] = "Release all keys to save automatically: {0}.",
        ["HotkeyDuplicateTemplate"] = "Duplicate shortcut: {0} is used for \"{1}\" and \"{2}\". Change it before saving.",
        ["HotkeyConflictTemplate"] = "{0} is already used by \"{1}\".",
        ["HotkeyRegistrationFailed"] = "This shortcut cannot be registered. It may be used by the system or another app.",
        ["AutoSavedUnset"] = "Auto-saved: not set",
        ["AutoSavedTemplate"] = "Auto-saved: {0}",
        ["Listening"] = "Listening",
        ["ClickToBind"] = "Click to bind",
        ["GamepadReleaseAllButtons"] = "Release all buttons on the gamepad first.",
        ["GamepadListenInstruction"] = "Press the gamepad button or combo to bind. Release all buttons to finish. Esc cancels.",
        ["GamepadDetectedTemplate"] = "Detected: {0}\nRelease all buttons to finish. Esc cancels.",
        ["GamepadNoAvailableDevice"] = "No available gamepad detected.",
        ["GamepadUnsupportedDevice"] = "The current device does not support button capture.",
        ["GamepadTooManyButtons"] = "You can bind up to 3 gamepad buttons. Try again.",
        ["GamepadSingleButtonWarning"] = "A single button may conflict with game controls. A combo is recommended.",
        ["GamepadDisconnectedCapture"] = "Gamepad disconnected. Reconnect it before binding again.",
        ["GamepadDisconnectedCancelled"] = "Gamepad disconnected. Capture was cancelled.",
        ["Retry"] = "Retry",
        ["Cancel"] = "Cancel",
        ["UseAnyway"] = "Use Anyway",
        ["ReplaceOriginalBinding"] = "Replace Original Binding",
        ["ConnectedMultipleGamepads"] = "Multiple gamepads connected",
        ["CurrentDeviceTemplate"] = "Current device: {0}",
        ["XboxGamepad"] = "Xbox gamepad",
        ["DualSenseGamepad"] = "DualSense gamepad",
        ["CompatibleGamepad"] = "Compatible gamepad",
        ["ConnectedGamepadTemplate"] = "Connected {0}",
        ["GamepadButtonSouth"] = "South button",
        ["GamepadButtonEast"] = "East button",
        ["GamepadButtonWest"] = "West button",
        ["GamepadButtonNorth"] = "North button",
        ["OtherAction"] = "Other action",
        ["PositionEditBarTitle"] = "Adjusting position",
        ["Done"] = "Done",
        ["TrayOpen"] = "Open MusicFloat",
        ["TrayShowOverlay"] = "Show overlay",
        ["TrayExit"] = "Exit MusicFloat"
    };

    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> ResourceSets =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            [DefaultLanguageCode] = ZhCn,
            [EnglishLanguageCode] = EnUs
        };

    private string _languageCode = DefaultLanguageCode;

    public static LocalizationService Current { get; } = new();

    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new(DefaultLanguageCode, "简体中文"),
        new(EnglishLanguageCode, "English")
    ];

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string LanguageCode => _languageCode;

    public FontFamily UiFontFamily => _languageCode == EnglishLanguageCode
        ? new FontFamily("Segoe UI Variable, Segoe UI")
        : new FontFamily("Microsoft YaHei UI");

    public string this[string key] => Text(key);

    public static string NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return DefaultLanguageCode;
        }

        string normalized = languageCode.Trim();
        return ResourceSets.ContainsKey(normalized) ? normalized : DefaultLanguageCode;
    }

    public static IReadOnlySet<string> GetResourceKeys(string languageCode)
    {
        string normalized = NormalizeLanguageCode(languageCode);
        return ResourceSets[normalized].Keys.ToHashSet(StringComparer.Ordinal);
    }

    public void SetLanguage(string? languageCode)
    {
        string normalized = NormalizeLanguageCode(languageCode);
        if (_languageCode == normalized)
        {
            return;
        }

        _languageCode = normalized;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(normalized);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageCode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiFontFamily)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Text(string key)
    {
        if (ResourceSets[_languageCode].TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return ZhCn.TryGetValue(key, out string? fallback) && !string.IsNullOrWhiteSpace(fallback)
            ? fallback
            : key;
    }

    public string Format(string key, params object[] values)
    {
        return string.Format(CultureInfo.CurrentUICulture, Text(key), values);
    }
}
