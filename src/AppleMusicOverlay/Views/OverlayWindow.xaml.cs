using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Views;

public partial class OverlayWindow : Window
{
    private const double BaseWidth = 256;
    private const double BaseHeight = 308;
    private const double MaxOverlayScale = 1.8;
    private const double VisualCenterX = 128;
    private const double VisualCenterY = 158;
    private const double MaxVisualHorizontalExtent = VisualCenterX;
    private const double MaxVisualVerticalExtent = VisualCenterY;
    private const double WindowWidth = MaxVisualHorizontalExtent * 2 * MaxOverlayScale;
    private const double WindowHeight = MaxVisualVerticalExtent * 2 * MaxOverlayScale;
    private const double CoverSize = 176;
    private const double ShadowCasterInset = 2;
    private const double ShadowCasterSize = CoverSize - (ShadowCasterInset * 2);
    private const double MaxAmbientShadowBlur = 32;
    private const double MaxAmbientShadowDepth = 0;
    private const double MaxAmbientShadowOpacity = 0.19;
    private const double MaxKeyShadowBlur = 36;
    private const double MaxKeyShadowDepth = 8;
    private const double MaxKeyShadowOpacity = 0.22;

    private static readonly Duration EnterDuration = TimeSpan.FromMilliseconds(230);
    private static readonly Duration ExitDuration = TimeSpan.FromMilliseconds(180);
    private static readonly Duration ContentFadeOutDuration = TimeSpan.FromMilliseconds(70);
    private static readonly Duration ContentFadeInDuration = TimeSpan.FromMilliseconds(130);

    private CancellationTokenSource? _hideCts;
    private OverlaySettings _settings = new();
    private bool _hasCover;
    private bool _hasPositionedWindow;

    public OverlayWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        SourceInitialized += (_, _) => WindowStyleService.ApplyOverlayStyles(this);
    }

    public void ApplySettings(OverlaySettings settings)
    {
        _settings = OverlaySettingsNormalizer.Normalize(settings);
        double scale = _settings.ScalePercent / 100.0;
        Point visualCenter = GetOrCreateVisualCenter(scale);
        Width = WindowWidth;
        Height = WindowHeight;
        Left = visualCenter.X - (WindowWidth / 2);
        Top = visualCenter.Y - (WindowHeight / 2);
        TitleText.Visibility = _settings.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
        ArtistText.Visibility = _settings.ShowArtist ? Visibility.Visible : Visibility.Collapsed;
        ApplyScaleTransform(scale);
        ApplyCoverShadowSettings();
    }

    public Task ShowTrackAsync(TrackInfo track)
    {
        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        SetTrackContent(track);
        VisualGroup.Opacity = 1;
        Show();
        Visibility = Visibility.Visible;
        WindowStyleService.ApplyOverlayStyles(this);
        BeginEnterAnimation();

        if (!_settings.PauseOverlay)
        {
            CancellationToken token = _hideCts.Token;
            _ = HideAfterDelayAsync(token);
        }

        return Task.CompletedTask;
    }

    public void UpdateTrack(TrackInfo track)
    {
        if (IsVisible && Visibility == Visibility.Visible && OverlayRoot.Opacity > 0.6)
        {
            BeginContentSwapAnimation(track);
            return;
        }

        SetTrackContent(track);
    }

    private void SetTrackContent(TrackInfo track)
    {
        TitleText.Text = track.Title;
        ArtistText.Text = track.Artist;
        BitmapSource? cover = CreateCover(track);
        CoverImage.Source = cover;
        _hasCover = cover != null;
        CoverImage.Opacity = _hasCover ? 1 : 0;
        ApplyCoverShadowSettings();
    }

    private void BeginContentSwapAnimation(TrackInfo track)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeOut = new DoubleAnimation(1, 0.86, ContentFadeOutDuration) { EasingFunction = ease };
        fadeOut.Completed += (_, _) =>
        {
            SetTrackContent(track);
            VisualGroup.BeginAnimation(OpacityProperty, new DoubleAnimation(0.86, 1, ContentFadeInDuration) { EasingFunction = ease });
        };
        VisualGroup.BeginAnimation(OpacityProperty, fadeOut);
    }

    private async Task HideAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_settings.DisplaySeconds), cancellationToken);
            await Dispatcher.InvokeAsync(BeginExitAnimation);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void BeginEnterAnimation()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        OverlayRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, EnterDuration) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, EnterDuration) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, EnterDuration) { EasingFunction = ease });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, EnterDuration) { EasingFunction = ease });
    }

    private void BeginExitAnimation()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1, 0, ExitDuration) { EasingFunction = ease };
        fade.Completed += (_, _) => Hide();
        OverlayRoot.BeginAnimation(OpacityProperty, fade);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.98, ExitDuration) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.98, ExitDuration) { EasingFunction = ease });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -4, ExitDuration) { EasingFunction = ease });
    }

    private void ApplyCoverShadowSettings()
    {
        double t = _settings.CoverShadowSizePercent / 100.0;
        if (!_hasCover)
        {
            KeyShadowCaster.Opacity = 0;
            AmbientShadowCaster.Opacity = 0;
            return;
        }

        KeyShadowCaster.Opacity = 1;
        AmbientShadowCaster.Opacity = 1;
        KeyShadowCaster.Width = ShadowCasterSize;
        KeyShadowCaster.Height = ShadowCasterSize;
        AmbientShadowCaster.Width = ShadowCasterSize;
        AmbientShadowCaster.Height = ShadowCasterSize;
        AmbientShadowEffect.BlurRadius = MaxAmbientShadowBlur * t;
        AmbientShadowEffect.ShadowDepth = MaxAmbientShadowDepth * t;
        AmbientShadowEffect.Opacity = MaxAmbientShadowOpacity * t;
        KeyShadowEffect.BlurRadius = MaxKeyShadowBlur * t;
        KeyShadowEffect.ShadowDepth = MaxKeyShadowDepth * t;
        KeyShadowEffect.Opacity = MaxKeyShadowOpacity * t;
    }

    private Point GetOrCreateVisualCenter(double scale)
    {
        if (_hasPositionedWindow)
        {
            return new Point(Left + (WindowWidth / 2), Top + (WindowHeight / 2));
        }

        double visibleContentW = BaseWidth * scale;
        double visibleContentH = BaseHeight * scale;
        double visualLeft = Math.Max(0, SystemParameters.PrimaryScreenWidth - visibleContentW - 28) * _settings.LeftPercent + 14;
        double visualTop = Math.Max(0, SystemParameters.PrimaryScreenHeight - visibleContentH - 28) * _settings.TopPercent + 14;
        _hasPositionedWindow = true;
        return new Point(visualLeft + (visibleContentW / 2), visualTop + (visibleContentH / 2));
    }

    private void ApplyScaleTransform(double scale)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(110);
        VisualScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
        VisualScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
    }

    private static BitmapSource? CreateCover(TrackInfo track)
    {
        if (track.CoverBytes is { Length: > 0 })
        {
            try
            {
                var bitmap = new BitmapImage();
                using var stream = new MemoryStream(track.CoverBytes);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private void CoverImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CoverImage.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 9, 9);
    }
}
