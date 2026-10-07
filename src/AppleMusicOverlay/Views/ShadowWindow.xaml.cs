using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Views;

public partial class ShadowWindow : Window
{
    public const double ShadowPadding = 56;

    private static readonly Duration ShadowStateDuration = TimeSpan.FromMilliseconds(180);

    public ShadowWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        SourceInitialized += (_, _) => WindowStyleService.ApplyShadowStyles(this);
        ApplyActiveState(false);
    }

    public void SyncWith(Window owner)
    {
        if (!owner.IsVisible || owner.WindowState is WindowState.Minimized or WindowState.Maximized)
        {
            Hide();
            return;
        }

        double ownerWidth = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
        double ownerHeight = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;
        Width = ownerWidth + ShadowPadding * 2;
        Height = ownerHeight + ShadowPadding * 2;
        Left = owner.Left - ShadowPadding;
        Top = owner.Top - ShadowPadding;

        if (!IsVisible)
        {
            Show();
        }

        WindowStyleService.ApplyShadowStyles(this);
        WindowStyleService.PlaceShadowBehind(this, owner);
    }

    public void ApplyActiveState(bool isActive)
    {
        AnimateShadow(AmbientShadow, isActive ? 72 : 62, isActive ? 0.42 : 0.28);
        AnimateShadow(LiftShadow, isActive ? 52 : 42, isActive ? 0.28 : 0.17);
    }

    private static void AnimateShadow(DropShadowEffect shadow, double blurRadius, double opacity)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        shadow.BeginAnimation(
            DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(blurRadius, ShadowStateDuration)
            {
                EasingFunction = easing
            });
        shadow.BeginAnimation(
            DropShadowEffect.OpacityProperty,
            new DoubleAnimation(opacity, ShadowStateDuration)
            {
                EasingFunction = easing
            });
    }
}
