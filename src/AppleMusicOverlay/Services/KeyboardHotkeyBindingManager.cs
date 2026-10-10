using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public interface IHotkeySnapshotRegistrar
{
    bool TryRegisterSnapshot(IReadOnlyDictionary<AppAction, string> hotkeys, AppAction requiredAction);
}

public enum HotkeyApplyFailureKind
{
    None,
    InternalConflict,
    RegistrationFailed
}

public sealed record HotkeyApplyResult(bool Success, string Message, HotkeyApplyFailureKind FailureKind = HotkeyApplyFailureKind.None, AppAction? ConflictAction = null);

public static class KeyboardHotkeyBindingManager
{
    public static Dictionary<AppAction, string> CreateSnapshot(OverlaySettings settings) => new()
    {
        [AppAction.PreviousTrack] = settings.KeyboardPrevious.Trim(),
        [AppAction.NextTrack] = settings.KeyboardNext.Trim(),
        [AppAction.TogglePlayPause] = settings.KeyboardToggle.Trim(),
        [AppAction.VolumeUp] = settings.KeyboardVolumeUp.Trim(),
        [AppAction.VolumeDown] = settings.KeyboardVolumeDown.Trim()
    };

    public static HotkeyApplyResult Apply(OverlaySettings settings, AppAction action, string hotkeyText, IHotkeySnapshotRegistrar registrar)
    {
        string normalized = hotkeyText.Trim();
        Dictionary<AppAction, string> candidate = CreateSnapshot(settings);
        candidate[action] = normalized;
        AppAction? duplicate = FindDuplicate(candidate, action, normalized);
        if (duplicate != null)
            return new(false, $"该快捷键已被“{GetLabel(duplicate.Value)}”使用。", HotkeyApplyFailureKind.InternalConflict, duplicate);
        if (!registrar.TryRegisterSnapshot(candidate, action))
            return new(false, "快捷键注册失败，可能已被系统或其他程序占用。", HotkeyApplyFailureKind.RegistrationFailed);
        SetSetting(settings, action, normalized);
        return new(true, string.IsNullOrWhiteSpace(normalized) ? "快捷键已清除。" : $"已保存：{normalized}");
    }

    private static AppAction? FindDuplicate(Dictionary<AppAction, string> values, AppAction current, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var pair in values)
            if (pair.Key != current && string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase)) return pair.Key;
        return null;
    }

    private static void SetSetting(OverlaySettings settings, AppAction action, string value)
    {
        switch (action)
        {
            case AppAction.PreviousTrack: settings.KeyboardPrevious = value; break;
            case AppAction.NextTrack: settings.KeyboardNext = value; break;
            case AppAction.TogglePlayPause: settings.KeyboardToggle = value; break;
            case AppAction.VolumeUp: settings.KeyboardVolumeUp = value; break;
            case AppAction.VolumeDown: settings.KeyboardVolumeDown = value; break;
        }
    }

    private static string GetLabel(AppAction action) => action switch
    {
        AppAction.PreviousTrack => "上一曲",
        AppAction.NextTrack => "下一曲",
        AppAction.TogglePlayPause => "播放 / 暂停",
        AppAction.VolumeUp => "Apple Music 音量 +5%",
        AppAction.VolumeDown => "Apple Music 音量 -5%",
        _ => action.ToString()
    };
}
