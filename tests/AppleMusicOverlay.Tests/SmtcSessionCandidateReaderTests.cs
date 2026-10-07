using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class SmtcSessionCandidateReaderTests
{
    [Fact]
    public async Task ReadCandidatesAsyncSkipsFailingSessionAndReturnsHealthySession()
    {
        string[] sessions = ["bad", "good"];

        IReadOnlyList<MediaSessionCandidate> candidates =
            await SmtcSessionCandidateReader.ReadCandidatesAsync(
                sessions,
                currentSession: "good",
                ReadSessionAsync,
                CancellationToken.None);

        MediaSessionCandidate candidate = Assert.Single(candidates);
        Assert.Equal("good", candidate.SourceAppUserModelId);
        Assert.Equal("Song", candidate.Title);
        Assert.Equal("Artist", candidate.Artist);
        Assert.True(candidate.IsPlaying);
        Assert.True(candidate.IsCurrent);
        Assert.Equal(1, candidate.Index);
    }

    [Fact]
    public async Task ReadCandidatesAsyncReturnsEmptyWhenAllSessionsFail()
    {
        string[] sessions = ["bad-1", "bad-2"];

        IReadOnlyList<MediaSessionCandidate> candidates =
            await SmtcSessionCandidateReader.ReadCandidatesAsync(
                sessions,
                currentSession: "bad-1",
                (_, _, _) => throw new InvalidOperationException("stale session"),
                CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task ReadCandidatesAsyncPreservesIndexesForExistingSelectionRules()
    {
        string[] sessions = ["chrome", "edge"];

        IReadOnlyList<MediaSessionCandidate> candidates =
            await SmtcSessionCandidateReader.ReadCandidatesAsync(
                sessions,
                currentSession: "chrome",
                ReadSessionAsync,
                CancellationToken.None);

        MediaSessionCandidate? selected = MediaSessionSelector.SelectBest(candidates);

        Assert.Equal("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!MSEdge", selected?.SourceAppUserModelId);
        Assert.Equal(1, selected?.Index);
    }

    private static Task<SmtcSessionReadResult> ReadSessionAsync(string session, int index, CancellationToken cancellationToken)
    {
        if (session == "bad")
        {
            throw new InvalidOperationException("stale session");
        }

        if (session == "chrome")
        {
            return Task.FromResult(new SmtcSessionReadResult(
                "Chrome",
                "A YouTube Video",
                "YouTube",
                IsPlaying: true));
        }

        if (session == "good")
        {
            return Task.FromResult(new SmtcSessionReadResult(
                "good",
                "Song",
                "Artist",
                IsPlaying: true));
        }

        return Task.FromResult(new SmtcSessionReadResult(
            "Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!MSEdge",
            "Song",
            "Artist",
            IsPlaying: true));
    }
}
