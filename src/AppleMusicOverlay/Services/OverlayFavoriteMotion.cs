namespace AppleMusicOverlay.Services;

public enum OverlayFavoriteMotionEasing
{
    EaseOut,
    EaseIn
}

public readonly record struct OverlayFavoriteMotionFrame(
    double Scale,
    double GlowOpacity,
    double InnerLightOpacity,
    double DepthOpacity,
    double OutlineOpacity,
    double FillOpacity,
    bool UseActiveFill,
    TimeSpan Duration,
    OverlayFavoriteMotionEasing Easing);

public static class OverlayFavoriteMotion
{
    public static IReadOnlyList<OverlayFavoriteMotionFrame> Frames { get; } =
    [
        new(
            Scale: 1.08,
            GlowOpacity: 0.24,
            InnerLightOpacity: 0.18,
            DepthOpacity: 0.12,
            OutlineOpacity: 0.36,
            FillOpacity: 0.68,
            UseActiveFill: true,
            Duration: TimeSpan.FromMilliseconds(90),
            Easing: OverlayFavoriteMotionEasing.EaseOut),
        new(
            Scale: 1.16,
            GlowOpacity: 0.7,
            InnerLightOpacity: 0.48,
            DepthOpacity: 0.34,
            OutlineOpacity: 0,
            FillOpacity: 0.98,
            UseActiveFill: true,
            Duration: TimeSpan.FromMilliseconds(100),
            Easing: OverlayFavoriteMotionEasing.EaseOut),
        new(
            Scale: 0.95,
            GlowOpacity: 0.2,
            InnerLightOpacity: 0.14,
            DepthOpacity: 0.18,
            OutlineOpacity: 0,
            FillOpacity: 0.96,
            UseActiveFill: true,
            Duration: TimeSpan.FromMilliseconds(55),
            Easing: OverlayFavoriteMotionEasing.EaseIn),
        new(
            Scale: 1,
            GlowOpacity: 0,
            InnerLightOpacity: 0,
            DepthOpacity: 0,
            OutlineOpacity: 0,
            FillOpacity: 0.96,
            UseActiveFill: false,
            Duration: TimeSpan.FromMilliseconds(115),
            Easing: OverlayFavoriteMotionEasing.EaseOut)
    ];
}
