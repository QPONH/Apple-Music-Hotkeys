namespace AppleMusicOverlay.Services;

public enum OverlayWindowMotionCurve
{
    EaseOutCubic,
    SmoothStep,
    EaseInCubic
}

public sealed record OverlayWindowMotionState(
    double Opacity,
    double Scale,
    double OffsetY);

public sealed record OverlayWindowMotionKeyFrame(
    TimeSpan At,
    double Value,
    OverlayWindowMotionCurve Curve);

public sealed record OverlayWindowScalarMotion(
    double Start,
    IReadOnlyList<OverlayWindowMotionKeyFrame> KeyFrames);

public sealed record OverlayWindowMotionPlan(
    OverlayWindowScalarMotion Opacity,
    OverlayWindowScalarMotion Scale,
    OverlayWindowScalarMotion OffsetY,
    TimeSpan Duration);

public static class OverlayWindowMotion
{
    public static readonly OverlayWindowMotionState Hidden = new(0, 0.968, 10);
    public static readonly OverlayWindowMotionState Visible = new(1, 1, 0);

    public static OverlayWindowMotionPlan CreateEnter(OverlayWindowMotionState current)
    {
        return new OverlayWindowMotionPlan(
            new OverlayWindowScalarMotion(
                current.Opacity,
                [new(TimeSpan.FromMilliseconds(160), 1, OverlayWindowMotionCurve.EaseOutCubic)]),
            new OverlayWindowScalarMotion(
                current.Scale,
                [new(TimeSpan.FromMilliseconds(320), 1, OverlayWindowMotionCurve.EaseOutCubic)]),
            new OverlayWindowScalarMotion(
                current.OffsetY,
                [new(TimeSpan.FromMilliseconds(320), 0, OverlayWindowMotionCurve.EaseOutCubic)]),
            TimeSpan.FromMilliseconds(320));
    }

    public static OverlayWindowMotionPlan CreateExit(OverlayWindowMotionState current)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(180);
        return new OverlayWindowMotionPlan(
            new OverlayWindowScalarMotion(
                current.Opacity,
                [new(duration, 0, OverlayWindowMotionCurve.SmoothStep)]),
            new OverlayWindowScalarMotion(
                current.Scale,
                [new(duration, current.Scale, OverlayWindowMotionCurve.SmoothStep)]),
            new OverlayWindowScalarMotion(
                current.OffsetY,
                [new(duration, -4, OverlayWindowMotionCurve.SmoothStep)]),
            duration);
    }

    public static OverlayWindowMotionState Evaluate(
        OverlayWindowMotionPlan plan,
        TimeSpan elapsed)
    {
        TimeSpan clamped = elapsed < TimeSpan.Zero
            ? TimeSpan.Zero
            : elapsed > plan.Duration ? plan.Duration : elapsed;

        return new OverlayWindowMotionState(
            Evaluate(plan.Opacity, clamped),
            Evaluate(plan.Scale, clamped),
            Evaluate(plan.OffsetY, clamped));
    }

    private static double Evaluate(OverlayWindowScalarMotion motion, TimeSpan elapsed)
    {
        TimeSpan previousTime = TimeSpan.Zero;
        double previousValue = motion.Start;
        foreach (OverlayWindowMotionKeyFrame keyFrame in motion.KeyFrames)
        {
            if (elapsed <= keyFrame.At)
            {
                double segmentMilliseconds = (keyFrame.At - previousTime).TotalMilliseconds;
                double progress = segmentMilliseconds <= 0
                    ? 1
                    : Math.Clamp(
                        (elapsed - previousTime).TotalMilliseconds / segmentMilliseconds,
                        0,
                        1);
                double eased = ApplyCurve(progress, keyFrame.Curve);
                return previousValue + ((keyFrame.Value - previousValue) * eased);
            }

            previousTime = keyFrame.At;
            previousValue = keyFrame.Value;
        }

        return previousValue;
    }

    private static double ApplyCurve(double progress, OverlayWindowMotionCurve curve)
    {
        return curve switch
        {
            OverlayWindowMotionCurve.EaseOutCubic => 1 - Math.Pow(1 - progress, 3),
            OverlayWindowMotionCurve.SmoothStep => (3 * progress * progress) -
                                                   (2 * progress * progress * progress),
            OverlayWindowMotionCurve.EaseInCubic => progress * progress * progress,
            _ => progress
        };
    }
}
