using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Views;

public sealed record OverlaySnapshot(
    byte[] Pixels,
    int PixelWidth,
    int PixelHeight,
    int ScreenLeft,
    int ScreenTop,
    int CoverScreenLeft,
    int CoverScreenTop,
    int CoverScreenRight,
    int CoverScreenBottom,
    double DpiScaleX,
    double DpiScaleY);

public sealed record OverlaySnapshotTransition(
    OverlaySnapshot Snapshot,
    TimeSpan Duration,
    bool EaseIn);

public sealed class LayeredOverlayWindow : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int BI_RGB = 0;
    private const int DIB_RGB_COLORS = 0;
    private const double EnterStartOffsetY = 8;
    private const double ExitEndOffsetY = -4;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(230);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(180);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pointerAutoHideTimer;
    private IntPtr _hwnd;
    private DirectCompositionOverlayPresenter? _compositionPresenter;
    private CancellationTokenSource? _lifetimeCts;
    private OverlaySnapshot? _currentSnapshot;
    private LayeredFrame? _currentFrame;
    private byte _currentOpacity;
    private byte _visibleOpacity = 255;
    private bool _autoHideEnabled;
    private bool _isPointerAutoHidden;
    private bool _compositionDisabled;
    private int _revision;

    public LayeredOverlayWindow()
        : this(Dispatcher.CurrentDispatcher)
    {
    }

    public LayeredOverlayWindow(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pointerAutoHideTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _pointerAutoHideTimer.Tick += (_, _) => UpdatePointerAutoHideState();
    }

    public bool IsVisible => _hwnd != IntPtr.Zero && _currentSnapshot != null;

    public Task ShowSnapshotAsync(OverlaySnapshot snapshot, int displaySeconds, bool keepVisible)
    {
        VerifyDispatcherAccess();
        if (!SystemParameters.ClientAreaAnimation)
        {
            ShowSnapshotImmediately(snapshot, displaySeconds, keepVisible);
            return Task.CompletedTask;
        }

        EnsureWindow();
        unchecked
        {
            _revision++;
        }

        int revision = _revision;
        _lifetimeCts?.Cancel();
        _lifetimeCts = new CancellationTokenSource();
        SetSnapshot(snapshot);
        _visibleOpacity = 255;
        _isPointerAutoHidden = false;
        UpdateLayeredWindow(snapshot, 255);

        if (TryEnsureCompositionPresenter())
        {
            DirectCompositionOverlayPresenter presenter = _compositionPresenter!;
            bool shown = presenter.IsVisible && !presenter.IsHiding
                ? presenter.Refresh(snapshot)
                : presenter.Show(
                    snapshot,
                    OverlayWindowMotion.CreateEnter(
                        presenter.IsVisible ? presenter.CurrentState : OverlayWindowMotion.Hidden));
            if (shown)
            {
                _currentOpacity = 255;
                CancellationToken compositionToken = _lifetimeCts.Token;
                if (!keepVisible)
                {
                    _ = HideAfterDelayAsync(displaySeconds, revision, compositionToken);
                }

                return Task.CompletedTask;
            }

            DisableComposition(restoreSource: false);
        }

        _currentOpacity = 0;
        UpdateLayeredWindow(snapshot, _currentOpacity, EnterStartOffsetY);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(_hwnd, HwndTopmost, snapshot.ScreenLeft, snapshot.ScreenTop, snapshot.PixelWidth, snapshot.PixelHeight, SWP_NOACTIVATE | SWP_SHOWWINDOW);

        CancellationToken token = _lifetimeCts.Token;
        _ = AnimateSnapshotAsync(
            0,
            255,
            EnterStartOffsetY,
            0,
            EnterDuration,
            easeOut: true,
            revision,
            token);
        if (!keepVisible)
        {
            _ = HideAfterDelayAsync(displaySeconds, revision, token);
        }

        return Task.CompletedTask;
    }

    public void ShowSnapshotImmediately(OverlaySnapshot snapshot, int displaySeconds, bool keepVisible)
    {
        VerifyDispatcherAccess();
        EnsureWindow();
        unchecked
        {
            _revision++;
        }

        int revision = _revision;
        _lifetimeCts?.Cancel();
        _lifetimeCts = new CancellationTokenSource();
        SetSnapshot(snapshot);
        _currentOpacity = 255;
        _visibleOpacity = 255;
        _isPointerAutoHidden = false;
        UpdateLayeredWindow(snapshot, 255);
        if (TryEnsureCompositionPresenter() &&
            _compositionPresenter!.ShowImmediately(snapshot, OverlayWindowMotion.Visible))
        {
            if (!keepVisible)
            {
                _ = HideAfterDelayAsync(displaySeconds, revision, _lifetimeCts.Token);
            }

            return;
        }

        if (_compositionPresenter != null)
        {
            DisableComposition(restoreSource: false);
        }

        UpdateLayeredWindow(snapshot, _currentOpacity);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(_hwnd, HwndTopmost, snapshot.ScreenLeft, snapshot.ScreenTop, snapshot.PixelWidth, snapshot.PixelHeight, SWP_NOACTIVATE | SWP_SHOWWINDOW);

        if (!keepVisible)
        {
            _ = HideAfterDelayAsync(displaySeconds, revision, _lifetimeCts.Token);
        }
    }

    public void UpdateSnapshot(OverlaySnapshot snapshot)
    {
        VerifyDispatcherAccess();
        if (_hwnd == IntPtr.Zero || _currentSnapshot == null)
        {
            return;
        }

        SetSnapshot(snapshot);
        _visibleOpacity = _currentOpacity == 0 ? (byte)255 : _currentOpacity;
        _currentOpacity = _isPointerAutoHidden ? (byte)0 : _visibleOpacity;
        if (_compositionPresenter?.IsVisible == true)
        {
            UpdateLayeredWindow(snapshot, 255);
            if (_compositionPresenter.Refresh(snapshot))
            {
                if (_isPointerAutoHidden)
                {
                    _ = _compositionPresenter.SetOpacity(0);
                }

                return;
            }

            DisableComposition();
        }

        UpdateLayeredWindow(snapshot, _currentOpacity);
    }

    public async Task TransitionSnapshotAsync(
        OverlaySnapshot snapshot,
        int displaySeconds,
        bool keepVisible,
        TimeSpan duration)
    {
        VerifyDispatcherAccess();
        if (_hwnd == IntPtr.Zero || _currentSnapshot == null || _currentFrame == null ||
            _currentSnapshot.PixelWidth != snapshot.PixelWidth ||
            _currentSnapshot.PixelHeight != snapshot.PixelHeight)
        {
            ShowSnapshotImmediately(snapshot, displaySeconds, keepVisible);
            return;
        }

        unchecked
        {
            _revision++;
        }

        int revision = _revision;
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = new CancellationTokenSource();
        CancellationToken token = _lifetimeCts.Token;
        byte[] previousPixels = _currentSnapshot.Pixels;
        byte[] workingPixels = (byte[])previousPixels.Clone();
        int[] changedByteIndices = FindChangedByteIndices(previousPixels, snapshot.Pixels);
        _currentSnapshot = snapshot;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (revision != _revision || _currentSnapshot == null)
                {
                    return;
                }

                double progress = duration <= TimeSpan.Zero
                    ? 1
                    : Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                double eased = progress < 1 ? EaseOutCubic(progress) : 1;
                foreach (int index in changedByteIndices)
                {
                    workingPixels[index] = (byte)Math.Round(
                        previousPixels[index] + ((snapshot.Pixels[index] - previousPixels[index]) * eased));
                }

                _currentFrame.TryUpdatePixels(workingPixels);
                byte opacity = _isPointerAutoHidden ? (byte)0 : _currentOpacity;
                UpdateVisibleRaster(snapshot, opacity);
                if (progress >= 1)
                {
                    break;
                }

                await Task.Delay(16, token);
            }

            SetSnapshot(snapshot);
            if (!keepVisible)
            {
                _ = HideAfterDelayAsync(displaySeconds, revision, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task TransitionSnapshotSequenceAsync(
        IReadOnlyList<OverlaySnapshotTransition> transitions,
        int displaySeconds,
        bool keepVisible)
    {
        VerifyDispatcherAccess();
        if (transitions.Count == 0)
        {
            return;
        }

        OverlaySnapshot finalSnapshot = transitions[^1].Snapshot;
        if (_hwnd == IntPtr.Zero || _currentSnapshot == null || _currentFrame == null ||
            transitions.Any(transition =>
                transition.Snapshot.PixelWidth != _currentSnapshot.PixelWidth ||
                transition.Snapshot.PixelHeight != _currentSnapshot.PixelHeight))
        {
            ShowSnapshotImmediately(finalSnapshot, displaySeconds, keepVisible);
            return;
        }

        OverlaySnapshot startingSnapshot = _currentSnapshot;
        var changedIndices = new int[transitions.Count][];
        byte[] previousTargetPixels = startingSnapshot.Pixels;
        for (int index = 0; index < transitions.Count; index++)
        {
            byte[] targetPixels = transitions[index].Snapshot.Pixels;
            changedIndices[index] = FindChangedByteIndices(previousTargetPixels, targetPixels);
            previousTargetPixels = targetPixels;
        }

        unchecked
        {
            _revision++;
        }

        int revision = _revision;
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = new CancellationTokenSource();
        CancellationToken token = _lifetimeCts.Token;
        byte[] workingPixels = (byte[])startingSnapshot.Pixels.Clone();
        _currentSnapshot = finalSnapshot;

        try
        {
            byte[] segmentStartPixels = startingSnapshot.Pixels;
            for (int segment = 0; segment < transitions.Count; segment++)
            {
                OverlaySnapshotTransition transition = transitions[segment];
                var stopwatch = Stopwatch.StartNew();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (revision != _revision || _currentSnapshot == null)
                    {
                        return;
                    }

                    double progress = transition.Duration <= TimeSpan.Zero
                        ? 1
                        : Math.Clamp(
                            stopwatch.Elapsed.TotalMilliseconds / transition.Duration.TotalMilliseconds,
                            0,
                            1);
                    double eased = progress < 1
                        ? transition.EaseIn ? EaseInCubic(progress) : EaseOutCubic(progress)
                        : 1;
                    foreach (int byteIndex in changedIndices[segment])
                    {
                        workingPixels[byteIndex] = (byte)Math.Round(
                            segmentStartPixels[byteIndex] +
                            ((transition.Snapshot.Pixels[byteIndex] - segmentStartPixels[byteIndex]) * eased));
                    }

                    _currentFrame.TryUpdatePixels(workingPixels);
                    byte opacity = _isPointerAutoHidden ? (byte)0 : _currentOpacity;
                    UpdateVisibleRaster(transition.Snapshot, opacity);
                    if (progress >= 1)
                    {
                        break;
                    }

                    await Task.Delay(12, token);
                }

                segmentStartPixels = transition.Snapshot.Pixels;
            }

            SetSnapshot(finalSnapshot);
            if (!keepVisible)
            {
                _ = HideAfterDelayAsync(displaySeconds, revision, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void ConfigurePointerAutoHide(bool enabled)
    {
        VerifyDispatcherAccess();
        _autoHideEnabled = enabled;
        if (_autoHideEnabled && IsVisible)
        {
            if (!_pointerAutoHideTimer.IsEnabled)
            {
                _pointerAutoHideTimer.Start();
            }

            UpdatePointerAutoHideState();
            return;
        }

        if (_pointerAutoHideTimer.IsEnabled)
        {
            _pointerAutoHideTimer.Stop();
        }

        if (_isPointerAutoHidden)
        {
            SetPointerAutoHidden(hidden: false);
        }
    }

    public void HideImmediately()
    {
        VerifyDispatcherAccess();
        unchecked
        {
            _revision++;
        }

        _lifetimeCts?.Cancel();
        _pointerAutoHideTimer.Stop();
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_HIDE);
        }

        if (_compositionPresenter != null)
        {
            _compositionPresenter.Hide();
            _compositionPresenter.DwmUncloakSource();
        }

        _currentSnapshot = null;
        _currentFrame?.Dispose();
        _currentFrame = null;
        _currentOpacity = 0;
        _visibleOpacity = 255;
        _isPointerAutoHidden = false;
    }

    public void Dispose()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Dispose);
            return;
        }

        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        _pointerAutoHideTimer.Stop();
        _currentFrame?.Dispose();
        _currentFrame = null;
        _compositionPresenter?.Dispose();
        _compositionPresenter = null;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void SetSnapshot(OverlaySnapshot snapshot)
    {
        _currentSnapshot = snapshot;
        if (_currentFrame?.TryUpdate(snapshot) == true)
        {
            return;
        }

        _currentFrame?.Dispose();
        _currentFrame = new LayeredFrame(snapshot);
    }

    private async Task HideAfterDelayAsync(int displaySeconds, int revision, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(displaySeconds), token);
            if (revision == _revision && _currentSnapshot != null)
            {
                if (_compositionPresenter?.IsVisible == true && SystemParameters.ClientAreaAnimation)
                {
                    OverlayWindowMotionPlan exitMotion = OverlayWindowMotion.CreateExit(
                        _compositionPresenter.CurrentState);
                    if (_compositionPresenter.StartMotion(exitMotion))
                    {
                        await Task.Delay(exitMotion.Duration, token);
                        if (revision == _revision)
                        {
                            HideImmediately();
                        }

                        return;
                    }

                    DisableComposition();
                }

                await AnimateSnapshotAsync(
                    _currentOpacity,
                    0,
                    0,
                    ExitEndOffsetY,
                    ExitDuration,
                    easeOut: false,
                    revision,
                    token);
                if (revision == _revision)
                {
                    HideImmediately();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AnimateSnapshotAsync(
        byte fromOpacity,
        byte toOpacity,
        double fromOffsetY,
        double toOffsetY,
        TimeSpan duration,
        bool easeOut,
        int revision,
        CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (revision != _revision || _currentSnapshot == null)
            {
                return;
            }

            double progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            double eased = progress < 1
                ? easeOut ? EaseOutCubic(progress) : EaseInCubic(progress)
                : 1;
            _currentOpacity = (byte)Math.Round(fromOpacity + ((toOpacity - fromOpacity) * eased));
            _visibleOpacity = _currentOpacity;
            double offsetY = fromOffsetY + ((toOffsetY - fromOffsetY) * eased);
            byte opacity = _isPointerAutoHidden ? (byte)0 : _currentOpacity;
            UpdateLayeredWindow(_currentSnapshot, opacity, offsetY);
            if (progress >= 1)
            {
                return;
            }

            await Task.Delay(16, token);
        }
    }

    private void UpdatePointerAutoHideState()
    {
        if (!_autoHideEnabled || _currentSnapshot == null)
        {
            ConfigurePointerAutoHide(enabled: false);
            return;
        }

        if (!GetCursorPos(out NativePoint cursor))
        {
            SetPointerAutoHidden(hidden: false);
            return;
        }

        double enterMargin = 10 * _currentSnapshot.DpiScaleX;
        double exitMargin = 30 * _currentSnapshot.DpiScaleX;
        bool isInsideEnter = ContainsInflatedCover(_currentSnapshot, cursor, enterMargin);
        bool isInsideExit = ContainsInflatedCover(_currentSnapshot, cursor, exitMargin);
        if (_isPointerAutoHidden)
        {
            if (!isInsideExit)
            {
                SetPointerAutoHidden(hidden: false);
            }

            return;
        }

        if (isInsideEnter)
        {
            SetPointerAutoHidden(hidden: true);
        }
    }

    private void SetPointerAutoHidden(bool hidden)
    {
        if (_isPointerAutoHidden == hidden || _currentSnapshot == null)
        {
            return;
        }

        _isPointerAutoHidden = hidden;
        _currentOpacity = hidden ? (byte)0 : _visibleOpacity;
        if (_compositionPresenter?.IsVisible == true)
        {
            if (_compositionPresenter.SetOpacity(_currentOpacity / 255d))
            {
                return;
            }

            DisableComposition();
        }

        UpdateLayeredWindow(_currentSnapshot, _currentOpacity);
    }

    private static bool ContainsInflatedCover(OverlaySnapshot snapshot, NativePoint point, double margin)
    {
        return point.X >= snapshot.CoverScreenLeft - margin &&
               point.X <= snapshot.CoverScreenRight + margin &&
               point.Y >= snapshot.CoverScreenTop - margin &&
               point.Y <= snapshot.CoverScreenBottom + margin;
    }

    private static double EaseOutCubic(double progress)
    {
        double inverse = 1 - progress;
        return 1 - (inverse * inverse * inverse);
    }

    private static double EaseInCubic(double progress)
    {
        return progress * progress * progress;
    }

    private static int[] FindChangedByteIndices(byte[] previousPixels, byte[] nextPixels)
    {
        if (previousPixels.Length != nextPixels.Length)
        {
            return [];
        }

        var changed = new List<int>();
        for (int index = 0; index < previousPixels.Length; index++)
        {
            if (previousPixels[index] != nextPixels[index])
            {
                changed.Add(index);
            }
        }

        return [.. changed];
    }

    private bool TryEnsureCompositionPresenter()
    {
        if (_compositionDisabled || _hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (_compositionPresenter != null)
        {
            return true;
        }

        if (DirectCompositionOverlayPresenter.TryCreate(_hwnd, out DirectCompositionOverlayPresenter? presenter))
        {
            _compositionPresenter = presenter;
            return true;
        }

        _compositionDisabled = true;
        return false;
    }

    private void DisableComposition(bool restoreSource = true)
    {
        DirectCompositionOverlayPresenter? presenter = _compositionPresenter;
        _compositionPresenter = null;
        _compositionDisabled = true;
        if (presenter == null)
        {
            return;
        }

        bool shouldRestore = restoreSource &&
                             presenter.IsVisible &&
                             _hwnd != IntPtr.Zero &&
                             _currentSnapshot != null;
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_HIDE);
        }

        presenter.Hide();
        presenter.DwmUncloakSource();
        presenter.Dispose();

        if (shouldRestore && _currentSnapshot != null)
        {
            byte opacity = _isPointerAutoHidden ? (byte)0 : _currentOpacity;
            UpdateLayeredWindow(_currentSnapshot, opacity);
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(
                _hwnd,
                HwndTopmost,
                _currentSnapshot.ScreenLeft,
                _currentSnapshot.ScreenTop,
                _currentSnapshot.PixelWidth,
                _currentSnapshot.PixelHeight,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    private void UpdateVisibleRaster(OverlaySnapshot snapshot, byte fallbackOpacity)
    {
        byte opacity = _compositionPresenter?.IsVisible == true ? (byte)255 : fallbackOpacity;
        UpdateLayeredWindow(snapshot, opacity);
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            return;
        }

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "STATIC",
            string.Empty,
            WS_POPUP,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not create layered overlay window: {Marshal.GetLastWin32Error()}");
        }
    }

    private void UpdateLayeredWindow(OverlaySnapshot snapshot, byte opacity, double offsetY = 0)
    {
        if (_currentFrame == null)
        {
            return;
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int top = snapshot.ScreenTop + (int)Math.Round(offsetY * snapshot.DpiScaleY);
            var destination = new NativePoint(snapshot.ScreenLeft, top);
            var size = new NativeSize(snapshot.PixelWidth, snapshot.PixelHeight);
            var source = new NativePoint(0, 0);
            var blend = new BlendFunction(AC_SRC_OVER, 0, opacity, AC_SRC_ALPHA);
            _ = UpdateLayeredWindow(_hwnd, screenDc, ref destination, ref size, _currentFrame.MemoryDc, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void VerifyDispatcherAccess()
    {
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("Layered overlay window must be used on its owning dispatcher.");
        }
    }

    private sealed class LayeredFrame : IDisposable
    {
        private readonly int _pixelWidth;
        private readonly int _pixelHeight;
        private IntPtr _oldBitmap;
        private IntPtr _bits;

        public LayeredFrame(OverlaySnapshot snapshot)
        {
            _pixelWidth = snapshot.PixelWidth;
            _pixelHeight = snapshot.PixelHeight;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return;
            }

            try
            {
                MemoryDc = CreateCompatibleDC(screenDc);
                if (MemoryDc == IntPtr.Zero)
                {
                    return;
                }

                Bitmap = CreateBitmap(snapshot);
                if (Bitmap == IntPtr.Zero)
                {
                    return;
                }

                _oldBitmap = SelectObject(MemoryDc, Bitmap);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public IntPtr MemoryDc { get; private set; }

        private IntPtr Bitmap { get; set; }

        public bool TryUpdate(OverlaySnapshot snapshot)
        {
            if (MemoryDc == IntPtr.Zero ||
                Bitmap == IntPtr.Zero ||
                _bits == IntPtr.Zero ||
                snapshot.PixelWidth != _pixelWidth ||
                snapshot.PixelHeight != _pixelHeight)
            {
                return false;
            }

            Marshal.Copy(snapshot.Pixels, 0, _bits, snapshot.Pixels.Length);
            return true;
        }

        public bool TryUpdatePixels(byte[] pixels)
        {
            if (MemoryDc == IntPtr.Zero || Bitmap == IntPtr.Zero || _bits == IntPtr.Zero ||
                pixels.Length != _pixelWidth * _pixelHeight * 4)
            {
                return false;
            }

            Marshal.Copy(pixels, 0, _bits, pixels.Length);
            return true;
        }

        public void Dispose()
        {
            if (MemoryDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            {
                SelectObject(MemoryDc, _oldBitmap);
                _oldBitmap = IntPtr.Zero;
            }

            if (Bitmap != IntPtr.Zero)
            {
                DeleteObject(Bitmap);
                Bitmap = IntPtr.Zero;
            }

            if (MemoryDc != IntPtr.Zero)
            {
                DeleteDC(MemoryDc);
                MemoryDc = IntPtr.Zero;
            }
        }

        private IntPtr CreateBitmap(OverlaySnapshot snapshot)
        {
            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = snapshot.PixelWidth,
                    Height = -snapshot.PixelHeight,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BI_RGB
                }
            };

            IntPtr bitmap = CreateDIBSection(IntPtr.Zero, ref bitmapInfo, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap != IntPtr.Zero && bits != IntPtr.Zero)
            {
                _bits = bits;
                Marshal.Copy(snapshot.Pixels, 0, bits, snapshot.Pixels.Length);
            }

            return bitmap;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize
    {
        public NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public readonly int Width;
        public readonly int Height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public BlendFunction(byte blendOperation, byte blendFlags, byte sourceConstantAlpha, byte alphaFormat)
        {
            BlendOperation = blendOperation;
            BlendFlags = blendFlags;
            SourceConstantAlpha = sourceConstantAlpha;
            AlphaFormat = alphaFormat;
        }

        public byte BlendOperation;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref NativePoint pptDst,
        ref NativeSize psize,
        IntPtr hdcSrc,
        ref NativePoint pptSrc,
        int crKey,
        ref BlendFunction pblend,
        int dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, int usage, out IntPtr ppvBits, IntPtr hSection, int offset);
}
