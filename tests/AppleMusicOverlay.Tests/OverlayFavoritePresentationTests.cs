using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class OverlayFavoritePresentationTests
{
    private static readonly TrackInfo AppleMusicTrack = new(
        "A Very Long Song Title",
        "Example Artist",
        null,
        "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App",
        TimeSpan.FromMinutes(3),
        true);

    [Fact]
    public void ResolveUsesAccurateFavoriteStateForMatchingAppleMusicTrack()
    {
        var status = new AppleMusicFavoriteStatus(
            FavoriteButtonState.AlreadyFavorite,
            "A Very Long Song Title - Example Artist");

        OverlayFavoriteVisualState result = OverlayFavoritePresentation.Resolve(AppleMusicTrack, status);

        Assert.Equal(OverlayFavoriteVisualState.Favorite, result);
    }

    [Fact]
    public void ResolveUsesAccurateNotFavoriteStateForMatchingAppleMusicTrack()
    {
        var status = new AppleMusicFavoriteStatus(
            FavoriteButtonState.NotFavorite,
            "A Very Long Song Title - Example Artist");

        OverlayFavoriteVisualState result = OverlayFavoritePresentation.Resolve(AppleMusicTrack, status);

        Assert.Equal(OverlayFavoriteVisualState.NotFavorite, result);
    }

    [Fact]
    public void ResolveFailsClosedWhenAutomationIdentityDoesNotMatchTrack()
    {
        var status = new AppleMusicFavoriteStatus(
            FavoriteButtonState.AlreadyFavorite,
            "Different Song - Different Artist");

        OverlayFavoriteVisualState result = OverlayFavoritePresentation.Resolve(AppleMusicTrack, status);

        Assert.Equal(OverlayFavoriteVisualState.Unavailable, result);
    }

    [Theory]
    [InlineData("Spotify.exe")]
    [InlineData("msedge.exe")]
    [InlineData("chrome.exe")]
    public void ResolveKeepsLegacyPresentationForUnsupportedSources(string sourceAppId)
    {
        TrackInfo track = AppleMusicTrack with { SourceAppId = sourceAppId };
        var status = new AppleMusicFavoriteStatus(
            FavoriteButtonState.AlreadyFavorite,
            "A Very Long Song Title - Example Artist");

        OverlayFavoriteVisualState result = OverlayFavoritePresentation.Resolve(track, status);

        Assert.Equal(OverlayFavoriteVisualState.Unavailable, result);
    }

    [Fact]
    public void FavoriteLayoutReservesFixedStarSlotInsideCoverWidth()
    {
        OverlayTrackContentLayout layout = OverlayFavoritePresentation.CreateLayout(
            OverlayFavoriteVisualState.Favorite,
            showTitle: true,
            showArtist: true);

        Assert.True(layout.ShowFavoriteStar);
        Assert.Equal(40, layout.TextLeft);
        Assert.Equal(140, layout.TextWidth);
        Assert.Equal(194, layout.StarLeft);
        Assert.Equal(22, layout.StarSize);
        Assert.True(layout.TextLeft + layout.TextWidth <= layout.StarLeft - 12);
        Assert.Equal(216, layout.StarLeft + layout.StarSize);
    }

    [Fact]
    public void UnavailableLayoutMatchesCurrentCenteredPresentation()
    {
        OverlayTrackContentLayout layout = OverlayFavoritePresentation.CreateLayout(
            OverlayFavoriteVisualState.Unavailable,
            showTitle: true,
            showArtist: true);

        Assert.False(layout.ShowFavoriteStar);
        Assert.Equal(4, layout.TextLeft);
        Assert.Equal(248, layout.TextWidth);
        Assert.True(layout.CenterText);
        Assert.Equal(228, layout.TitleTop);
        Assert.Equal(249, layout.ArtistTop);
    }

    [Theory]
    [InlineData(OverlayTrackFontIds.Default, false, 0)]
    [InlineData(OverlayTrackFontIds.SpotifyMix, false, 0)]
    [InlineData(OverlayTrackFontIds.SfPro, true, 0)]
    [InlineData(OverlayTrackFontIds.SfPro, false, 2)]
    public void ArtistOpticalOffsetOnlyAppliesToLeftAlignedSfProLayout(
        string fontId,
        bool centerText,
        double expectedOffset)
    {
        double offset = OverlayFavoritePresentation.GetArtistOpticalOffset(fontId, centerText);

        Assert.Equal(expectedOffset, offset);
    }

    [Fact]
    public void FavoriteMotionCrossFadesPullsCompressesAndSettlesContinuously()
    {
        IReadOnlyList<OverlayFavoriteMotionFrame> frames = OverlayFavoriteMotion.Frames;

        Assert.Equal(4, frames.Count);
        Assert.Equal(1.08, frames[0].Scale);
        Assert.InRange(frames[0].OutlineOpacity, 0.2, 0.5);
        Assert.InRange(frames[0].FillOpacity, 0.5, 0.8);
        Assert.Equal(TimeSpan.FromMilliseconds(90), frames[0].Duration);
        Assert.Equal(1.16, frames[1].Scale);
        Assert.Equal(0, frames[1].OutlineOpacity);
        Assert.True(frames[1].GlowOpacity > frames[0].GlowOpacity);
        Assert.Equal(TimeSpan.FromMilliseconds(100), frames[1].Duration);
        Assert.Equal(0.95, frames[2].Scale);
        Assert.Equal(OverlayFavoriteMotionEasing.EaseIn, frames[2].Easing);
        Assert.Equal(TimeSpan.FromMilliseconds(55), frames[2].Duration);
        Assert.Equal(1, frames[3].Scale);
        Assert.Equal(0, frames[3].GlowOpacity);
        Assert.Equal(0, frames[3].DepthOpacity);
        Assert.False(frames[3].UseActiveFill);
        Assert.Equal(TimeSpan.FromMilliseconds(115), frames[3].Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(360), frames.Aggregate(TimeSpan.Zero, (total, frame) => total + frame.Duration));
    }

    [Fact]
    public async Task StatusCoordinatorDiscardsResultFromSupersededTrack()
    {
        var firstRead = new TaskCompletionSource<AppleMusicFavoriteStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource<AppleMusicFavoriteStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int readCount = 0;
        using var coordinator = new OverlayFavoriteStatusCoordinator(_ =>
        {
            readCount++;
            return readCount == 1 ? firstRead.Task : secondRead.Task;
        });
        TrackInfo secondTrack = AppleMusicTrack with { Title = "Second Song" };

        Task<OverlayFavoriteResolution?> first = coordinator.ResolveAsync(AppleMusicTrack);
        Task<OverlayFavoriteResolution?> second = coordinator.ResolveAsync(secondTrack);
        secondRead.SetResult(new AppleMusicFavoriteStatus(
            FavoriteButtonState.AlreadyFavorite,
            "Second Song - Example Artist"));
        firstRead.SetResult(new AppleMusicFavoriteStatus(
            FavoriteButtonState.NotFavorite,
            "A Very Long Song Title - Example Artist"));

        Assert.Null(await first);
        OverlayFavoriteResolution? current = await second;
        Assert.NotNull(current);
        Assert.Equal(secondTrack, current.Track);
        Assert.Equal(OverlayFavoriteVisualState.Favorite, current.State);
    }

    [Fact]
    public async Task StatusCoordinatorSkipsAutomationForUnsupportedSource()
    {
        int readCount = 0;
        using var coordinator = new OverlayFavoriteStatusCoordinator(_ =>
        {
            readCount++;
            return Task.FromResult(new AppleMusicFavoriteStatus(FavoriteButtonState.AlreadyFavorite, "unused"));
        });
        TrackInfo spotifyTrack = AppleMusicTrack with { SourceAppId = "Spotify.exe" };

        OverlayFavoriteResolution? result = await coordinator.ResolveAsync(spotifyTrack);

        Assert.NotNull(result);
        Assert.Equal(OverlayFavoriteVisualState.Unavailable, result.State);
        Assert.Equal(0, readCount);
    }

    [Fact]
    public async Task StatusCoordinatorFallsBackToLegacyPresentationWhenReadTimesOut()
    {
        using var coordinator = new OverlayFavoriteStatusCoordinator(
            async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AppleMusicFavoriteStatus(FavoriteButtonState.AlreadyFavorite, "unreachable");
            },
            TimeSpan.FromMilliseconds(10));

        OverlayFavoriteResolution? result = await coordinator.ResolveAsync(AppleMusicTrack);

        Assert.NotNull(result);
        Assert.Equal(OverlayFavoriteVisualState.Unavailable, result.State);
    }

    [Fact]
    public async Task StatusCoordinatorRetriesWhenAppleMusicStillReportsPreviousTrack()
    {
        var statuses = new Queue<AppleMusicFavoriteStatus>(
        [
            new AppleMusicFavoriteStatus(FavoriteButtonState.AlreadyFavorite, "Previous Song - Previous Artist"),
            new AppleMusicFavoriteStatus(
                FavoriteButtonState.NotFavorite,
                "A Very Long Song Title - Example Artist")
        ]);
        int readCount = 0;
        using var coordinator = new OverlayFavoriteStatusCoordinator(
            _ =>
            {
                readCount++;
                return Task.FromResult(statuses.Dequeue());
            },
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            maxAttempts: 3);

        OverlayFavoriteResolution? result = await coordinator.ResolveAsync(AppleMusicTrack);

        Assert.NotNull(result);
        Assert.Equal(OverlayFavoriteVisualState.NotFavorite, result.State);
        Assert.Equal(2, readCount);
    }
}
