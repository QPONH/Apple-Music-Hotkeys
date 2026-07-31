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
        ["OverlayPageDescription"] = "\u8c03\u6574\u60ac\u6d6e\u7a97\u7684\u663e\u793a\u3001\u5185\u5bb9\u548c\u884c\u4e3a\u3002",
        ["CoverShadowSize"] = "封面阴影大小",
        ["CoverShadowSizeDescription"] = "调整封面投影向外扩散的范围，保持柔和渐隐。",
        ["DisplaySeconds"] = "显示时长",
        ["DisplaySecondsDescription"] = "\u63a7\u5236\u4e34\u65f6\u60ac\u6d6e\u7a97\u505c\u7559\u591a\u4e45\u3002",
        ["SecondsSuffix"] = "{0:0} 秒",
        ["ScalePercent"] = "缩放大小",
        ["ScalePercentDescription"] = "按百分比调整悬浮窗尺寸。",
        ["DisplayBehavior"] = "显示行为",
        ["DisplayBehaviorDescription"] = "\u63a7\u5236\u60ac\u6d6e\u7a97\u4f55\u65f6\u663e\u793a\u3002",
        ["PinnedBadge"] = "常驻",
        ["PauseOverlay"] = "常驻显示悬浮窗",
        ["ShowOverlayOnTrackChange"] = "\u5207\u6b4c\u65f6\u81ea\u52a8\u663e\u793a\u60ac\u6d6e\u7a97",
        ["ShowOverlayOnTrackChangeDescription"] = "\u5f53\u524d\u6b4c\u66f2\u53d1\u751f\u5207\u6362\u65f6\u81ea\u52a8\u663e\u793a\u60ac\u6d6e\u7a97\u3002",
        ["PauseOverlayRequired"] = "\u81ea\u52a8\u9690\u85cf\u548c\u4f4d\u7f6e\u8c03\u6574\u4ec5\u9002\u7528\u4e8e\u5e38\u9a7b\u6a21\u5f0f\u3002",
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
        ["OverlayPageDescription"] = "Adjust the overlay's appearance, content, and behavior.",
        ["CoverShadowSize"] = "Cover shadow size",
        ["CoverShadowSizeDescription"] = "Adjust how far the cover shadow spreads while keeping the fade soft.",
        ["DisplaySeconds"] = "Display duration",
        ["DisplaySecondsDescription"] = "Controls how long a temporary overlay stays visible.",
        ["SecondsSuffix"] = "{0:0} sec",
        ["ScalePercent"] = "Scale",
        ["ScalePercentDescription"] = "Resize the overlay by percentage.",
        ["DisplayBehavior"] = "Display behavior",
        ["DisplayBehaviorDescription"] = "Controls when the overlay appears.",
        ["PinnedBadge"] = "Pinned",
        ["PauseOverlay"] = "Keep overlay visible",
        ["ShowOverlayOnTrackChange"] = "Show overlay on track change",
        ["ShowOverlayOnTrackChangeDescription"] = "Show the overlay automatically when the current track changes.",
        ["PauseOverlayRequired"] = "Auto-hide and position adjustment are available only in persistent mode.",
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

    private static readonly Dictionary<string, string> OverlayFontZhCn = new(StringComparer.Ordinal)
    {
        ["OverlayTrackFont"] = "歌曲信息字体",
        ["OverlayTrackFontDescription"] = "设置悬浮窗中歌名和歌手使用的字体。",
        ["OverlayTrackFontDefault"] = "默认",
        ["OverlayTrackFontSpotifyMix"] = "Spotify Mix",
        ["OverlayTrackFontSfPro"] = "Apple SF Pro",
        ["OverlayFontUpdatedTitle"] = "字体已更新",
        ["OverlayFontUpdatedMessage"] = "悬浮窗歌曲信息字体已切换。"
    };

    private static readonly Dictionary<string, string> OverlayFontEnUs = new(StringComparer.Ordinal)
    {
        ["OverlayTrackFont"] = "Track information font",
        ["OverlayTrackFontDescription"] = "Choose the font used for the song title and artist in the overlay.",
        ["OverlayTrackFontDefault"] = "Default",
        ["OverlayTrackFontSpotifyMix"] = "Spotify Mix",
        ["OverlayTrackFontSfPro"] = "Apple SF Pro",
        ["OverlayFontUpdatedTitle"] = "Font updated",
        ["OverlayFontUpdatedMessage"] = "The overlay track information font has been changed."
    };

    private static readonly Dictionary<string, string> FavoriteActionZhCn = new(StringComparer.Ordinal)
    {
        ["FavoriteCurrentTrack"] = "\u6536\u85cf\u5f53\u524d\u6b4c\u66f2",
        ["FavoriteCurrentTrackDescription"] = "Apple Music \u6dfb\u52a0\u6536\u85cf\uff1b\u7f51\u6613\u4e91\u97f3\u4e50\u5207\u6362\u559c\u6b22\u6216\u53d6\u6d88\u559c\u6b22\u3002",
        ["FavoriteNoTrackTitle"] = "\u672a\u68c0\u6d4b\u5230\u6b4c\u66f2",
        ["FavoriteNoTrackMessage"] = "\u8bf7\u5148\u5728\u652f\u6301\u7684\u97f3\u4e50\u5e94\u7528\u4e2d\u64ad\u653e\u4e00\u9996\u6b4c\u66f2\u3002",
        ["FavoriteUnsupportedTitle"] = "\u5f53\u524d\u6765\u6e90\u4e0d\u652f\u6301\u6536\u85cf",
        ["FavoriteUnsupportedMessage"] = "\u6b64\u529f\u80fd\u76ee\u524d\u652f\u6301 Apple Music \u548c\u7f51\u6613\u4e91\u97f3\u4e50\u3002",
        ["FavoriteInProgressTitle"] = "\u6b63\u5728\u6536\u85cf",
        ["FavoriteInProgressMessage"] = "\u6b63\u5728\u5c06\u5f53\u524d\u6b4c\u66f2\u52a0\u5165\u6536\u85cf\u2026\u2026",
        ["CloudMusicUnfavoriteInProgressTitle"] = "\u6b63\u5728\u53d6\u6d88\u6536\u85cf",
        ["CloudMusicUnfavoriteInProgressMessage"] = "\u6b63\u5728\u4ece\u7f51\u6613\u4e91\u97f3\u4e50\u201c\u6211\u559c\u6b22\u7684\u97f3\u4e50\u201d\u4e2d\u79fb\u9664\u5f53\u524d\u6b4c\u66f2\u2026\u2026",
        ["CloudMusicNativeFavoriteHotkey"] = "\u7f51\u6613\u4e91\u6536\u85cf\u8054\u52a8",
        ["CloudMusicNativeFavoriteHotkeyDescription"] = "MusicFloat \u6267\u884c\u6536\u85cf\u65f6\uff0c\u4f1a\u8f6c\u53d1\u4e3a\u7f51\u6613\u4e91\u97f3\u4e50\u7684\u201c\u559c\u6b22\u6b4c\u66f2\u201d\u5168\u5c40\u5feb\u6377\u952e\u3002\u8bf7\u5c06\u4e0b\u65b9\u6309\u952e\u4e0e\u7f51\u6613\u4e91\u8bbe\u7f6e\u4fdd\u6301\u4e00\u81f4\u3002",
        ["CloudMusicHotkeyModifiers"] = "\u7ec4\u5408\u952e",
        ["CloudMusicHotkeyMainKey"] = "\u4e3b\u952e",
        ["CloudMusicNativeFavoriteHotkeySavedTemplate"] = "\u7f51\u6613\u4e91\u6536\u85cf\u8054\u52a8\u5df2\u8bbe\u4e3a\uff1a{0}\u3002",
        ["CloudMusicNativeFavoriteHotkeyInvalid"] = "\u8bf7\u9009\u62e9\u81f3\u5c11\u4e00\u4e2a\u4fee\u9970\u952e\u548c\u4e00\u4e2a\u4e3b\u952e\u3002",
        ["CloudMusicNativeFavoriteHotkeyConflictTemplate"] = "\u8be5\u7ec4\u5408\u5df2\u88ab MusicFloat \u7528\u4e8e\u201c{0}\u201d\u3002\u8bf7\u4fdd\u6301\u7f51\u6613\u4e91\u8f6c\u53d1\u5feb\u6377\u952e\u4e0e MusicFloat \u81ea\u8eab\u5feb\u6377\u952e\u4e0d\u540c\u3002",
        ["FavoriteAddedTitle"] = "\u5df2\u52a0\u5165\u6536\u85cf",
        ["FavoriteAddedMessage"] = "\u5f53\u524d\u6b4c\u66f2\u5df2\u6dfb\u52a0\u5230\u64ad\u653e\u5668\u81ea\u8eab\u7684\u6536\u85cf\u3002",
        ["FavoriteAlreadyTitle"] = "\u5f53\u524d\u6b4c\u66f2\u5df2\u6536\u85cf",
        ["FavoriteAlreadyMessage"] = "\u65e0\u9700\u91cd\u590d\u6dfb\u52a0\u3002",
        ["CloudMusicTriggerReleaseTimedOutTitle"] = "\u8bf7\u677e\u5f00\u6536\u85cf\u5feb\u6377\u952e",
        ["CloudMusicTriggerReleaseTimedOutMessage"] = "\u68c0\u6d4b\u5230 MusicFloat \u6536\u85cf\u5feb\u6377\u952e\u4ecd\u5904\u4e8e\u6309\u4e0b\u72b6\u6001\uff0c\u56e0\u6b64\u6ca1\u6709\u5411\u7f51\u6613\u4e91\u53d1\u9001\u64cd\u4f5c\u3002\u8bf7\u677e\u5f00\u6309\u952e\u540e\u91cd\u8bd5\u3002",
        ["CloudMusicStateUnavailableTitle"] = "\u65e0\u6cd5\u8bfb\u53d6\u7f51\u6613\u4e91\u6536\u85cf\u72b6\u6001",
        ["CloudMusicStateUnavailableMessage"] = "MusicFloat \u6682\u65f6\u65e0\u6cd5\u786e\u8ba4\u5f53\u524d\u6b4c\u66f2\u662f\u5426\u5df2\u6536\u85cf\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5\u3002",
        ["CloudMusicDispatchFailedTitle"] = "\u7f51\u6613\u4e91\u5feb\u6377\u952e\u8f6c\u53d1\u5931\u8d25",
        ["CloudMusicDispatchFailedMessage"] = "Windows \u672a\u80fd\u5b8c\u6574\u53d1\u9001\u7f51\u6613\u4e91\u6536\u85cf\u7ec4\u5408\u952e\u3002\u8bf7\u91cd\u8bd5\uff1b\u5982\u679c\u6301\u7eed\u51fa\u73b0\uff0c\u8bf7\u68c0\u67e5\u4e24\u4e2a\u5e94\u7528\u7684\u6743\u9650\u7ea7\u522b\u3002",
        ["CloudMusicConfirmationTimedOutTitle"] = "\u7f51\u6613\u4e91\u672a\u786e\u8ba4\u6536\u85cf\u53d8\u5316",
        ["CloudMusicConfirmationTimedOutMessage"] = "\u5feb\u6377\u952e\u5df2\u53d1\u9001\uff0c\u4f46\u5728\u7b49\u5f85\u65f6\u95f4\u5185\u6ca1\u6709\u68c0\u6d4b\u5230\u7f51\u6613\u4e91\u6536\u85cf\u72b6\u6001\u53d8\u5316\u3002",
        ["CloudMusicTrackChangedTitle"] = "\u6b4c\u66f2\u5df2\u5207\u6362",
        ["CloudMusicTrackChangedMessage"] = "\u6536\u85cf\u5feb\u6377\u952e\u53d1\u9001\u524d\u6b4c\u66f2\u53d1\u751f\u4e86\u53d8\u5316\uff0c\u672c\u6b21\u64cd\u4f5c\u5df2\u53d6\u6d88\u3002",
        ["FavoriteFailedTitle"] = "\u6536\u85cf\u64cd\u4f5c\u5931\u8d25",
        ["FavoriteFailedMessage"] = "\u672a\u80fd\u5b8c\u6210\u64cd\u4f5c\u3002\u4f7f\u7528\u7f51\u6613\u4e91\u65f6\uff0c\u8bf7\u786e\u8ba4\u5168\u5c40\u5feb\u6377\u952e\u5df2\u5f00\u542f\uff0c\u4e14 MusicFloat \u4e2d\u7684\u201c\u7f51\u6613\u4e91\u6536\u85cf\u8054\u52a8\u201d\u4e0e\u7f51\u6613\u4e91\u8bbe\u7f6e\u4fdd\u6301\u4e00\u81f4\u3002"
    };

    private static readonly Dictionary<string, string> FavoriteActionEnUs = new(StringComparer.Ordinal)
    {
        ["FavoriteCurrentTrack"] = "Favorite current song",
        ["FavoriteCurrentTrackDescription"] = "Add an Apple Music favorite, or toggle Like in CloudMusic.",
        ["FavoriteNoTrackTitle"] = "No song detected",
        ["FavoriteNoTrackMessage"] = "Play a song in a supported music app first.",
        ["FavoriteUnsupportedTitle"] = "Favorites not supported",
        ["FavoriteUnsupportedMessage"] = "This feature currently supports Apple Music and CloudMusic.",
        ["FavoriteInProgressTitle"] = "Adding to Favorites",
        ["FavoriteInProgressMessage"] = "Adding the current song to Apple Music Favorites...",
        ["CloudMusicUnfavoriteInProgressTitle"] = "Removing from Favorites",
        ["CloudMusicUnfavoriteInProgressMessage"] = "Removing the current song from CloudMusic's Liked Songs...",
        ["CloudMusicNativeFavoriteHotkey"] = "CloudMusic favorite link",
        ["CloudMusicNativeFavoriteHotkeyDescription"] = "When MusicFloat favorites a song, it forwards CloudMusic's global Like shortcut. Keep the selection below identical to the shortcut configured in CloudMusic.",
        ["CloudMusicHotkeyModifiers"] = "Key combination",
        ["CloudMusicHotkeyMainKey"] = "Main key",
        ["CloudMusicNativeFavoriteHotkeySavedTemplate"] = "CloudMusic favorite link set to {0}.",
        ["CloudMusicNativeFavoriteHotkeyInvalid"] = "Select at least one modifier and one main key.",
        ["CloudMusicNativeFavoriteHotkeyConflictTemplate"] = "This combination is already used by MusicFloat for \"{0}\". Keep the CloudMusic forwarding shortcut different from MusicFloat shortcuts.",
        ["FavoriteAddedTitle"] = "Added to Favorites",
        ["FavoriteAddedMessage"] = "The current song has been added to the player's own Favorites.",
        ["FavoriteAlreadyTitle"] = "Already Favorited",
        ["FavoriteAlreadyMessage"] = "No duplicate action was needed.",
        ["CloudMusicTriggerReleaseTimedOutTitle"] = "Release the favorite shortcut",
        ["CloudMusicTriggerReleaseTimedOutMessage"] = "The MusicFloat favorite shortcut was still held, so nothing was sent to CloudMusic. Release the keys and try again.",
        ["CloudMusicStateUnavailableTitle"] = "CloudMusic favorite state unavailable",
        ["CloudMusicStateUnavailableMessage"] = "MusicFloat could not confirm whether the current song is liked. Try again shortly.",
        ["CloudMusicDispatchFailedTitle"] = "CloudMusic shortcut dispatch failed",
        ["CloudMusicDispatchFailedMessage"] = "Windows could not send the complete CloudMusic favorite shortcut. Try again, and check both apps' privilege levels if it persists.",
        ["CloudMusicConfirmationTimedOutTitle"] = "CloudMusic did not confirm the change",
        ["CloudMusicConfirmationTimedOutMessage"] = "The shortcut was sent, but MusicFloat did not detect a CloudMusic favorite-state change in time.",
        ["CloudMusicTrackChangedTitle"] = "The song changed",
        ["CloudMusicTrackChangedMessage"] = "The song changed before the favorite shortcut was sent, so this action was cancelled.",
        ["FavoriteFailedTitle"] = "Favorite action failed",
        ["FavoriteFailedMessage"] = "MusicFloat could not complete the action. For CloudMusic, enable global shortcuts and keep the CloudMusic favorite link identical to CloudMusic's setting."
    };

    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> ResourceSets =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            [DefaultLanguageCode] = ZhCn,
            [EnglishLanguageCode] = EnUs
        };

    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> OverlayFontResourceSets =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            [DefaultLanguageCode] = OverlayFontZhCn,
            [EnglishLanguageCode] = OverlayFontEnUs
        };

    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> FavoriteActionResourceSets =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            [DefaultLanguageCode] = FavoriteActionZhCn,
            [EnglishLanguageCode] = FavoriteActionEnUs
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

    public static string GetPreferredStartupLanguageCode(CultureInfo culture)
    {
        return culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? DefaultLanguageCode
            : EnglishLanguageCode;
    }

    public static IReadOnlySet<string> GetResourceKeys(string languageCode)
    {
        string normalized = NormalizeLanguageCode(languageCode);
        HashSet<string> keys = ResourceSets[normalized].Keys.ToHashSet(StringComparer.Ordinal);
        keys.UnionWith(OverlayFontResourceSets[normalized].Keys);
        keys.UnionWith(FavoriteActionResourceSets[normalized].Keys);
        return keys;
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

        if (OverlayFontResourceSets[_languageCode].TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (FavoriteActionResourceSets[_languageCode].TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (OverlayFontZhCn.TryGetValue(key, out string? overlayFontFallback) && !string.IsNullOrWhiteSpace(overlayFontFallback))
        {
            return overlayFontFallback;
        }

        if (FavoriteActionZhCn.TryGetValue(key, out string? favoriteActionFallback) && !string.IsNullOrWhiteSpace(favoriteActionFallback))
        {
            return favoriteActionFallback;
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
