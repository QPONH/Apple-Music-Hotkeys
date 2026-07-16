namespace AppleMusicOverlay.Services;

public enum OverlayTrackChangeDisplayDecision
{
    Show,
    UpdateVisible,
    CacheOnly
}

public static class OverlayTrackChangeDisplayPolicy
{
    public static OverlayTrackChangeDisplayDecision Decide(
        bool showOnTrackChange,
        bool pauseOverlay,
        bool isOverlayVisible)
    {
        if (showOnTrackChange)
        {
            return OverlayTrackChangeDisplayDecision.Show;
        }

        if (isOverlayVisible)
        {
            return OverlayTrackChangeDisplayDecision.UpdateVisible;
        }

        return pauseOverlay
            ? OverlayTrackChangeDisplayDecision.Show
            : OverlayTrackChangeDisplayDecision.CacheOnly;
    }
}
