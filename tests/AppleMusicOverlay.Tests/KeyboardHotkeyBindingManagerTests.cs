using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class KeyboardHotkeyBindingManagerTests
{
    [Fact]
    public void ApplyDoesNotMutateSettingsWhenRegistrationFails()
    {
        var settings = new OverlaySettings
        {
            KeyboardPrevious = "Ctrl+Shift+Left",
            KeyboardNext = "Ctrl+Shift+Right"
        };
        var registrar = new RecordingHotkeyRegistrar(success: false);

        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(settings, AppAction.PreviousTrack, "Ctrl+Alt+P", registrar);

        Assert.False(result.Success);
        Assert.Equal(HotkeyApplyFailureKind.RegistrationFailed, result.FailureKind);
        Assert.Equal("Ctrl+Shift+Left", settings.KeyboardPrevious);
        Assert.Equal("Ctrl+Shift+Right", settings.KeyboardNext);
        Assert.Equal("Ctrl+Alt+P", registrar.LastAttempt[AppAction.PreviousTrack]);
    }

    [Fact]
    public void ApplyMutatesSettingsOnlyAfterRegistrationSucceeds()
    {
        var settings = new OverlaySettings
        {
            KeyboardPrevious = "Ctrl+Shift+Left",
            KeyboardNext = "Ctrl+Shift+Right"
        };
        var registrar = new RecordingHotkeyRegistrar(success: true);

        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(settings, AppAction.PreviousTrack, "Ctrl+Alt+P", registrar);

        Assert.True(result.Success);
        Assert.Equal("Ctrl+Alt+P", settings.KeyboardPrevious);
        Assert.Equal("Ctrl+Alt+P", registrar.LastAttempt[AppAction.PreviousTrack]);
    }

    [Fact]
    public void ClearBindingSavesEmptyTextAfterRegistrationSucceeds()
    {
        var settings = new OverlaySettings
        {
            KeyboardPrevious = "Ctrl+Shift+Left",
            KeyboardNext = "Ctrl+Shift+Right"
        };
        var registrar = new RecordingHotkeyRegistrar(success: true);

        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(settings, AppAction.PreviousTrack, string.Empty, registrar);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, settings.KeyboardPrevious);
        Assert.Equal(string.Empty, registrar.LastAttempt[AppAction.PreviousTrack]);
    }

    [Fact]
    public void DuplicateHotkeysReturnConflictMetadataBeforeRegistration()
    {
        var settings = new OverlaySettings
        {
            KeyboardPrevious = "Ctrl+Shift+Left",
            KeyboardNext = "Ctrl+Shift+Right"
        };
        var registrar = new RecordingHotkeyRegistrar(success: true);

        HotkeyApplyResult result = KeyboardHotkeyBindingManager.Apply(settings, AppAction.PreviousTrack, "Ctrl+Shift+Right", registrar);

        Assert.False(result.Success);
        Assert.Equal(HotkeyApplyFailureKind.InternalConflict, result.FailureKind);
        Assert.Equal(AppAction.NextTrack, result.ConflictAction);
        Assert.Equal("Ctrl+Shift+Right", result.HotkeyText);
        Assert.Empty(registrar.LastAttempt);
        Assert.Equal("Ctrl+Shift+Left", settings.KeyboardPrevious);
    }

    [Fact]
    public void ReplaceConflictClearsOriginalActionOnlyAfterRegistrationSucceeds()
    {
        var settings = new OverlaySettings
        {
            KeyboardPrevious = "Ctrl+Shift+Left",
            KeyboardNext = "Ctrl+Shift+Right"
        };
        var registrar = new RecordingHotkeyRegistrar(success: true);

        HotkeyApplyResult result = KeyboardHotkeyBindingManager.ApplyReplacingConflict(
            settings,
            AppAction.NextTrack,
            AppAction.PreviousTrack,
            "Ctrl+Shift+Left",
            registrar);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, settings.KeyboardPrevious);
        Assert.Equal("Ctrl+Shift+Left", settings.KeyboardNext);
        Assert.Equal(string.Empty, registrar.LastAttempt[AppAction.PreviousTrack]);
        Assert.Equal("Ctrl+Shift+Left", registrar.LastAttempt[AppAction.NextTrack]);
    }

    [Fact]
    public void ReplaceConflictKeepsSettingsWhenRegistrationFails()
    {
        var settings = new OverlaySettings
        {
            KeyboardPrevious = "Ctrl+Shift+Left",
            KeyboardNext = "Ctrl+Shift+Right"
        };
        var registrar = new RecordingHotkeyRegistrar(success: false);

        HotkeyApplyResult result = KeyboardHotkeyBindingManager.ApplyReplacingConflict(
            settings,
            AppAction.NextTrack,
            AppAction.PreviousTrack,
            "Ctrl+Shift+Left",
            registrar);

        Assert.False(result.Success);
        Assert.Equal(HotkeyApplyFailureKind.RegistrationFailed, result.FailureKind);
        Assert.Equal("Ctrl+Shift+Left", settings.KeyboardPrevious);
        Assert.Equal("Ctrl+Shift+Right", settings.KeyboardNext);
    }

    private sealed class RecordingHotkeyRegistrar : IHotkeySnapshotRegistrar
    {
        private readonly bool _success;

        public RecordingHotkeyRegistrar(bool success)
        {
            _success = success;
        }

        public IReadOnlyDictionary<AppAction, string> LastAttempt { get; private set; } =
            new Dictionary<AppAction, string>();

        public bool TryRegisterSnapshot(IReadOnlyDictionary<AppAction, string> hotkeys)
        {
            LastAttempt = hotkeys.ToDictionary(pair => pair.Key, pair => pair.Value);
            return _success;
        }
    }
}
