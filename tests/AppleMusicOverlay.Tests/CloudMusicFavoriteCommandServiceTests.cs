using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class CloudMusicFavoriteCommandServiceTests
{
    [Fact]
    public void SendInputLayoutMatchesWindowsAbiForCurrentArchitecture()
    {
        int expectedSize = IntPtr.Size == 8 ? 40 : 28;

        Assert.Equal(expectedSize, SendInputGlobalShortcutSender.NativeInputSize);
    }

    [Fact]
    public void NativeShortcutPlanHoldsTheCompleteChordBeforeReleasingIt()
    {
        GlobalShortcutDispatchPlan plan = SendInputGlobalShortcutSender.CreatePlan(
            new HotkeyDefinition(0x0003, 'G'));

        Assert.Equal([0x11, 0x12, (ushort)'G'], plan.KeyDownOrder);
        Assert.Equal([(ushort)'G', 0x12, 0x11], plan.KeyUpOrder);
        Assert.True(plan.HoldDuration >= TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task FavoriteDispatchesCloudMusicShortcutAndConfirmsRemoval()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.Favorite,
            OverlayFavoriteVisualState.NotFavorite);
        var sender = new RecordingShortcutSender();
        var service = CreateService(stateReader, sender);

        CloudMusicFavoriteCommandResult result = await service.FavoriteCurrentTrackAsync(
            CreateTrack(),
            "Ctrl+Alt+L");

        Assert.Equal(CloudMusicFavoriteCommandResultKind.Removed, result.Kind);
        Assert.Single(sender.Hotkeys);
        Assert.All(stateReader.ForceRefreshValues, Assert.True);
    }

    [Fact]
    public async Task NotFavoriteDispatchesCloudMusicShortcutAndConfirmsDatabaseState()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.NotFavorite,
            OverlayFavoriteVisualState.Favorite);
        var sender = new RecordingShortcutSender();
        var service = CreateService(stateReader, sender);

        CloudMusicFavoriteCommandResult result = await service.FavoriteCurrentTrackAsync(
            CreateTrack(),
            "Ctrl+Alt+L");

        Assert.Equal(CloudMusicFavoriteCommandResultKind.Added, result.Kind);
        HotkeyDefinition hotkey = Assert.Single(sender.Hotkeys);
        Assert.Equal(0x0003u, hotkey.Modifiers);
        Assert.Equal((uint)'L', hotkey.VirtualKey);
        Assert.All(stateReader.ForceRefreshValues, Assert.True);
    }

    [Fact]
    public async Task CustomCloudMusicShortcutIsParsedAndDispatched()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.NotFavorite,
            OverlayFavoriteVisualState.Favorite);
        var sender = new RecordingShortcutSender();
        var service = CreateService(stateReader, sender);

        CloudMusicFavoriteCommandResult result = await service.FavoriteCurrentTrackAsync(
            CreateTrack(),
            "Ctrl+Shift+K");

        Assert.Equal(CloudMusicFavoriteCommandResultKind.Added, result.Kind);
        HotkeyDefinition hotkey = Assert.Single(sender.Hotkeys);
        Assert.Equal(0x0006u, hotkey.Modifiers);
        Assert.Equal((uint)'K', hotkey.VirtualKey);
    }

    [Fact]
    public async Task InvalidNativeShortcutDoesNotReadOrDispatch()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.NotFavorite);
        var sender = new RecordingShortcutSender();
        var service = CreateService(stateReader, sender);

        CloudMusicFavoriteCommandResult result = await service.FavoriteCurrentTrackAsync(
            CreateTrack(),
            "L");

        Assert.Equal(CloudMusicFavoriteCommandResultKind.InvalidHotkey, result.Kind);
        Assert.Empty(stateReader.ForceRefreshValues);
        Assert.Empty(sender.Hotkeys);
    }

    [Fact]
    public async Task DispatchFailureDoesNotReportFavorite()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.NotFavorite);
        var sender = new RecordingShortcutSender(success: false);
        var service = CreateService(stateReader, sender);

        CloudMusicFavoriteCommandResult result = await service.FavoriteCurrentTrackAsync(
            CreateTrack(),
            "Ctrl+Alt+L");

        Assert.Equal(CloudMusicFavoriteCommandResultKind.DispatchFailed, result.Kind);
        Assert.Single(sender.Hotkeys);
    }

    [Fact]
    public async Task MissingDatabaseConfirmationTimesOutAfterOneNativeDispatch()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.NotFavorite);
        var sender = new RecordingShortcutSender();
        var service = CreateService(stateReader, sender, maxPolls: 3);

        CloudMusicFavoriteCommandResult result = await service.FavoriteCurrentTrackAsync(
            CreateTrack(),
            "Ctrl+Alt+L");

        Assert.Equal(CloudMusicFavoriteCommandResultKind.ConfirmationTimedOut, result.Kind);
        Assert.Single(sender.Hotkeys);
        Assert.Equal(4, stateReader.ForceRefreshValues.Count);
    }

    [Fact]
    public async Task FavoriteThatRemainsFavoriteDoesNotReportRemoval()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.Favorite);
        var sender = new RecordingShortcutSender();
        var service = CreateService(stateReader, sender, maxPolls: 3);

        CloudMusicFavoriteCommandResult result = await service.FavoriteCurrentTrackAsync(
            CreateTrack(),
            "Ctrl+Alt+L");

        Assert.Equal(CloudMusicFavoriteCommandResultKind.ConfirmationTimedOut, result.Kind);
        Assert.Single(sender.Hotkeys);
        Assert.Equal(4, stateReader.ForceRefreshValues.Count);
    }

    [Fact]
    public async Task TrackChangeBeforeDispatchDoesNotFavoriteTheNewCloudMusicSong()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.NotFavorite);
        var sender = new RecordingShortcutSender();
        var service = CreateService(stateReader, sender);

        CloudMusicFavoriteCommandResult result = await service.FavoriteCurrentTrackAsync(
            CreateTrack(),
            "Ctrl+Alt+L",
            isStillCurrent: () => false);

        Assert.Equal(CloudMusicFavoriteCommandResultKind.TrackChanged, result.Kind);
        Assert.Empty(sender.Hotkeys);
    }

    [Fact]
    public async Task KeyboardTriggerMustBeReleasedBeforeNativeShortcutIsDispatched()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.NotFavorite,
            OverlayFavoriteVisualState.Favorite);
        var sender = new RecordingShortcutSender();
        var releaseWaiter = new ControlledHotkeyReleaseWaiter();
        var service = CreateService(stateReader, sender, releaseWaiter: releaseWaiter);

        Task<CloudMusicFavoriteCommandResult> request =
            service.FavoriteCurrentTrackAsync(
                CreateTrack(),
                "Ctrl+Alt+G",
                triggeringHotkey: "Ctrl+Space");

        await releaseWaiter.Started.Task;
        Assert.Empty(sender.Hotkeys);
        releaseWaiter.Complete(released: true);

        CloudMusicFavoriteCommandResult result = await request;

        Assert.Equal(CloudMusicFavoriteCommandResultKind.Added, result.Kind);
        HotkeyDefinition trigger = Assert.Single(releaseWaiter.Hotkeys);
        Assert.Equal(0x0002u, trigger.Modifiers);
        Assert.Equal((uint)' ', trigger.VirtualKey);
        Assert.Single(sender.Hotkeys);
    }

    [Fact]
    public async Task HeldKeyboardTriggerFailsWithoutTogglingCloudMusic()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.NotFavorite);
        var sender = new RecordingShortcutSender();
        var releaseWaiter = new ControlledHotkeyReleaseWaiter();
        var service = CreateService(stateReader, sender, releaseWaiter: releaseWaiter);

        Task<CloudMusicFavoriteCommandResult> request =
            service.FavoriteCurrentTrackAsync(
                CreateTrack(),
                "Ctrl+Alt+G",
                triggeringHotkey: "Ctrl+Space");

        await releaseWaiter.Started.Task;
        releaseWaiter.Complete(released: false);
        CloudMusicFavoriteCommandResult result = await request;

        Assert.Equal(
            CloudMusicFavoriteCommandResultKind.TriggerReleaseTimedOut,
            result.Kind);
        Assert.Empty(sender.Hotkeys);
    }

    [Fact]
    public async Task GamepadTriggerSkipsPhysicalKeyboardReleaseWait()
    {
        var stateReader = new SequenceStateReader(
            OverlayFavoriteVisualState.NotFavorite,
            OverlayFavoriteVisualState.Favorite);
        var sender = new RecordingShortcutSender();
        var releaseWaiter = new ControlledHotkeyReleaseWaiter();
        var service = CreateService(stateReader, sender, releaseWaiter: releaseWaiter);

        CloudMusicFavoriteCommandResult result =
            await service.FavoriteCurrentTrackAsync(
                CreateTrack(),
                "Ctrl+Alt+G",
                triggeringHotkey: null);

        Assert.Equal(CloudMusicFavoriteCommandResultKind.Added, result.Kind);
        Assert.Empty(releaseWaiter.Hotkeys);
        Assert.Single(sender.Hotkeys);
    }

    private static CloudMusicFavoriteCommandService CreateService(
        ICloudMusicFavoriteStateReader stateReader,
        IGlobalShortcutSender sender,
        int maxPolls = 2,
        IHotkeyReleaseWaiter? releaseWaiter = null)
    {
        return new CloudMusicFavoriteCommandService(
            stateReader,
            sender,
            dispatchDelay: TimeSpan.Zero,
            pollInterval: TimeSpan.Zero,
            maxPolls,
            releaseWaiter);
    }

    private static TrackInfo CreateTrack()
    {
        return new TrackInfo(
            "Test song",
            "Test artist",
            null,
            "cloudmusic.exe",
            TimeSpan.FromMinutes(3),
            true);
    }

    private sealed class SequenceStateReader(
        params OverlayFavoriteVisualState[] states) : ICloudMusicFavoriteStateReader
    {
        private readonly Queue<OverlayFavoriteVisualState> _states = new(states);
        private OverlayFavoriteVisualState _lastState =
            states.LastOrDefault(OverlayFavoriteVisualState.Unavailable);

        public List<bool> ForceRefreshValues { get; } = [];

        public bool IsSupportedSource(string? sourceAppId)
        {
            return OverlayFavoritePresentation.IsSupportedCloudMusicSource(sourceAppId);
        }

        public Task<OverlayFavoriteVisualState> ReadAsync(
            TrackInfo track,
            CancellationToken cancellationToken)
        {
            return ReadAsync(track, forceRefresh: false, cancellationToken);
        }

        public Task<OverlayFavoriteVisualState> ReadAsync(
            TrackInfo track,
            bool forceRefresh,
            CancellationToken cancellationToken = default)
        {
            ForceRefreshValues.Add(forceRefresh);
            if (_states.TryDequeue(out OverlayFavoriteVisualState state))
            {
                _lastState = state;
            }

            return Task.FromResult(_lastState);
        }
    }

    private sealed class RecordingShortcutSender(bool success = true) : IGlobalShortcutSender
    {
        public List<HotkeyDefinition> Hotkeys { get; } = [];

        public Task<bool> TrySendAsync(
            HotkeyDefinition hotkey,
            CancellationToken cancellationToken = default)
        {
            Hotkeys.Add(hotkey);
            return Task.FromResult(success);
        }
    }

    private sealed class ControlledHotkeyReleaseWaiter : IHotkeyReleaseWaiter
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<HotkeyDefinition> Hotkeys { get; } = [];

        public async Task<bool> WaitForReleaseAsync(
            HotkeyDefinition hotkey,
            CancellationToken cancellationToken = default)
        {
            Hotkeys.Add(hotkey);
            Started.TrySetResult();
            return await _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(bool released)
        {
            _completion.TrySetResult(released);
        }
    }
}
