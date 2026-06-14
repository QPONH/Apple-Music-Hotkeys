using AppleMusicOverlay.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AppleMusicOverlay.Services;

public sealed class SmtcMediaSessionService : IMediaSessionService
{
    public async Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
    {
        GlobalSystemMediaTransportControlsSession? session = await GetSessionAsync(cancellationToken);
        if (session == null)
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties properties =
            await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();

        string title = NormalizeText(properties.Title, "Unknown Track");
        string artist = NormalizeText(properties.Artist, "Unknown Artist");
        byte[]? coverBytes = await ReadCoverBytesAsync(properties.Thumbnail, cancellationToken);

        return new TrackInfo(
            title,
            artist,
            coverBytes,
            NormalizeText(session.SourceAppUserModelId, "Unknown Source"),
            timeline.EndTime,
            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
    }

    public async Task PreviousAsync(CancellationToken cancellationToken = default)
    {
        GlobalSystemMediaTransportControlsSession? session = await GetSessionAsync(cancellationToken);
        if (session != null)
        {
            await session.TrySkipPreviousAsync().AsTask(cancellationToken);
        }
    }

    public async Task NextAsync(CancellationToken cancellationToken = default)
    {
        GlobalSystemMediaTransportControlsSession? session = await GetSessionAsync(cancellationToken);
        if (session != null)
        {
            await session.TrySkipNextAsync().AsTask(cancellationToken);
        }
    }

    public async Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        GlobalSystemMediaTransportControlsSession? session = await GetSessionAsync(cancellationToken);
        if (session != null)
        {
            await session.TryTogglePlayPauseAsync().AsTask(cancellationToken);
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSession?> GetSessionAsync(CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSessionManager manager =
            await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
        if (sessions.Count == 0)
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSession? current = manager.GetCurrentSession();
        var candidates = new List<MediaSessionCandidate>(sessions.Count);
        var sessionByIndex = new Dictionary<int, GlobalSystemMediaTransportControlsSession>();

        for (int index = 0; index < sessions.Count; index++)
        {
            GlobalSystemMediaTransportControlsSession session = sessions[index];
            GlobalSystemMediaTransportControlsSessionMediaProperties properties =
                await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();

            var candidate = new MediaSessionCandidate(
                NormalizeText(session.SourceAppUserModelId, "Unknown Source"),
                NormalizeText(properties.Title, "Unknown Track"),
                NormalizeText(properties.Artist, "Unknown Artist"),
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                ReferenceEquals(session, current),
                index);

            candidates.Add(candidate);
            sessionByIndex[index] = session;
        }

        MediaSessionCandidate? selected = MediaSessionSelector.SelectBest(candidates);
        if (selected != null && sessionByIndex.TryGetValue(selected.Index, out GlobalSystemMediaTransportControlsSession? selectedSession))
        {
            return selectedSession;
        }

        return current ?? sessions.FirstOrDefault();
    }

    private static async Task<byte[]?> ReadCoverBytesAsync(IRandomAccessStreamReference? thumbnail, CancellationToken cancellationToken)
    {
        if (thumbnail == null)
        {
            return null;
        }

        using IRandomAccessStreamWithContentType stream = await thumbnail.OpenReadAsync().AsTask(cancellationToken);
        if (stream.Size == 0 || stream.Size > int.MaxValue)
        {
            return null;
        }

        var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
        IBuffer readBuffer = await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None).AsTask(cancellationToken);
        byte[] bytes = new byte[readBuffer.Length];
        DataReader.FromBuffer(readBuffer).ReadBytes(bytes);
        return bytes;
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
