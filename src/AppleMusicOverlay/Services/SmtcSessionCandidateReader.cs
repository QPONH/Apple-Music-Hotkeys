namespace AppleMusicOverlay.Services;

public sealed record SmtcSessionReadResult(
    string SourceAppUserModelId,
    string Title,
    string Artist,
    bool IsPlaying);

public static class SmtcSessionCandidateReader
{
    public static async Task<IReadOnlyList<MediaSessionCandidate>> ReadCandidatesAsync<TSession>(
        IReadOnlyList<TSession> sessions,
        TSession? currentSession,
        Func<TSession, int, CancellationToken, Task<SmtcSessionReadResult>> readSessionAsync,
        CancellationToken cancellationToken)
    {
        var candidates = new List<MediaSessionCandidate>(sessions.Count);

        for (int index = 0; index < sessions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TSession session = sessions[index];

            SmtcSessionReadResult result;
            try
            {
                result = await readSessionAsync(session, index, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"Skipped unreadable SMTC session at index {index}.");
#endif
                continue;
            }

            candidates.Add(new MediaSessionCandidate(
                result.SourceAppUserModelId,
                result.Title,
                result.Artist,
                result.IsPlaying,
                EqualityComparer<TSession>.Default.Equals(session, currentSession),
                index));
        }

        return candidates;
    }
}
