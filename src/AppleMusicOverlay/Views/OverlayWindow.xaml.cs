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
    private static readonly Duration EnterDuration = TimeSpan.FromMilliseconds(230);
    private static readonly Duration ExitDuration = TimeSpan.FromMilliseconds(180);
    private static readonly Duration ContentFadeOutDuration = TimeSpan.FromMilliseconds(70);
    private static readonly Duration ContentFadeInDuration = TimeSpan.FromMilliseconds(130);

    private CancellationTokenSource? _hideCts;
    private OverlaySettings _settings = new();

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
        Width = 188 * scale;
        Height = 238 * scale;
        Left = Math.Max(0, SystemParameters.PrimaryScreenWidth - Width - 28) * _settings.LeftPercent + 14;
        Top = Math.Max(0, SystemParameters.PrimaryScreenHeight - Height - 28) * _settings.TopPercent + 14;
        TitleText.Visibility = _settings.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
        ArtistText.Visibility = _settings.ShowArtist ? Visibility.Visible : Visibility.Collapsed;
    }

    public Task ShowTrackAsync(TrackInfo track)
    {
        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        SetTrackContent(track);
        ContentRoot.Opacity = 1;
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
        CoverImage.Source = CreateCover(track);
        CoverImage.Opacity = CoverImage.Source == null ? 0 : 1;
    }

    private void BeginContentSwapAnimation(TrackInfo track)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeOut = new DoubleAnimation(1, 0.86, ContentFadeOutDuration) { EasingFunction = ease };
        fadeOut.Completed += (_, _) =>
        {
            SetTrackContent(track);
            ContentRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.86, 1, ContentFadeInDuration) { EasingFunction = ease });
        };
        ContentRoot.BeginAnimation(OpacityProperty, fadeOut);
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
        CoverImage.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 7, 7);
    }
}
