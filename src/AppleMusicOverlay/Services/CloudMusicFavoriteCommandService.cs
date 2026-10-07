using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public enum CloudMusicFavoriteCommandResultKind
{
    Added = 0,
    Removed = 1,
    InvalidHotkey = 2,
    StateUnavailable = 3,
    DispatchFailed = 4,
    ConfirmationTimedOut = 5,
    TrackChanged = 6,
    TriggerReleaseTimedOut = 7
}

public sealed record CloudMusicFavoriteCommandResult(
    CloudMusicFavoriteCommandResultKind Kind);

public sealed class CloudMusicFavoriteCommandService
{
    private static readonly TimeSpan DefaultDispatchDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(180);
    private const int DefaultMaxPolls = 32;

    private readonly ICloudMusicFavoriteStateReader _stateReader;
    private readonly IGlobalShortcutSender _shortcutSender;
    private readonly IHotkeyReleaseWaiter _releaseWaiter;
    private readonly TimeSpan _dispatchDelay;
    private readonly TimeSpan _pollInterval;
    private readonly int _maxPolls;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    public CloudMusicFavoriteCommandService(
        ICloudMusicFavoriteStateReader stateReader,
        IGlobalShortcutSender? shortcutSender = null,
        TimeSpan? dispatchDelay = null,
        TimeSpan? pollInterval = null,
        int maxPolls = DefaultMaxPolls,
        IHotkeyReleaseWaiter? releaseWaiter = null)
    {
        _stateReader = stateReader;
        _shortcutSender = shortcutSender ?? new SendInputGlobalShortcutSender();
        _releaseWaiter = releaseWaiter ?? new AsyncKeyStateHotkeyReleaseWaiter();
        _dispatchDelay = dispatchDelay ?? DefaultDispatchDelay;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _maxPolls = Math.Max(1, maxPolls);
    }

    public async Task<CloudMusicFavoriteCommandResult> FavoriteCurrentTrackAsync(
        TrackInfo track,
        string nativeFavoriteHotkey,
        Func<bool>? isStillCurrent = null,
        string? triggeringHotkey = null,
        CancellationToken cancellationToken = default)
    {
        if (!HotkeyParser.TryParse(nativeFavoriteHotkey, out HotkeyDefinition hotkey))
        {
            return new CloudMusicFavoriteCommandResult(
                CloudMusicFavoriteCommandResultKind.InvalidHotkey);
        }

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            OverlayFavoriteVisualState initialState = await _stateReader.ReadAsync(
                track,
                forceRefresh: true,
                cancellationToken);
            if (initialState is not OverlayFavoriteVisualState.Favorite and
                not OverlayFavoriteVisualState.NotFavorite)
            {
                return new CloudMusicFavoriteCommandResult(
                    CloudMusicFavoriteCommandResultKind.StateUnavailable);
            }

            OverlayFavoriteVisualState targetState =
                initialState == OverlayFavoriteVisualState.Favorite
                    ? OverlayFavoriteVisualState.NotFavorite
                    : OverlayFavoriteVisualState.Favorite;

            if (!string.IsNullOrWhiteSpace(triggeringHotkey) &&
                HotkeyParser.TryParse(triggeringHotkey, out HotkeyDefinition trigger) &&
                !await _releaseWaiter.WaitForReleaseAsync(trigger, cancellationToken))
            {
                return new CloudMusicFavoriteCommandResult(
                    CloudMusicFavoriteCommandResultKind.TriggerReleaseTimedOut);
            }

            // Leave a short gap after the physical trigger is released before
            // pressing CloudMusic's independent shortcut.
            if (_dispatchDelay > TimeSpan.Zero)
            {
                await Task.Delay(_dispatchDelay, cancellationToken);
            }

            if (isStillCurrent != null && !isStillCurrent())
            {
                return new CloudMusicFavoriteCommandResult(
                    CloudMusicFavoriteCommandResultKind.TrackChanged);
            }

            if (!await _shortcutSender.TrySendAsync(hotkey, cancellationToken))
            {
                return new CloudMusicFavoriteCommandResult(
                    CloudMusicFavoriteCommandResultKind.DispatchFailed);
            }

            for (int attempt = 0; attempt < _maxPolls; attempt++)
            {
                await Task.Delay(_pollInterval, cancellationToken);
                OverlayFavoriteVisualState confirmedState = await _stateReader.ReadAsync(
                    track,
                    forceRefresh: true,
                    cancellationToken);
                if (confirmedState == targetState)
                {
                    return new CloudMusicFavoriteCommandResult(
                        targetState == OverlayFavoriteVisualState.Favorite
                            ? CloudMusicFavoriteCommandResultKind.Added
                            : CloudMusicFavoriteCommandResultKind.Removed);
                }
            }

            return new CloudMusicFavoriteCommandResult(
                CloudMusicFavoriteCommandResultKind.ConfirmationTimedOut);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new CloudMusicFavoriteCommandResult(
                CloudMusicFavoriteCommandResultKind.StateUnavailable);
        }
        finally
        {
            _commandGate.Release();
        }
    }
}
