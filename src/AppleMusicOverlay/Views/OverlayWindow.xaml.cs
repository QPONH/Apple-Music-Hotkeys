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
        Height = 232 * scale;
        Left = Math.Max(0, SystemParameters.PrimaryScreenWidth - Width - 28) * _settings.LeftPercent + 14;
        Top = Math.Max(0, SystemParameters.PrimaryScreenHeight - Height - 28) * _settings.TopPercent + 14;
        TitleText.Visibility = _settings.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
        ArtistText.Visibility = _settings.ShowArtist ? Visibility.Visible : Visibility.Collapsed;
    }

    public Task ShowTrackAsync(TrackInfo track)
    {
        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        TitleText.Text = track.Title;
        ArtistText.Text = track.Artist;
        CoverImage.Source = CreateCover(track);
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

    private static BitmapSource CreateCover(TrackInfo track)
    {
        if (track.CoverBytes is { Length: > 0 })
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

        return CreatePlaceholder();
    }

    private static BitmapSource CreatePlaceholder()
    {
        int width = 96;
        int height = 96;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)(42 + x / 3);
                pixels[offset + 1] = (byte)(35 + y / 4);
                pixels[offset + 2] = (byte)(74 + x / 6);
                pixels[offset + 3] = 255;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void CoverImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CoverImage.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 7, 7);
    }
}
