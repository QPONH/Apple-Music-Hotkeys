using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class GamepadShortcutRuntimeTests
{
    [Fact]
    public void SingleButtonTriggersOnceUntilReleased()
    {
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet
        {
            Toggle = GamepadBinding.FromButtons([GamepadButton.FaceSouth])
        };

        Assert.Equal(AppAction.TogglePlayPause, runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Null(runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Null(runtime.Update([], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Equal(AppAction.TogglePlayPause, runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
    }

    [Fact]
    public void CombinationTriggersOnceAndSuppressesInnerSingle()
    {
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet
        {
            Toggle = GamepadBinding.FromButtons([GamepadButton.FaceSouth]),
            Next = GamepadBinding.FromButtons([GamepadButton.LeftShoulder, GamepadButton.FaceSouth])
        };

        Assert.Null(runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Equal(AppAction.NextTrack, runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Null(runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
    }

    [Fact]
    public void ThreeButtonCombinationWinsOverTwoButtonAndSingle()
    {
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet
        {
            Toggle = GamepadBinding.FromButtons([GamepadButton.FaceSouth]),
            Next = GamepadBinding.FromButtons([GamepadButton.LeftShoulder, GamepadButton.FaceSouth]),
            Previous = GamepadBinding.FromButtons([GamepadButton.View, GamepadButton.LeftShoulder, GamepadButton.FaceSouth])
        };

        AppAction? action = runtime.Update(
            [GamepadButton.View, GamepadButton.LeftShoulder, GamepadButton.FaceSouth],
            TimeSpan.FromMilliseconds(16),
            bindings);

        Assert.Equal(AppAction.PreviousTrack, action);
    }

    [Fact]
    public void DelayedSingleTriggersAfterConfirmationWindowWhenNoComboArrives()
    {
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet
        {
            Toggle = GamepadBinding.FromButtons([GamepadButton.FaceSouth]),
            Next = GamepadBinding.FromButtons([GamepadButton.LeftShoulder, GamepadButton.FaceSouth])
        };

        Assert.Null(runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Null(runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(70), bindings));
        Assert.Equal(AppAction.TogglePlayPause, runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(30), bindings));
    }

    [Fact]
    public void CombinationWithinConfirmationWindowCancelsPendingSingle()
    {
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet
        {
            Toggle = GamepadBinding.FromButtons([GamepadButton.FaceSouth]),
            Next = GamepadBinding.FromButtons([GamepadButton.LeftShoulder, GamepadButton.FaceSouth])
        };

        Assert.Null(runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Equal(AppAction.NextTrack, runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(40), bindings));
        Assert.Null(runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(80), bindings));
    }

    [Fact]
    public void RuntimeDoesNotTriggerWhileCaptureIsActive()
    {
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet
        {
            Next = GamepadBinding.FromButtons([GamepadButton.LeftShoulder, GamepadButton.FaceSouth])
        };

        Assert.Null(runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings, isCaptureActive: true));
        Assert.Null(runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Null(runtime.Update([], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Equal(AppAction.NextTrack, runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
    }

    [Fact]
    public void ResumeAfterCaptureWithNeutralInputAllowsNextPressImmediately()
    {
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet
        {
            Next = GamepadBinding.FromButtons([GamepadButton.LeftShoulder, GamepadButton.FaceSouth])
        };

        Assert.Null(runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings, isCaptureActive: true));

        runtime.ResumeAfterCapture([]);

        Assert.Equal(AppAction.NextTrack, runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
    }

    [Fact]
    public void ResumeAfterCaptureWithPressedInputWaitsForNeutralBeforeTriggering()
    {
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet
        {
            Next = GamepadBinding.FromButtons([GamepadButton.LeftShoulder, GamepadButton.FaceSouth])
        };

        Assert.Null(runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings, isCaptureActive: true));

        runtime.ResumeAfterCapture([GamepadButton.LeftShoulder, GamepadButton.FaceSouth]);

        Assert.Null(runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Null(runtime.Update([], TimeSpan.FromMilliseconds(16), bindings));
        Assert.Equal(AppAction.NextTrack, runtime.Update([GamepadButton.LeftShoulder, GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
    }

    [Fact]
    public void ResetClearsPendingAndLockedState()
    {
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet
        {
            Toggle = GamepadBinding.FromButtons([GamepadButton.FaceSouth])
        };

        Assert.Equal(AppAction.TogglePlayPause, runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
        runtime.Reset();
        Assert.Equal(AppAction.TogglePlayPause, runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
    }

    [Fact]
    public void DeletedBindingStopsTriggeringImmediately()
    {
        var runtime = new GamepadShortcutRuntime();
        var bindings = new GamepadBindingSet
        {
            Toggle = GamepadBinding.FromButtons([GamepadButton.FaceSouth])
        };

        Assert.Equal(AppAction.TogglePlayPause, runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
        runtime.Reset();
        bindings.Clear(AppAction.TogglePlayPause);

        Assert.Null(runtime.Update([GamepadButton.FaceSouth], TimeSpan.FromMilliseconds(16), bindings));
    }
}
