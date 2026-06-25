using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Views;

public enum OverlayPositionEditResult
{
    Saved,
    Cancelled
}

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
    private const double PositionEditBarGap = 12;
    private const double PositionEditScreenMargin = 8;
    private const double PositionEditLiftScale = 1.018;
    private const double PositionEditLiftOffsetY = -2;
    private const double PositionEditBarVelocityToLag = 0.012;
    private const double PositionEditBarMaxLag = 9;
    private const double PositionEditBarSettleBoost = 0.004;
    private const double PositionEditBarMaxSettleOvershoot = 4;
    private const double PositionEditBarSpringStiffness = 760;
    private const int MonitorDefaultToNearest = 2;

    private static readonly Duration EnterDuration = TimeSpan.FromMilliseconds(230);
    private static readonly Duration ExitDuration = TimeSpan.FromMilliseconds(180);
    private static readonly Duration PointerFadeOutDuration = TimeSpan.FromMilliseconds(200);
    private static readonly Duration PointerFadeInDuration = TimeSpan.FromMilliseconds(240);
    private static readonly Duration PositionEditBarDuration = TimeSpan.FromMilliseconds(190);
    private static readonly Duration PositionEditLiftDuration = TimeSpan.FromMilliseconds(110);
    private static readonly Duration PositionEditDropDuration = TimeSpan.FromMilliseconds(190);
    private static readonly Duration PositionEditCancelDuration = TimeSpan.FromMilliseconds(220);
    private static readonly Duration ContentFadeOutDuration = TimeSpan.FromMilliseconds(70);
    private static readonly Duration ContentFadeInDuration = TimeSpan.FromMilliseconds(130);
    private static readonly CubicEase PositionEditEase = new() { EasingMode = EasingMode.EaseOut };

    private CancellationTokenSource? _hideCts;
    private readonly DispatcherTimer _pointerAutoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private OverlaySettings _settings = new();
    private TrackInfo? _currentTrack;
    private bool _hasCover;
    private bool _hasPositionedWindow;
    private bool _isPointerAutoHidden;
    private bool _pointerAutoHideTargetHidden;
    private int _displayRevision;
    private bool _isPositionEditing;
    private bool _isPositionPointerDown;
    private bool _isPositionDragging;
    private bool _pendingPointerAutoHideResume;
    private Point _positionEditOriginalWindowPosition;
    private Point _positionEditDragOffset;
    private Point _positionEditPointerDownScreen;
    private Point _positionEditLastWindowPosition;
    private DateTime _positionEditLastMoveTime;
    private DateTime _positionEditLastFollowFrameTime;
    private DateTime _positionEditBarSettleBoostUntil;
    private Vector _positionEditLastVelocity;
    private Vector _positionEditBarOffset;
    private Vector _positionEditBarTargetOffset;
    private Vector _positionEditBarVelocity;
    private OverlaySettings? _positionEditSettingsTarget;
    private Action? _positionEditSaveCallback;
    private Action<OverlayPositionEditResult>? _positionEditCompletedCallback;
    private bool _isPositionLifted;
    private bool _isPositionEditBarFollowing;

    public OverlayWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        _pointerAutoHideTimer.Tick += (_, _) => UpdatePointerAutoHideState();
        SourceInitialized += (_, _) => ApplyOverlayWindowStyles();
        PreviewKeyDown += OverlayWindow_PreviewKeyDown;
        Closed += (_, _) => CleanupPositionEdit(restoreOriginalPosition: false, savePosition: false, animateBar: false);
    }

    public void ApplySettings(OverlaySettings settings)
    {
        OverlaySettings normalized = OverlaySettingsNormalizer.Normalize(CloneSettings(settings));
        bool pauseOverlayChanged = _settings.PauseOverlay != normalized.PauseOverlay;
        _settings = normalized;
        double scale = _settings.ScalePercent / 100.0;
        Point visualCenter = _isPositionEditing
            ? new Point(Left + (WindowWidth / 2), Top + (WindowHeight / 2))
            : GetOrCreateVisualCenter(scale);
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
        ApplyOverlayWindowStyles();
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
        if (_isPositionEditing)
        {
            CleanupPositionEdit(restoreOriginalPosition: false, savePosition: false, animateBar: true);
        }

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

        if (_isPositionEditing)
        {
            CancelPositionEdit(animateReturn: true);
        }

        StopPointerAutoHideTracking(restoreVisual: true);
        if (IsVisible && Visibility == Visibility.Visible)
        {
            BeginExitAnimation();
        }
    }

    private void ApplyPointerAutoHideMode()
    {
        if (_isPositionEditing)
        {
            StopPointerAutoHideTracking(restoreVisual: true);
            return;
        }

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

    public void BeginPositionEdit(
        OverlaySettings settings,
        Action saveSettings,
        Action<OverlayPositionEditResult>? positionEditCompleted = null)
    {
        if (_isPositionEditing)
        {
            return;
        }

        ApplySettings(settings);
        if (!IsVisible || Visibility != Visibility.Visible)
        {
            Show();
            Visibility = Visibility.Visible;
        }

        _positionEditSettingsTarget = settings;
        _positionEditSaveCallback = saveSettings;
        _positionEditCompletedCallback = positionEditCompleted;
        _positionEditOriginalWindowPosition = new Point(Left, Top);
        _pendingPointerAutoHideResume = _settings.PauseOverlay && _settings.AutoHideOnMouseNear;
        _isPositionEditing = true;
        _isPositionPointerDown = false;
        _isPositionDragging = false;
        _isPositionLifted = false;
        _positionEditLastVelocity = default;
        _positionEditBarOffset = default;
        _positionEditBarTargetOffset = default;
        _positionEditBarVelocity = default;
        PositionEditBarInertiaTranslate.X = 0;
        PositionEditBarInertiaTranslate.Y = 0;
        _hideCts?.Cancel();
        RestorePointerAutoHideVisual(force: true);
        StopPointerAutoHideTracking(restoreVisual: true);
        AutoHideGroup.IsHitTestVisible = true;
        ApplyOverlayWindowStyles();
        CoverClip.Cursor = Cursors.SizeAll;
        PreparePositionEditMotionResources();
        ShowPositionEditBar();
        AnimatePositionEditLift(lifted: false, duration: PositionEditBarDuration);
    }

    private void PositionEditDone_Click(object sender, RoutedEventArgs e)
    {
        CompletePositionEdit();
    }

    private void PositionEditCancel_Click(object sender, RoutedEventArgs e)
    {
        CancelPositionEdit(animateReturn: true);
    }

    private void CoverClip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isPositionEditing || !TryGetCursorPointDip(out Point cursor))
        {
            return;
        }

        e.Handled = true;
        _isPositionPointerDown = true;
        _isPositionDragging = false;
        _positionEditPointerDownScreen = cursor;
        _positionEditDragOffset = new Point(cursor.X - Left, cursor.Y - Top);
        _positionEditLastWindowPosition = new Point(Left, Top);
        _positionEditLastMoveTime = DateTime.UtcNow;
        Mouse.Capture(CoverClip);
        AnimatePositionEditLift(lifted: true, duration: PositionEditLiftDuration);
        StartPositionEditBarFollow();
    }

    private void CoverClip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPositionEditing || !_isPositionPointerDown || !TryGetCursorPointDip(out Point cursor))
        {
            return;
        }

        e.Handled = true;
        double threshold = Math.Max(2, Math.Min(SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance) * 0.5);
        if (!_isPositionDragging)
        {
            double dx = cursor.X - _positionEditPointerDownScreen.X;
            double dy = cursor.Y - _positionEditPointerDownScreen.Y;
            if (Math.Sqrt((dx * dx) + (dy * dy)) < threshold)
            {
                return;
            }

            _isPositionDragging = true;
        }

        Point next = ClampWindowPosition(new Point(cursor.X - _positionEditDragOffset.X, cursor.Y - _positionEditDragOffset.Y));
        UpdatePositionEditDragVelocity(next);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = next.X;
        Top = next.Y;
        UpdatePositionEditBarPlacement();
    }

    private void CoverClip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPositionEditing || !_isPositionPointerDown)
        {
            return;
        }

        e.Handled = true;
        _isPositionPointerDown = false;
        _isPositionDragging = false;
        Mouse.Capture(null);
        AnimatePositionEditLift(lifted: false, duration: PositionEditDropDuration);
        SettlePositionEditBarFollow();
    }

    private void OverlayWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isPositionEditing)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CompletePositionEdit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelPositionEdit(animateReturn: true);
        }
    }

    private void CompletePositionEdit()
    {
        if (!_isPositionEditing)
        {
            return;
        }

        if (_positionEditSettingsTarget != null)
        {
            (double leftPercent, double topPercent) = CalculatePositionPercents(new Point(Left, Top), _settings.ScalePercent / 100.0);
            _positionEditSettingsTarget.LeftPercent = leftPercent;
            _positionEditSettingsTarget.TopPercent = topPercent;
            _settings.LeftPercent = leftPercent;
            _settings.TopPercent = topPercent;
            _positionEditSaveCallback?.Invoke();
        }

        CleanupPositionEdit(restoreOriginalPosition: false, savePosition: true, animateBar: true, OverlayPositionEditResult.Saved);
    }

    private void CancelPositionEdit(bool animateReturn)
    {
        if (!_isPositionEditing)
        {
            return;
        }

        Mouse.Capture(null);
        _isPositionPointerDown = false;
        _isPositionDragging = false;
        StopPositionEditBarFollow(resetOffset: true);
        AnimatePositionEditLift(lifted: false, duration: PositionEditDropDuration);
        if (!animateReturn)
        {
            Left = _positionEditOriginalWindowPosition.X;
            Top = _positionEditOriginalWindowPosition.Y;
            CleanupPositionEdit(restoreOriginalPosition: true, savePosition: false, animateBar: true, OverlayPositionEditResult.Cancelled);
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var leftAnimation = new DoubleAnimation(Left, _positionEditOriginalWindowPosition.X, PositionEditCancelDuration) { EasingFunction = ease };
        var topAnimation = new DoubleAnimation(Top, _positionEditOriginalWindowPosition.Y, PositionEditCancelDuration) { EasingFunction = ease };
        topAnimation.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = _positionEditOriginalWindowPosition.X;
            Top = _positionEditOriginalWindowPosition.Y;
            CleanupPositionEdit(restoreOriginalPosition: true, savePosition: false, animateBar: true, OverlayPositionEditResult.Cancelled);
        };
        BeginAnimation(LeftProperty, leftAnimation, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(TopProperty, topAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void CleanupPositionEdit(
        bool restoreOriginalPosition,
        bool savePosition,
        bool animateBar,
        OverlayPositionEditResult? editResult = null)
    {
        if (!_isPositionEditing && PositionEditBar.Visibility != Visibility.Visible)
        {
            return;
        }

        Mouse.Capture(null);
        _isPositionPointerDown = false;
        _isPositionDragging = false;
        _isPositionEditing = false;
        _isPositionLifted = false;
        StopPositionEditBarFollow(resetOffset: true);
        AutoHideGroup.IsHitTestVisible = false;
        CoverClip.Cursor = null;
        if (restoreOriginalPosition)
        {
            Left = _positionEditOriginalWindowPosition.X;
            Top = _positionEditOriginalWindowPosition.Y;
        }

        AnimatePositionEditLift(lifted: false, duration: PositionEditDropDuration);
        HidePositionEditBar(animateBar);
        Action<OverlayPositionEditResult>? completedCallback = _positionEditCompletedCallback;
        _positionEditSettingsTarget = null;
        _positionEditSaveCallback = null;
        _positionEditCompletedCallback = null;
        ApplyOverlayWindowStyles();
        if (editResult.HasValue)
        {
            completedCallback?.Invoke(editResult.Value);
        }

        if (_pendingPointerAutoHideResume && _settings.PauseOverlay && _settings.AutoHideOnMouseNear)
        {
            ResumePointerAutoHideAfterPositionEdit();
        }
        else
        {
            ApplyPointerAutoHideMode();
        }

        _pendingPointerAutoHideResume = false;
    }

    private async void ResumePointerAutoHideAfterPositionEdit()
    {
        await Task.Delay(220);
        if (!_isPositionEditing)
        {
            ApplyPointerAutoHideMode();
        }
    }

    private void ShowPositionEditBar()
    {
        PositionEditBar.Visibility = Visibility.Visible;
        UpdatePositionEditBarPlacement();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PositionEditBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, PositionEditBarDuration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        PositionEditBarTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, PositionEditBarDuration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
    }

    private void HidePositionEditBar(bool animate)
    {
        if (!animate)
        {
            PositionEditBar.BeginAnimation(OpacityProperty, null);
            PositionEditBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            StopPositionEditBarFollow(resetOffset: true);
            PositionEditBar.Opacity = 0;
            PositionEditBar.Visibility = Visibility.Collapsed;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(PositionEditBar.Opacity, 0, PositionEditBarDuration) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            StopPositionEditBarFollow(resetOffset: true);
            PositionEditBar.Visibility = Visibility.Collapsed;
        };
        PositionEditBar.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        PositionEditBarTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 6, PositionEditBarDuration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimatePositionEditLift(bool lifted, Duration duration)
    {
        _isPositionLifted = lifted;
        double scale = lifted ? PositionEditLiftScale : 1;
        double offsetY = lifted ? PositionEditLiftOffsetY : 0;
        PositionEditScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(PositionEditScale.ScaleX, scale, duration) { EasingFunction = PositionEditEase },
            HandoffBehavior.SnapshotAndReplace);
        PositionEditScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(PositionEditScale.ScaleY, scale, duration) { EasingFunction = PositionEditEase },
            HandoffBehavior.SnapshotAndReplace);
        PositionEditTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(PositionEditTranslate.Y, offsetY, duration) { EasingFunction = PositionEditEase },
            HandoffBehavior.SnapshotAndReplace);
        ApplyPositionEditShadowSettings(lifted, duration);
    }

    private void PreparePositionEditMotionResources()
    {
        BeginNoOpAnimation(PositionEditScale, ScaleTransform.ScaleXProperty, PositionEditScale.ScaleX);
        BeginNoOpAnimation(PositionEditScale, ScaleTransform.ScaleYProperty, PositionEditScale.ScaleY);
        BeginNoOpAnimation(PositionEditTranslate, TranslateTransform.YProperty, PositionEditTranslate.Y);
        BeginNoOpAnimation(PositionEditBarTranslate, TranslateTransform.YProperty, PositionEditBarTranslate.Y);
        BeginNoOpAnimation(PositionEditBarInertiaTranslate, TranslateTransform.XProperty, PositionEditBarInertiaTranslate.X);
        BeginNoOpAnimation(PositionEditBarInertiaTranslate, TranslateTransform.YProperty, PositionEditBarInertiaTranslate.Y);
        BeginNoOpAnimation(AmbientShadowEffect, DropShadowEffect.BlurRadiusProperty, AmbientShadowEffect.BlurRadius);
        BeginNoOpAnimation(AmbientShadowEffect, DropShadowEffect.OpacityProperty, AmbientShadowEffect.Opacity);
        BeginNoOpAnimation(KeyShadowEffect, DropShadowEffect.BlurRadiusProperty, KeyShadowEffect.BlurRadius);
        BeginNoOpAnimation(KeyShadowEffect, DropShadowEffect.ShadowDepthProperty, KeyShadowEffect.ShadowDepth);
        BeginNoOpAnimation(KeyShadowEffect, DropShadowEffect.OpacityProperty, KeyShadowEffect.Opacity);
        StartPositionEditBarFollow();
    }

    private static void BeginNoOpAnimation(Animatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(
            property,
            new DoubleAnimation(value, value, TimeSpan.FromMilliseconds(1)) { EasingFunction = PositionEditEase },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyPositionEditShadowSettings(bool lifted, Duration duration)
    {
        double t = _hasCover ? _settings.CoverShadowSizePercent / 100.0 : 0;
        double ambientBlur = MaxAmbientShadowBlur * t;
        double ambientOpacity = MaxAmbientShadowOpacity * t;
        double keyBlur = MaxKeyShadowBlur * t;
        double keyDepth = MaxKeyShadowDepth * t;
        double keyOpacity = MaxKeyShadowOpacity * t;
        if (lifted)
        {
            ambientBlur *= 1.08;
            ambientOpacity = Math.Min(0.28, ambientOpacity + 0.025);
            keyBlur *= 1.12;
            keyDepth += 1.8;
            keyOpacity = Math.Min(0.32, keyOpacity + 0.045);
        }

        AmbientShadowEffect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(AmbientShadowEffect.BlurRadius, ambientBlur, duration) { EasingFunction = PositionEditEase }, HandoffBehavior.SnapshotAndReplace);
        AmbientShadowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(AmbientShadowEffect.Opacity, ambientOpacity, duration) { EasingFunction = PositionEditEase }, HandoffBehavior.SnapshotAndReplace);
        KeyShadowEffect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(KeyShadowEffect.BlurRadius, keyBlur, duration) { EasingFunction = PositionEditEase }, HandoffBehavior.SnapshotAndReplace);
        KeyShadowEffect.BeginAnimation(DropShadowEffect.ShadowDepthProperty, new DoubleAnimation(KeyShadowEffect.ShadowDepth, keyDepth, duration) { EasingFunction = PositionEditEase }, HandoffBehavior.SnapshotAndReplace);
        KeyShadowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(KeyShadowEffect.Opacity, keyOpacity, duration) { EasingFunction = PositionEditEase }, HandoffBehavior.SnapshotAndReplace);
    }

    private void StartPositionEditBarFollow()
    {
        _positionEditBarSettleBoostUntil = DateTime.MinValue;
        if (_isPositionEditBarFollowing)
        {
            return;
        }

        _isPositionEditBarFollowing = true;
        _positionEditLastFollowFrameTime = DateTime.UtcNow;
        CompositionTarget.Rendering += UpdatePositionEditBarFollow;
    }

    private void StopPositionEditBarFollow(bool resetOffset)
    {
        if (_isPositionEditBarFollowing)
        {
            CompositionTarget.Rendering -= UpdatePositionEditBarFollow;
            _isPositionEditBarFollowing = false;
        }

        _positionEditBarTargetOffset = default;
        _positionEditBarVelocity = default;
        if (resetOffset)
        {
            _positionEditBarOffset = default;
            PositionEditBarInertiaTranslate.X = 0;
            PositionEditBarInertiaTranslate.Y = 0;
        }
    }

    private void UpdatePositionEditDragVelocity(Point nextWindowPosition)
    {
        DateTime now = DateTime.UtcNow;
        double dt = Math.Max(0.001, (now - _positionEditLastMoveTime).TotalSeconds);
        var delta = nextWindowPosition - _positionEditLastWindowPosition;
        _positionEditLastVelocity = delta / dt;
        _positionEditLastWindowPosition = nextWindowPosition;
        _positionEditLastMoveTime = now;
        SetPositionEditBarTarget(ClampVector(-_positionEditLastVelocity * PositionEditBarVelocityToLag, PositionEditBarMaxLag));
    }

    private void SettlePositionEditBarFollow()
    {
        double speed = _positionEditLastVelocity.Length;
        if (speed < 120)
        {
            SetPositionEditBarTarget(default);
            return;
        }

        SetPositionEditBarTarget(ClampVector(_positionEditLastVelocity * PositionEditBarSettleBoost, PositionEditBarMaxSettleOvershoot));
        _positionEditBarSettleBoostUntil = DateTime.UtcNow.AddMilliseconds(70);
        StartPositionEditBarFollow();
    }

    private void SetPositionEditBarTarget(Vector targetOffset)
    {
        _positionEditBarTargetOffset = targetOffset;
        StartPositionEditBarFollow();
    }

    private void UpdatePositionEditBarFollow(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        double dt = Math.Min(0.033, Math.Max(0.001, (now - _positionEditLastFollowFrameTime).TotalSeconds));
        _positionEditLastFollowFrameTime = now;

        if (_positionEditBarSettleBoostUntil != DateTime.MinValue && now >= _positionEditBarSettleBoostUntil)
        {
            _positionEditBarSettleBoostUntil = DateTime.MinValue;
            _positionEditBarTargetOffset = default;
        }

        double damping = 2 * Math.Sqrt(PositionEditBarSpringStiffness);
        Vector displacement = _positionEditBarOffset - _positionEditBarTargetOffset;
        Vector acceleration = (-PositionEditBarSpringStiffness * displacement) - (damping * _positionEditBarVelocity);
        _positionEditBarVelocity += acceleration * dt;
        _positionEditBarOffset += _positionEditBarVelocity * dt;
        _positionEditBarOffset = ClampVector(_positionEditBarOffset, PositionEditBarMaxLag);
        PositionEditBarInertiaTranslate.X = _positionEditBarOffset.X;
        PositionEditBarInertiaTranslate.Y = _positionEditBarOffset.Y;

        if (!_isPositionPointerDown &&
            _positionEditBarSettleBoostUntil == DateTime.MinValue &&
            _positionEditBarOffset.Length < 0.08 &&
            _positionEditBarVelocity.Length < 0.25)
        {
            StopPositionEditBarFollow(resetOffset: true);
        }
    }

    private static Vector ClampVector(Vector vector, double maxLength)
    {
        if (vector.Length <= maxLength || vector.Length <= 0)
        {
            return vector;
        }

        vector.Normalize();
        return vector * maxLength;
    }

    private void UpdatePositionEditBarPlacement()
    {
        Rect contentRect = GetOverlayVisibleContentRectInWindow(_settings.ScalePercent / 100.0);
        Rect coverRect = GetCoverVisibleRectInWindow(_settings.ScalePercent / 100.0);
        Size barSize = GetPositionEditBarSize();
        Rect workArea = GetMonitorWorkAreaForRect(GetOverlayVisibleContentRectForWindow(new Point(Left, Top), _settings.ScalePercent / 100.0));

        double left = contentRect.Left + ((contentRect.Width - barSize.Width) / 2);
        double screenLeft = Clamp(Left + left, workArea.Left + PositionEditScreenMargin, workArea.Right - barSize.Width - PositionEditScreenMargin);
        left = screenLeft - Left;

        double topAbove = coverRect.Top - barSize.Height - PositionEditBarGap;
        double topBelow = contentRect.Bottom + PositionEditBarGap;
        double screenTopAbove = Top + topAbove;
        double screenBottomAbove = screenTopAbove + barSize.Height;
        double top = screenTopAbove >= workArea.Top + PositionEditScreenMargin &&
                     screenBottomAbove <= workArea.Bottom - PositionEditScreenMargin
            ? topAbove
            : topBelow;

        double screenTop = Clamp(Top + top, workArea.Top + PositionEditScreenMargin, workArea.Bottom - barSize.Height - PositionEditScreenMargin);
        top = screenTop - Top;
        Canvas.SetLeft(PositionEditBar, left);
        Canvas.SetTop(PositionEditBar, top);
    }

    private Point ClampWindowPosition(Point windowPosition)
    {
        double scale = _settings.ScalePercent / 100.0;
        Rect visibleRect = GetOverlayVisibleContentRectForWindow(windowPosition, scale);
        Rect workArea = GetMonitorWorkAreaForRect(visibleRect);
        double dx = 0;
        double dy = 0;
        if (visibleRect.Left < workArea.Left + PositionEditScreenMargin)
        {
            dx = workArea.Left + PositionEditScreenMargin - visibleRect.Left;
        }
        else if (visibleRect.Right > workArea.Right - PositionEditScreenMargin)
        {
            dx = workArea.Right - PositionEditScreenMargin - visibleRect.Right;
        }

        if (visibleRect.Top < workArea.Top + PositionEditScreenMargin)
        {
            dy = workArea.Top + PositionEditScreenMargin - visibleRect.Top;
        }
        else if (visibleRect.Bottom > workArea.Bottom - PositionEditScreenMargin)
        {
            dy = workArea.Bottom - PositionEditScreenMargin - visibleRect.Bottom;
        }

        return new Point(windowPosition.X + dx, windowPosition.Y + dy);
    }

    private (double LeftPercent, double TopPercent) CalculatePositionPercents(Point windowPosition, double scale)
    {
        Rect visualRect = GetOverlayVisibleContentRectForWindow(windowPosition, scale);
        Rect virtualScreen = GetVirtualScreenRect();
        double availableWidth = Math.Max(1, virtualScreen.Width - visualRect.Width - 28);
        double availableHeight = Math.Max(1, virtualScreen.Height - visualRect.Height - 28);
        double leftPercent = (visualRect.Left - virtualScreen.Left - 14) / availableWidth;
        double topPercent = (visualRect.Top - virtualScreen.Top - 14) / availableHeight;
        return (Clamp(leftPercent, 0, 1), Clamp(topPercent, 0, 1));
    }

    private Rect GetOverlayVisibleContentRectInWindow(double scale)
    {
        Rect? bounds = TryGetElementRectInWindow(CoverClip);
        AddTextBounds(ref bounds, TitleText);
        AddTextBounds(ref bounds, ArtistText);
        return bounds ?? GetFallbackVisibleContentRectInWindow(scale);
    }

    private Rect GetCoverVisibleRectInWindow(double scale)
    {
        return TryGetElementRectInWindow(CoverClip) ?? TransformLogicalRectToWindow(new Rect(40, 40, CoverSize, CoverSize), scale);
    }

    private Rect GetOverlayVisibleContentRectForWindow(Point windowPosition, double scale)
    {
        Rect rect = GetOverlayVisibleContentRectInWindow(scale);
        rect.Offset(windowPosition.X, windowPosition.Y);
        return rect;
    }

    private Size GetPositionEditBarSize()
    {
        PositionEditBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = PositionEditBar.ActualWidth > 0 ? PositionEditBar.ActualWidth : PositionEditBar.DesiredSize.Width;
        double height = PositionEditBar.ActualHeight > 0 ? PositionEditBar.ActualHeight : PositionEditBar.DesiredSize.Height;
        return new Size(width, height);
    }

    private void AddElementBounds(ref Rect? bounds, FrameworkElement element)
    {
        Rect? elementRect = TryGetElementRectInWindow(element);
        if (elementRect == null)
        {
            return;
        }

        bounds = bounds == null ? elementRect.Value : Rect.Union(bounds.Value, elementRect.Value);
    }

    private void AddTextBounds(ref Rect? bounds, TextBlock textBlock)
    {
        Rect? textRect = TryGetTextVisibleRectInWindow(textBlock);
        if (textRect == null)
        {
            return;
        }

        bounds = bounds == null ? textRect.Value : Rect.Union(bounds.Value, textRect.Value);
    }

    private Rect? TryGetTextVisibleRectInWindow(TextBlock textBlock)
    {
        if (textBlock.Visibility != Visibility.Visible ||
            string.IsNullOrWhiteSpace(textBlock.Text) ||
            textBlock.ActualWidth <= 0 ||
            textBlock.ActualHeight <= 0)
        {
            return null;
        }

        PresentationSource? source = PresentationSource.FromVisual(this);
        double pixelsPerDip = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var formattedText = new FormattedText(
            textBlock.Text,
            CultureInfo.CurrentUICulture,
            textBlock.FlowDirection,
            new Typeface(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch),
            textBlock.FontSize,
            Brushes.White,
            pixelsPerDip)
        {
            MaxTextWidth = textBlock.ActualWidth,
            Trimming = textBlock.TextTrimming
        };

        double visibleWidth = Clamp(formattedText.WidthIncludingTrailingWhitespace, 0, textBlock.ActualWidth);
        double visibleHeight = Clamp(formattedText.Height, 0, textBlock.ActualHeight);
        if (visibleWidth <= 0 || visibleHeight <= 0)
        {
            return null;
        }

        double left = textBlock.TextAlignment == TextAlignment.Center
            ? (textBlock.ActualWidth - visibleWidth) / 2
            : textBlock.TextAlignment == TextAlignment.Right
                ? textBlock.ActualWidth - visibleWidth
                : 0;
        double top = Math.Max(0, (textBlock.ActualHeight - visibleHeight) / 2);

        try
        {
            GeneralTransform transform = textBlock.TransformToAncestor(WindowCanvas);
            return transform.TransformBounds(new Rect(left, top, visibleWidth, visibleHeight));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private Rect? TryGetElementRectInWindow(FrameworkElement element)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return null;
        }

        try
        {
            GeneralTransform transform = element.TransformToAncestor(WindowCanvas);
            return transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private Rect GetFallbackVisibleContentRectInWindow(double scale)
    {
        Rect bounds = new(40, 40, CoverSize, CoverSize);
        if (_settings.ShowTitle)
        {
            bounds = Rect.Union(bounds, new Rect(4, 228, 248, 18));
        }

        if (_settings.ShowArtist)
        {
            bounds = Rect.Union(bounds, new Rect(4, 249, 248, 16));
        }

        return TransformLogicalRectToWindow(bounds, scale);
    }

    private static Rect TransformLogicalRectToWindow(Rect logicalRect, double scale)
    {
        double visualLeft = (WindowWidth / 2) - (BaseWidth / 2);
        double visualTop = (WindowHeight / 2) - (BaseHeight / 2);
        double left = visualLeft + VisualCenterX + ((logicalRect.Left - VisualCenterX) * scale);
        double top = visualTop + VisualCenterY + ((logicalRect.Top - VisualCenterY) * scale);
        double right = visualLeft + VisualCenterX + ((logicalRect.Right - VisualCenterX) * scale);
        double bottom = visualTop + VisualCenterY + ((logicalRect.Bottom - VisualCenterY) * scale);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private void ApplyOverlayWindowStyles()
    {
        WindowStyleService.ApplyOverlayStyles(this, clickThrough: !_isPositionEditing);
    }

    private static Rect GetVirtualScreenRect()
    {
        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }

    private Rect GetMonitorWorkAreaForRect(Rect screenRect)
    {
        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null)
        {
            return SystemParameters.WorkArea;
        }

        Matrix toDevice = source.CompositionTarget.TransformToDevice;
        Point topLeft = toDevice.Transform(new Point(screenRect.Left, screenRect.Top));
        Point bottomRight = toDevice.Transform(new Point(screenRect.Right, screenRect.Bottom));
        var nativeRect = new NativeRect
        {
            Left = (int)Math.Floor(Math.Min(topLeft.X, bottomRight.X)),
            Top = (int)Math.Floor(Math.Min(topLeft.Y, bottomRight.Y)),
            Right = (int)Math.Ceiling(Math.Max(topLeft.X, bottomRight.X)),
            Bottom = (int)Math.Ceiling(Math.Max(topLeft.Y, bottomRight.Y))
        };

        IntPtr monitor = MonitorFromRect(ref nativeRect, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return GetVirtualScreenRect();
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return GetVirtualScreenRect();
        }

        Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
        Point workTopLeft = fromDevice.Transform(new Point(monitorInfo.Work.Left, monitorInfo.Work.Top));
        Point workBottomRight = fromDevice.Transform(new Point(monitorInfo.Work.Right, monitorInfo.Work.Bottom));
        return new Rect(workTopLeft, workBottomRight);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }

        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private bool TryGetCursorPointDip(out Point point)
    {
        if (!TryGetCursorPoint(out Point screenPoint))
        {
            point = default;
            return false;
        }

        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            point = source.CompositionTarget.TransformFromDevice.Transform(screenPoint);
            return true;
        }

        point = screenPoint;
        return true;
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

        Rect virtualScreen = GetVirtualScreenRect();
        Rect visibleContent = GetFallbackVisibleContentRectInWindow(scale);
        double visibleContentW = visibleContent.Width;
        double visibleContentH = visibleContent.Height;
        double visualLeft = virtualScreen.Left + Math.Max(0, virtualScreen.Width - visibleContentW - 28) * _settings.LeftPercent + 14;
        double visualTop = virtualScreen.Top + Math.Max(0, virtualScreen.Height - visibleContentH - 28) * _settings.TopPercent + 14;
        _hasPositionedWindow = true;
        return new Point(
            visualLeft + (WindowWidth / 2) - visibleContent.Left,
            visualTop + (WindowHeight / 2) - visibleContent.Top);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
}
