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
    private sealed record HotkeyInfo(
        string UiLabel, string ConflictLabel,
        Func<OverlaySettings, string> Get,
        Action<OverlaySettings, string> Set);

    private static readonly IReadOnlyDictionary<AppAction, HotkeyInfo> Hotkeys = new Dictionary<AppAction, HotkeyInfo>
    {
        [AppAction.PreviousTrack] = new("上一曲", "上一曲", s => s.KeyboardPrevious, (s, v) => s.KeyboardPrevious = v),
        [AppAction.NextTrack] = new("下一曲", "下一曲", s => s.KeyboardNext, (s, v) => s.KeyboardNext = v),
        [AppAction.TogglePlayPause] = new("播放/暂停", "播放 / 暂停", s => s.KeyboardToggle, (s, v) => s.KeyboardToggle = v),
        [AppAction.VolumeUp] = new("音量+", "Apple Music 音量 +5%", s => s.KeyboardVolumeUp, (s, v) => s.KeyboardVolumeUp = v),
        [AppAction.VolumeDown] = new("音量-", "Apple Music 音量 -5%", s => s.KeyboardVolumeDown, (s, v) => s.KeyboardVolumeDown = v)
    };

    public static Dictionary<AppAction, string> CreateSnapshot(OverlaySettings settings) =>
        Hotkeys.ToDictionary(entry => entry.Key, entry => entry.Value.Get(settings).Trim());

    public static string GetSettingValue(OverlaySettings settings, AppAction action) => Hotkeys[action].Get(settings);
    public static string GetUiLabel(AppAction action) => Hotkeys.TryGetValue(action, out var info) ? info.UiLabel : action.ToString();

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
        if (!HotkeyParser.TryParse(value, out HotkeyDefinition wanted)) return null;
        foreach (var pair in values)
        {
            if (pair.Key != current
                && HotkeyParser.TryParse(pair.Value, out HotkeyDefinition existing)
                && existing == wanted)
                return pair.Key;
        }
        return null;
    }

    private static void SetSetting(OverlaySettings settings, AppAction action, string value) => Hotkeys[action].Set(settings, value);
    private static string GetLabel(AppAction action) => Hotkeys.TryGetValue(action, out var info) ? info.ConflictLabel : action.ToString();
}
