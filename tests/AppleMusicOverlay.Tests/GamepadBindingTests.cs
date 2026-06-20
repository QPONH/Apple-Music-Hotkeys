using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;
using Windows.Gaming.Input;

namespace AppleMusicOverlay.Tests;

public sealed class GamepadBindingTests
{
    [Fact]
    public void FormatterUsesStableOrderAndDeviceSpecificNames()
    {
        var binding = GamepadBinding.FromButtons([
            GamepadButton.FaceSouth,
            GamepadButton.DPadRight,
            GamepadButton.View
        ]);

        Assert.Equal("Create + → + ×", GamepadBindingFormatter.Format(binding, GamepadDeviceKind.DualSense));
        Assert.Equal("View + → + A", GamepadBindingFormatter.Format(binding, GamepadDeviceKind.Xbox));
    }

    [Fact]
    public void EquivalentBindingsIgnorePressOrderAndDuplicateButtons()
    {
        GamepadBinding first = GamepadBinding.FromButtons([
            GamepadButton.FaceSouth,
            GamepadButton.View,
            GamepadButton.FaceSouth
        ]);
        GamepadBinding second = GamepadBinding.FromButtons([
            GamepadButton.View,
            GamepadButton.FaceSouth
        ]);

        Assert.Equal(first.Key, second.Key);
        Assert.Equal(2, first.Buttons.Count);
    }

    [Fact]
    public void CaptureWaitsForNeutralBeforeListening()
    {
        var session = new GamepadBindingCaptureSession(GamepadDeviceKind.DualSense, AppAction.NextTrack, new GamepadBindingSet());

        session.Update(new[] { GamepadButton.FaceSouth }, TimeSpan.FromMilliseconds(16));
        Assert.Equal(GamepadCaptureState.WaitingForNeutral, session.State);

        session.Update([], TimeSpan.FromMilliseconds(80));
        Assert.Equal(GamepadCaptureState.WaitingForNeutral, session.State);

        session.Update([], TimeSpan.FromMilliseconds(25));
        Assert.Equal(GamepadCaptureState.Listening, session.State);
    }

    [Fact]
    public void CaptureCompletesCombinationAfterAllButtonsReleasedInAnyOrder()
    {
        var session = ReadySession();

        session.Update(new[] { GamepadButton.View }, TimeSpan.FromMilliseconds(16));
        session.Update(new[] { GamepadButton.View, GamepadButton.DPadRight }, TimeSpan.FromMilliseconds(16));
        session.Update(new[] { GamepadButton.View }, TimeSpan.FromMilliseconds(16));
        session.Update([], TimeSpan.FromMilliseconds(16));

        Assert.Equal(GamepadCaptureState.Completed, session.State);
        Assert.Equal("Create + →", session.DisplayText);
        Assert.Equal("View + →", GamepadBindingFormatter.Format(session.PendingBinding!, GamepadDeviceKind.Xbox));
    }

    [Fact]
    public void CaptureRejectsMoreThanThreeButtonsWithoutSaving()
    {
        var session = ReadySession();

        session.Update(new[] { GamepadButton.View, GamepadButton.DPadRight, GamepadButton.FaceSouth, GamepadButton.LeftShoulder }, TimeSpan.FromMilliseconds(16));

        Assert.Equal(GamepadCaptureState.TooManyButtons, session.State);
        Assert.Null(session.PendingBinding);
    }

    [Fact]
    public void SingleButtonRequiresConfirmationBeforeCompleted()
    {
        var session = ReadySession();

        session.Update(new[] { GamepadButton.FaceSouth }, TimeSpan.FromMilliseconds(16));
        session.Update([], TimeSpan.FromMilliseconds(16));

        Assert.Equal(GamepadCaptureState.SingleButtonWarning, session.State);
        Assert.Equal("×", session.DisplayText);

        session.ConfirmSingleButton();

        Assert.Equal(GamepadCaptureState.Completed, session.State);
        Assert.Equal("×", GamepadBindingFormatter.Format(session.PendingBinding!, GamepadDeviceKind.DualSense));
    }

    [Fact]
    public void RetrySingleButtonReturnsToWaitingForNeutral()
    {
        var session = ReadySession();
        session.Update(new[] { GamepadButton.FaceSouth }, TimeSpan.FromMilliseconds(16));
        session.Update([], TimeSpan.FromMilliseconds(16));

        session.Retry();

        Assert.Equal(GamepadCaptureState.WaitingForNeutral, session.State);
        Assert.Null(session.PendingBinding);
    }

    [Fact]
    public void CaptureReportsDuplicateBindingConflictAndCanReplaceOriginal()
    {
        var bindings = new GamepadBindingSet
        {
            Next = GamepadBinding.FromButtons([GamepadButton.View, GamepadButton.DPadRight])
        };
        var session = new GamepadBindingCaptureSession(GamepadDeviceKind.DualSense, AppAction.PreviousTrack, bindings);
        session.Update([], TimeSpan.FromMilliseconds(120));

        session.Update(new[] { GamepadButton.DPadRight, GamepadButton.View }, TimeSpan.FromMilliseconds(16));
        session.Update([], TimeSpan.FromMilliseconds(16));

        Assert.Equal(GamepadCaptureState.Conflict, session.State);
        Assert.Equal(AppAction.NextTrack, session.ConflictAction);

        session.ReplaceConflict(bindings);

        Assert.True(bindings.Next.IsEmpty);
        Assert.Equal("Create + →", GamepadBindingFormatter.Format(bindings.Previous, GamepadDeviceKind.DualSense));
    }

    [Fact]
    public void SettingsPreserveSeparateBindingsPerDeviceKind()
    {
        var settings = new OverlaySettings
        {
            XboxGamepadBindings = new GamepadBindingSet
            {
                Next = GamepadBinding.FromButtons([GamepadButton.View, GamepadButton.DPadRight])
            },
            DualSenseGamepadBindings = new GamepadBindingSet
            {
                Next = GamepadBinding.FromButtons([GamepadButton.View, GamepadButton.FaceSouth])
            }
        };

        OverlaySettings normalized = OverlaySettingsNormalizer.Normalize(settings);

        Assert.Equal("View + →", GamepadBindingFormatter.Format(normalized.XboxGamepadBindings.Next, GamepadDeviceKind.Xbox));
        Assert.Equal("Create + ×", GamepadBindingFormatter.Format(normalized.DualSenseGamepadBindings.Next, GamepadDeviceKind.DualSense));
    }

    [Fact]
    public void ButtonReaderUsesTriggerHysteresis()
    {
        var reader = new GamepadButtonReader();

        Assert.DoesNotContain(GamepadButton.LeftTrigger, reader.Read(leftTrigger: 0.45, rightTrigger: 0, buttons: GamepadButtons.None));
        Assert.Contains(GamepadButton.LeftTrigger, reader.Read(leftTrigger: 0.72, rightTrigger: 0, buttons: GamepadButtons.None));
        Assert.Contains(GamepadButton.LeftTrigger, reader.Read(leftTrigger: 0.42, rightTrigger: 0, buttons: GamepadButtons.None));
        Assert.DoesNotContain(GamepadButton.LeftTrigger, reader.Read(leftTrigger: 0.22, rightTrigger: 0, buttons: GamepadButtons.None));
    }

    private static GamepadBindingCaptureSession ReadySession()
    {
        var session = new GamepadBindingCaptureSession(GamepadDeviceKind.DualSense, AppAction.NextTrack, new GamepadBindingSet());
        session.Update([], TimeSpan.FromMilliseconds(120));
        return session;
    }
}
