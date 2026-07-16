using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class OverlayTrackChangeDisplayPolicyTests
{
    [Fact]
    public void EnabledSettingPreservesAutomaticShowBehavior()
    {
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            showOnTrackChange: true,
            pauseOverlay: false,
            isOverlayVisible: false);

        Assert.Equal(OverlayTrackChangeDisplayDecision.Show, decision);
    }

    [Fact]
    public void ManualOnlyModeCachesTrackWhenOverlayIsHidden()
    {
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            showOnTrackChange: false,
            pauseOverlay: false,
            isOverlayVisible: false);

        Assert.Equal(OverlayTrackChangeDisplayDecision.CacheOnly, decision);
    }

    [Fact]
    public void ManualOnlyModeUpdatesAnAlreadyVisibleOverlay()
    {
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            showOnTrackChange: false,
            pauseOverlay: false,
            isOverlayVisible: true);

        Assert.Equal(OverlayTrackChangeDisplayDecision.UpdateVisible, decision);
    }

    [Fact]
    public void ManualOnlyModeShowsPersistentOverlayWhenItIsNotVisibleYet()
    {
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            showOnTrackChange: false,
            pauseOverlay: true,
            isOverlayVisible: false);

        Assert.Equal(OverlayTrackChangeDisplayDecision.Show, decision);
    }

    [Fact]
    public void ManualOnlyModeUpdatesVisiblePersistentOverlayWithoutRestartingIt()
    {
        OverlayTrackChangeDisplayDecision decision = OverlayTrackChangeDisplayPolicy.Decide(
            showOnTrackChange: false,
            pauseOverlay: true,
            isOverlayVisible: true);

        Assert.Equal(OverlayTrackChangeDisplayDecision.UpdateVisible, decision);
    }
}
