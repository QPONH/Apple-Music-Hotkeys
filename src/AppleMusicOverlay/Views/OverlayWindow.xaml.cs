using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private const double PointerAutoHideOpacity = 0;
    private const double PointerAutoHideScale = 0.99;
    private const double PointerAutoHideEnterMargin = 10;
    private const double PointerAutoHideExitMargin = 30;

    private static readonly Duration EnterDuration = TimeSpan.FromMilliseconds(230);
    private static readonly Duration ExitDuration = TimeSpan.FromMilliseconds(180);
    private static readonly Duration PointerFadeOutDuration = TimeSpan.FromMilliseconds(200);
    private static readonly Duration PointerFadeInDuration = TimeSpan.FromMilliseconds(240);
    private static readonly Duration ContentFadeOutDuration = TimeSpan.FromMilliseconds(70);
    private static readonly Duration ContentFadeInDuration = TimeSpan.FromMilliseconds(130);

    private CancellationTokenSource? _hideCts;
    private readonly DispatcherTimer _pointerAutoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private OverlaySettings _settings = new();
    private TrackInfo? _currentTrack;
    private bool _hasCover;
    private bool _hasPositionedWindow;
    private bool _isPointerAutoHidden;
    private bool _pointerAutoHideTargetHidden;
    private int _displayRevision;

    public OverlayWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        _pointerAutoHideTimer.Tick += (_, _) => UpdatePointerAutoHideState();
        SourceInitialized += (_, _) => WindowStyleService.ApplyOverlayStyles(this);
    }

    public void ApplySettings(OverlaySettings settings)
    {
        OverlaySettings normalized = OverlaySettingsNormalizer.Normalize(CloneSettings(settings));
        bool pauseOverlayChanged = _settings.PauseOverlay != normalized.PauseOverlay;
        _settings = normalized;
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
        ApplyPauseOverlayMode(pauseOverlayChanged);
        ApplyPointerAutoHideMode();
    }

    public Task ShowTrackAsync(TrackInfo track)
    {
        unchecked
        {
            _displayRevision++;
        }

        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        SetTrackContent(track);
        RestorePointerAutoHideVisual(force: true);
        VisualGroup.Opacity = 1;
        Show();
        Visibility = Visibility.Visible;
        WindowStyleService.ApplyOverlayStyles(this);
        BeginEnterAnimation();
        ApplyPointerAutoHideMode();

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
        _currentTrack = track;
        TitleText.Text = track.Title;
        ArtistText.Text = track.Artist;
        BitmapSource? cover = CreateCover(track);
        CoverImage.Source = cover;
        _hasCover = cover != null;
        CoverImage.Opacity = _hasCover ? 1 : 0;
        ApplyCoverShadowSettings();
        ApplyPointerAutoHideMode();
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
        int exitRevision = _displayRevision;
        StopPointerAutoHideTracking(restoreVisual: true);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1, 0, ExitDuration) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (_displayRevision == exitRevision)
            {
                Hide();
            }
        };
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
            StopPointerAutoHideTracking(restoreVisual: true);
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

    private void ApplyPauseOverlayMode(bool pauseOverlayChanged)
    {
        if (!pauseOverlayChanged || _currentTrack == null)
        {
            return;
        }

        _hideCts?.Cancel();
        if (_settings.PauseOverlay)
        {
            ShowTrackAsync(_currentTrack);
            return;
        }

        StopPointerAutoHideTracking(restoreVisual: true);
        if (IsVisible && Visibility == Visibility.Visible)
        {
            BeginExitAnimation();
        }
    }

    private void ApplyPointerAutoHideMode()
    {
        if (_settings.PauseOverlay && _settings.AutoHideOnMouseNear && _hasCover && IsVisible && Visibility == Visibility.Visible)
        {
            StartPointerAutoHideTracking();
            UpdatePointerAutoHideState();
            return;
        }

        StopPointerAutoHideTracking(restoreVisual: true);
    }

    private void StartPointerAutoHideTracking()
    {
        if (!_pointerAutoHideTimer.IsEnabled)
        {
            _pointerAutoHideTimer.Start();
        }
    }

    private void StopPointerAutoHideTracking(bool restoreVisual)
    {
        if (_pointerAutoHideTimer.IsEnabled)
        {
            _pointerAutoHideTimer.Stop();
        }

        if (restoreVisual)
        {
            AnimatePointerAutoHide(hidden: false);
        }
    }

    private void UpdatePointerAutoHideState()
    {
        if (!_settings.PauseOverlay || !_settings.AutoHideOnMouseNear || !_hasCover || !IsVisible || Visibility != Visibility.Visible)
        {
            StopPointerAutoHideTracking(restoreVisual: true);
            return;
        }

        if (!TryGetCursorPoint(out Point cursor) || !TryGetCoverScreenRect(out Rect coverRect))
        {
            AnimatePointerAutoHide(hidden: false);
            return;
        }

        double scale = _settings.ScalePercent / 100.0;
        Rect enterRect = InflateRect(coverRect, PointerAutoHideEnterMargin * scale);
        Rect exitRect = InflateRect(coverRect, PointerAutoHideExitMargin * scale);
        if (_isPointerAutoHidden)
        {
            if (!exitRect.Contains(cursor))
            {
                AnimatePointerAutoHide(hidden: false);
            }

            return;
        }

        if (enterRect.Contains(cursor))
        {
            AnimatePointerAutoHide(hidden: true);
        }
    }

    private void AnimatePointerAutoHide(bool hidden)
    {
        if (_pointerAutoHideTargetHidden == hidden)
        {
            return;
        }

        _pointerAutoHideTargetHidden = hidden;
        _isPointerAutoHidden = hidden;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Duration duration = hidden ? PointerFadeOutDuration : PointerFadeInDuration;
        double opacity = hidden ? PointerAutoHideOpacity : 1;
        double scale = hidden ? PointerAutoHideScale : 1;
        AutoHideGroup.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        AutoHideScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        AutoHideScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
    }

    private void RestorePointerAutoHideVisual(bool force)
    {
        if (!force)
        {
            AnimatePointerAutoHide(hidden: false);
            return;
        }

        _pointerAutoHideTargetHidden = false;
        _isPointerAutoHidden = false;
        AutoHideGroup.BeginAnimation(OpacityProperty, null);
        AutoHideScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AutoHideScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AutoHideGroup.Opacity = 1;
        AutoHideScale.ScaleX = 1;
        AutoHideScale.ScaleY = 1;
    }

    private bool TryGetCoverScreenRect(out Rect rect)
    {
        rect = Rect.Empty;
        if (CoverClip.ActualWidth <= 0 || CoverClip.ActualHeight <= 0)
        {
            return false;
        }

        Point topLeft = CoverClip.PointToScreen(new Point(0, 0));
        Point bottomRight = CoverClip.PointToScreen(new Point(CoverClip.ActualWidth, CoverClip.ActualHeight));
        rect = new Rect(topLeft, bottomRight);
        return !rect.IsEmpty;
    }

    private static Rect InflateRect(Rect rect, double amount)
    {
        rect.Inflate(amount, amount);
        return rect;
    }

    private static bool TryGetCursorPoint(out Point point)
    {
        if (GetCursorPos(out NativePoint nativePoint))
        {
            point = new Point(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
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

    private static OverlaySettings CloneSettings(OverlaySettings settings)
    {
        return new OverlaySettings
        {
            SchemaVersion = settings.SchemaVersion,
            DisplayStyle = settings.DisplayStyle,
            LeftPercent = settings.LeftPercent,
            TopPercent = settings.TopPercent,
            ScalePercent = settings.ScalePercent,
            DisplaySeconds = settings.DisplaySeconds,
            CoverShadowSizePercent = settings.CoverShadowSizePercent,
            ShowTitle = settings.ShowTitle,
            ShowArtist = settings.ShowArtist,
            ShowControls = settings.ShowControls,
            CloseToTray = settings.CloseToTray,
            AutoStart = settings.AutoStart,
            PauseOverlay = settings.PauseOverlay,
            AutoHideOnMouseNear = settings.AutoHideOnMouseNear,
            CaptureSourceAppUserModelId = settings.CaptureSourceAppUserModelId,
            KeyboardPrevious = settings.KeyboardPrevious,
            KeyboardNext = settings.KeyboardNext,
            KeyboardToggle = settings.KeyboardToggle,
            KeyboardTestOverlay = settings.KeyboardTestOverlay
        };
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}
