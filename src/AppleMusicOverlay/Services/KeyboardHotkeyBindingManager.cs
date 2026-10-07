using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public interface IHotkeySnapshotRegistrar
{
    bool TryRegisterSnapshot(IReadOnlyDictionary<AppAction, string> hotkeys);
}

public enum HotkeyApplyFailureKind
{
    None,
    InternalConflict,
    RegistrationFailed
}

public sealed record HotkeyApplyResult(
    bool Success,
    string Message,
    HotkeyApplyFailureKind FailureKind = HotkeyApplyFailureKind.None,
    AppAction? ConflictAction = null,
    string HotkeyText = "");

public static class KeyboardHotkeyBindingManager
{
    public static HotkeyApplyResult Apply(
        OverlaySettings settings,
        AppAction action,
        string hotkeyText,
        IHotkeySnapshotRegistrar registrar)
    {
        string normalized = NormalizeHotkeyText(hotkeyText);
        Dictionary<AppAction, string> candidate = CreateSnapshot(settings);
        candidate[action] = normalized;

        AppAction? duplicate = FindDuplicate(candidate, action, normalized);
        if (duplicate != null)
        {
            return CreateInternalConflictResult(normalized, duplicate.Value);
        }

        if (!registrar.TryRegisterSnapshot(candidate))
        {
            return CreateRegistrationFailedResult(normalized);
        }

        SetSetting(settings, action, normalized);
        return CreateSuccessResult(normalized);
    }

    public static HotkeyApplyResult ApplyReplacingConflict(
        OverlaySettings settings,
        AppAction action,
        AppAction conflictAction,
        string hotkeyText,
        IHotkeySnapshotRegistrar registrar)
    {
        string normalized = NormalizeHotkeyText(hotkeyText);
        Dictionary<AppAction, string> candidate = CreateSnapshot(settings);
        candidate[conflictAction] = string.Empty;
        candidate[action] = normalized;

        AppAction? duplicate = FindDuplicate(candidate, action, normalized);
        if (duplicate != null)
        {
            return CreateInternalConflictResult(normalized, duplicate.Value);
        }

        if (!registrar.TryRegisterSnapshot(candidate))
        {
            return CreateRegistrationFailedResult(normalized);
        }

        SetSetting(settings, conflictAction, string.Empty);
        SetSetting(settings, action, normalized);
        return CreateSuccessResult(normalized);
    }

    public static Dictionary<AppAction, string> CreateSnapshot(OverlaySettings settings)
    {
        return new Dictionary<AppAction, string>
        {
            [AppAction.PreviousTrack] = NormalizeHotkeyText(settings.KeyboardPrevious),
            [AppAction.NextTrack] = NormalizeHotkeyText(settings.KeyboardNext),
            [AppAction.TogglePlayPause] = NormalizeHotkeyText(settings.KeyboardToggle),
            [AppAction.VolumeUp] = NormalizeHotkeyText(settings.KeyboardVolumeUp),
            [AppAction.VolumeDown] = NormalizeHotkeyText(settings.KeyboardVolumeDown)
        };
    }

    private static HotkeyApplyResult CreateInternalConflictResult(string hotkeyText, AppAction conflictAction)
    {
        return new HotkeyApplyResult(
            false,
            LocalizationService.Current.Format("HotkeyConflictTemplate", hotkeyText, GamepadBindingActions.GetLabel(conflictAction)),
            HotkeyApplyFailureKind.InternalConflict,
            conflictAction,
            hotkeyText);
    }

    private static HotkeyApplyResult CreateRegistrationFailedResult(string hotkeyText)
    {
        return new HotkeyApplyResult(
            false,
            LocalizationService.Current.Text("HotkeyRegistrationFailed"),
            HotkeyApplyFailureKind.RegistrationFailed,
            null,
            hotkeyText);
    }

    private static HotkeyApplyResult CreateSuccessResult(string hotkeyText)
    {
        return new HotkeyApplyResult(
            true,
            string.IsNullOrWhiteSpace(hotkeyText)
                ? LocalizationService.Current.Text("AutoSavedUnset")
                : LocalizationService.Current.Format("AutoSavedTemplate", hotkeyText));
    }

    private static AppAction? FindDuplicate(Dictionary<AppAction, string> candidate, AppAction currentAction, string hotkeyText)
    {
        if (string.IsNullOrWhiteSpace(hotkeyText))
        {
            return null;
        }

        foreach ((AppAction action, string existing) in candidate)
        {
            if (action == currentAction)
            {
                continue;
            }

            if (string.Equals(existing, hotkeyText, StringComparison.OrdinalIgnoreCase))
            {
                return action;
            }
        }

        return null;
    }

    private static void SetSetting(OverlaySettings settings, AppAction action, string hotkeyText)
    {
        switch (action)
        {
            case AppAction.PreviousTrack:
                settings.KeyboardPrevious = hotkeyText;
                break;
            case AppAction.NextTrack:
                settings.KeyboardNext = hotkeyText;
                break;
            case AppAction.TogglePlayPause:
                settings.KeyboardToggle = hotkeyText;
                break;
            case AppAction.ShowCurrentTrack:
                settings.KeyboardTestOverlay = hotkeyText;
                break;
            case AppAction.FavoriteCurrentTrack:
                settings.KeyboardFavorite = hotkeyText;
                break;
            case AppAction.VolumeUp:
                settings.KeyboardVolumeUp = hotkeyText;
                break;
            case AppAction.VolumeDown:
                settings.KeyboardVolumeDown = hotkeyText;
                break;
        }
    }

    private static string NormalizeHotkeyText(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
    }
}
