using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class OverlayWindowMotionTests
{
    [Fact]
    public void EnterUsesSingleDirectionScaleAndSettlesAtVisibleState()
    {
        OverlayWindowMotionPlan plan = OverlayWindowMotion.CreateEnter(OverlayWindowMotion.Hidden);

        Assert.Equal(TimeSpan.FromMilliseconds(320), plan.Duration);
        Assert.Single(plan.Scale.KeyFrames);
        Assert.Equal(1, plan.Scale.KeyFrames[0].Value, precision: 3);
        Assert.Equal(TimeSpan.FromMilliseconds(320), plan.Scale.KeyFrames[0].At);
        Assert.Single(plan.OffsetY.KeyFrames);
        Assert.Equal(0, plan.OffsetY.KeyFrames[0].Value);

        double previousScale = OverlayWindowMotion.Hidden.Scale;
        for (int milliseconds = 20; milliseconds <= 320; milliseconds += 20)
        {
            double scale = OverlayWindowMotion.Evaluate(
                plan,
                TimeSpan.FromMilliseconds(milliseconds)).Scale;
            Assert.InRange(scale, previousScale, 1);
            previousScale = scale;
        }

        OverlayWindowMotionState settled = OverlayWindowMotion.Evaluate(plan, plan.Duration);
        Assert.Equal(OverlayWindowMotion.Visible, settled);
    }

    [Fact]
    public void EnterOpacityCompletesBeforeScaleSettles()
    {
        OverlayWindowMotionPlan plan = OverlayWindowMotion.CreateEnter(OverlayWindowMotion.Hidden);

        OverlayWindowMotionState state = OverlayWindowMotion.Evaluate(
            plan,
            TimeSpan.FromMilliseconds(170));

        Assert.Equal(1, state.Opacity, precision: 3);
        Assert.InRange(state.Scale, OverlayWindowMotion.Hidden.Scale, 1);
        Assert.NotEqual(1, state.Scale);
    }

    [Fact]
    public void ExitUsesShortRestrainedMotion()
    {
        OverlayWindowMotionPlan plan = OverlayWindowMotion.CreateExit(OverlayWindowMotion.Visible);

        Assert.Equal(TimeSpan.FromMilliseconds(180), plan.Duration);

        OverlayWindowMotionState hidden = OverlayWindowMotion.Evaluate(plan, plan.Duration);
        Assert.Equal(0, hidden.Opacity, precision: 3);
        Assert.Equal(1, hidden.Scale, precision: 3);
        Assert.Equal(-4, hidden.OffsetY, precision: 3);
    }

    [Fact]
    public void ExitDoesNotIntroduceASecondScaleGesture()
    {
        var current = new OverlayWindowMotionState(0.8, 0.987, 2);
        OverlayWindowMotionPlan plan = OverlayWindowMotion.CreateExit(current);

        Assert.Equal(current.Scale, OverlayWindowMotion.Evaluate(plan, TimeSpan.Zero).Scale);
        Assert.Equal(current.Scale, OverlayWindowMotion.Evaluate(plan, plan.Duration).Scale);
    }

    [Fact]
    public void ReversingDirectionStartsAtCurrentVisualState()
    {
        OverlayWindowMotionPlan entering = OverlayWindowMotion.CreateEnter(OverlayWindowMotion.Hidden);
        OverlayWindowMotionState interruptedEnter = OverlayWindowMotion.Evaluate(
            entering,
            TimeSpan.FromMilliseconds(145));

        OverlayWindowMotionPlan exiting = OverlayWindowMotion.CreateExit(interruptedEnter);
        Assert.Equal(interruptedEnter, OverlayWindowMotion.Evaluate(exiting, TimeSpan.Zero));

        OverlayWindowMotionState interruptedExit = OverlayWindowMotion.Evaluate(
            exiting,
            TimeSpan.FromMilliseconds(70));
        OverlayWindowMotionPlan resumedEnter = OverlayWindowMotion.CreateEnter(interruptedExit);

        Assert.Equal(interruptedExit, OverlayWindowMotion.Evaluate(resumedEnter, TimeSpan.Zero));
    }

    [Fact]
    public void EvaluationClampsOutsidePlanDuration()
    {
        OverlayWindowMotionPlan plan = OverlayWindowMotion.CreateEnter(OverlayWindowMotion.Hidden);

        Assert.Equal(
            OverlayWindowMotion.Hidden,
            OverlayWindowMotion.Evaluate(plan, TimeSpan.FromMilliseconds(-20)));
        Assert.Equal(
            OverlayWindowMotion.Visible,
            OverlayWindowMotion.Evaluate(plan, TimeSpan.FromSeconds(2)));
    }
}
