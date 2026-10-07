using System.Diagnostics;
using System.Runtime.InteropServices;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Views;

internal sealed class DirectCompositionOverlayPresenter : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int GWLP_WNDPROC = -4;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const int DWMWA_CLOAK = 13;
    private const double MotionPadding = 16;

    private static readonly nint HwndTopmost = new(-1);
    private static readonly Guid IidCompositionDesktopDevice = new("5F4633FE-1E08-4CB8-8C75-CE24333F5602");

    private readonly nint _sourceHwnd;
    private readonly WindowProcedure _targetWindowProcedure;
    private nint _targetHwnd;
    private nint _previousTargetWindowProcedure;
    private nint _device;
    private nint _target;
    private nint _visual;
    private nint _sourceSurface;
    private nint _scaleTransform;
    private nint _effectGroup;
    private OverlayWindowMotionPlan? _activePlan;
    private Stopwatch? _activeMotionClock;
    private OverlayWindowMotionState _motionState = OverlayWindowMotion.Hidden;
    private double? _opacityOverride;
    private int _paddingX;
    private int _paddingY;
    private double _dpiScaleY = 1;
    private bool _sourceCloaked;
    private bool _isVisible;
    private bool _disposed;

    private DirectCompositionOverlayPresenter(nint sourceHwnd)
    {
        _sourceHwnd = sourceHwnd;
        _targetWindowProcedure = TargetWindowProc;
    }

    public OverlayWindowMotionState CurrentState
    {
        get
        {
            if (_activePlan == null || _activeMotionClock == null)
            {
                return _motionState;
            }

            _motionState = OverlayWindowMotion.Evaluate(_activePlan, _activeMotionClock.Elapsed);
            if (_opacityOverride.HasValue)
            {
                _motionState = _motionState with { Opacity = _opacityOverride.Value };
            }
            if (_activeMotionClock.Elapsed >= _activePlan.Duration)
            {
                _activePlan = null;
                _activeMotionClock = null;
            }

            return _motionState;
        }
    }

    public bool IsVisible => _isVisible;

    public bool IsHiding => _activePlan != null &&
                            OverlayWindowMotion.Evaluate(_activePlan, _activePlan.Duration).Opacity <= 0;

    public static bool TryCreate(
        nint sourceHwnd,
        out DirectCompositionOverlayPresenter? presenter)
    {
        presenter = null;
        DirectCompositionOverlayPresenter? candidate = null;
        try
        {
            if (sourceHwnd == nint.Zero ||
                DwmIsCompositionEnabled(out int enabled) < 0 ||
                enabled == 0)
            {
                return false;
            }

            candidate = new DirectCompositionOverlayPresenter(sourceHwnd);
            candidate.Initialize();
            presenter = candidate;
            return true;
        }
        catch (Exception ex) when (ex is COMException or
                                   DllNotFoundException or
                                   EntryPointNotFoundException or
                                   InvalidOperationException)
        {
            candidate?.Dispose();
            presenter = null;
            return false;
        }
    }

    public bool Show(
        OverlaySnapshot snapshot,
        OverlayWindowMotionPlan motion)
    {
        ThrowIfDisposed();
        try
        {
            UpdateBounds(snapshot);
            SetStaticState(OverlayWindowMotion.Evaluate(motion, TimeSpan.Zero));
            ThrowIfFailed(Commit(_device), "Commit initial overlay state");

            if (!DwmCloakSource())
            {
                return false;
            }

            _ = ShowWindow(_sourceHwnd, SW_SHOWNOACTIVATE);
            _ = ShowWindow(_targetHwnd, SW_SHOWNOACTIVATE);
            _ = SetWindowPos(
                _targetHwnd,
                HwndTopmost,
                snapshot.ScreenLeft - _paddingX,
                snapshot.ScreenTop - _paddingY,
                snapshot.PixelWidth + (_paddingX * 2),
                snapshot.PixelHeight + (_paddingY * 2),
                SWP_NOACTIVATE | SWP_SHOWWINDOW);

            if (!StartMotion(motion))
            {
                return false;
            }

            _isVisible = true;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public bool Refresh(OverlaySnapshot snapshot)
    {
        ThrowIfDisposed();
        try
        {
            UpdateBounds(snapshot);
            ThrowIfFailed(Commit(_device), "Commit overlay bounds");
            if (!DwmCloakSource())
            {
                return false;
            }

            _ = ShowWindow(_sourceHwnd, SW_SHOWNOACTIVATE);
            _ = ShowWindow(_targetHwnd, SW_SHOWNOACTIVATE);
            _ = SetWindowPos(
                _targetHwnd,
                HwndTopmost,
                snapshot.ScreenLeft - _paddingX,
                snapshot.ScreenTop - _paddingY,
                snapshot.PixelWidth + (_paddingX * 2),
                snapshot.PixelHeight + (_paddingY * 2),
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _isVisible = true;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public bool ShowImmediately(
        OverlaySnapshot snapshot,
        OverlayWindowMotionState state)
    {
        ThrowIfDisposed();
        try
        {
            UpdateBounds(snapshot);
            SetStaticState(state);
            ThrowIfFailed(Commit(_device), "Commit immediate overlay state");
            if (!DwmCloakSource())
            {
                return false;
            }

            _ = ShowWindow(_sourceHwnd, SW_SHOWNOACTIVATE);
            _ = ShowWindow(_targetHwnd, SW_SHOWNOACTIVATE);
            _ = SetWindowPos(
                _targetHwnd,
                HwndTopmost,
                snapshot.ScreenLeft - _paddingX,
                snapshot.ScreenTop - _paddingY,
                snapshot.PixelWidth + (_paddingX * 2),
                snapshot.PixelHeight + (_paddingY * 2),
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _activePlan = null;
            _activeMotionClock = null;
            _opacityOverride = null;
            _isVisible = true;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public bool StartMotion(OverlayWindowMotionPlan motion)
    {
        ThrowIfDisposed();
        nint opacityAnimation = nint.Zero;
        nint scaleAnimation = nint.Zero;
        nint offsetAnimation = nint.Zero;
        try
        {
            opacityAnimation = CreateAnimation(motion.Opacity, static value => value);
            scaleAnimation = CreateAnimation(motion.Scale, static value => value);
            offsetAnimation = CreateAnimation(
                motion.OffsetY,
                value => _paddingY + (value * _dpiScaleY));

            ThrowIfFailed(SetEffectOpacityAnimation(_effectGroup, opacityAnimation), "Animate overlay opacity");
            ThrowIfFailed(SetScaleXAnimation(_scaleTransform, scaleAnimation), "Animate overlay scale X");
            ThrowIfFailed(SetScaleYAnimation(_scaleTransform, scaleAnimation), "Animate overlay scale Y");
            ThrowIfFailed(SetVisualOffsetYAnimation(_visual, offsetAnimation), "Animate overlay offset Y");
            ThrowIfFailed(Commit(_device), "Commit overlay motion");

            _activePlan = motion;
            _activeMotionClock = Stopwatch.StartNew();
            _motionState = OverlayWindowMotion.Evaluate(motion, TimeSpan.Zero);
            _opacityOverride = null;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            ReleaseCom(ref opacityAnimation);
            ReleaseCom(ref scaleAnimation);
            ReleaseCom(ref offsetAnimation);
        }
    }

    public bool SetOpacity(double opacity)
    {
        ThrowIfDisposed();
        try
        {
            OverlayWindowMotionState current = CurrentState;
            double clampedOpacity = Math.Clamp(opacity, 0, 1);
            _opacityOverride = clampedOpacity;
            _motionState = current with { Opacity = clampedOpacity };
            ThrowIfFailed(SetEffectOpacity(_effectGroup, (float)clampedOpacity), "Set overlay opacity");
            ThrowIfFailed(Commit(_device), "Commit overlay opacity");
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public void Hide()
    {
        if (_targetHwnd != nint.Zero)
        {
            _ = ShowWindow(_targetHwnd, SW_HIDE);
        }

        _activePlan = null;
        _activeMotionClock = null;
        _opacityOverride = null;
        _motionState = OverlayWindowMotion.Hidden;
        _isVisible = false;
    }

    public bool DwmCloakSource()
    {
        if (_sourceCloaked)
        {
            return true;
        }

        int cloak = 1;
        int result = DwmSetWindowAttribute(
            _sourceHwnd,
            DWMWA_CLOAK,
            ref cloak,
            Marshal.SizeOf<int>());
        _sourceCloaked = result >= 0;
        return _sourceCloaked;
    }

    public void DwmUncloakSource()
    {
        if (!_sourceCloaked || _sourceHwnd == nint.Zero)
        {
            return;
        }

        int cloak = 0;
        _ = DwmSetWindowAttribute(
            _sourceHwnd,
            DWMWA_CLOAK,
            ref cloak,
            Marshal.SizeOf<int>());
        _sourceCloaked = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
        DwmUncloakSource();
        ReleaseCom(ref _effectGroup);
        ReleaseCom(ref _scaleTransform);
        ReleaseCom(ref _sourceSurface);
        ReleaseCom(ref _visual);
        ReleaseCom(ref _target);
        ReleaseCom(ref _device);
        if (_targetHwnd != nint.Zero)
        {
            if (_previousTargetWindowProcedure != nint.Zero)
            {
                _ = SetWindowLongPtr(_targetHwnd, GWLP_WNDPROC, _previousTargetWindowProcedure);
                _previousTargetWindowProcedure = nint.Zero;
            }

            _ = DestroyWindow(_targetHwnd);
            _targetHwnd = nint.Zero;
        }
    }

    private void Initialize()
    {
        _targetHwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "STATIC",
            string.Empty,
            WS_POPUP,
            0,
            0,
            1,
            1,
            nint.Zero,
            nint.Zero,
            nint.Zero,
            nint.Zero);
        if (_targetHwnd == nint.Zero)
        {
            throw new InvalidOperationException(
                $"Could not create DirectComposition target window: {Marshal.GetLastWin32Error()}");
        }

        Marshal.SetLastPInvokeError(0);
        _previousTargetWindowProcedure = SetWindowLongPtr(
            _targetHwnd,
            GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_targetWindowProcedure));
        if (_previousTargetWindowProcedure == nint.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            throw new InvalidOperationException(
                $"Could not make DirectComposition target input-transparent: {Marshal.GetLastPInvokeError()}");
        }

        Guid iid = IidCompositionDesktopDevice;
        ThrowIfFailed(
            DCompositionCreateDevice2(nint.Zero, ref iid, out _device),
            "Create DirectComposition device");
        ThrowIfFailed(CreateTargetForHwnd(_device, _targetHwnd, true, out _target), "Create composition target");
        ThrowIfFailed(CreateVisual(_device, out _visual), "Create composition visual");
        ThrowIfFailed(CreateSurfaceFromHwnd(_device, _sourceHwnd, out _sourceSurface), "CreateSurfaceFromHwnd");
        ThrowIfFailed(CreateScaleTransform(_device, out _scaleTransform), "Create composition scale transform");
        ThrowIfFailed(CreateEffectGroup(_device, out _effectGroup), "Create composition effect group");

        ThrowIfFailed(SetVisualContent(_visual, _sourceSurface), "Set composition source content");
        ThrowIfFailed(SetVisualTransform(_visual, _scaleTransform), "Set composition scale transform");
        ThrowIfFailed(SetVisualEffect(_visual, _effectGroup), "Set composition opacity effect");
        ThrowIfFailed(SetTargetRoot(_target, _visual), "Set composition root");
        SetStaticState(OverlayWindowMotion.Hidden);
        ThrowIfFailed(Commit(_device), "Commit composition visual tree");
    }

    private nint TargetWindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WM_NCHITTEST)
        {
            return new nint(HTTRANSPARENT);
        }

        return _previousTargetWindowProcedure != nint.Zero
            ? CallWindowProc(_previousTargetWindowProcedure, hwnd, message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static nint SetWindowLongPtr(nint hwnd, int index, nint value) =>
        nint.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new nint(SetWindowLong32(hwnd, index, value.ToInt32()));

    private void UpdateBounds(OverlaySnapshot snapshot)
    {
        _paddingX = Math.Max(1, (int)Math.Ceiling(MotionPadding * snapshot.DpiScaleX));
        _paddingY = Math.Max(1, (int)Math.Ceiling(MotionPadding * snapshot.DpiScaleY));
        _dpiScaleY = snapshot.DpiScaleY;

        ThrowIfFailed(SetVisualOffsetX(_visual, _paddingX), "Set overlay X offset");
        ThrowIfFailed(SetScaleCenterX(_scaleTransform, snapshot.PixelWidth / 2f), "Set scale center X");
        ThrowIfFailed(SetScaleCenterY(_scaleTransform, snapshot.PixelHeight / 2f), "Set scale center Y");
    }

    private void SetStaticState(OverlayWindowMotionState state)
    {
        ThrowIfFailed(SetEffectOpacity(_effectGroup, (float)state.Opacity), "Set overlay opacity");
        ThrowIfFailed(SetScaleX(_scaleTransform, (float)state.Scale), "Set overlay scale X");
        ThrowIfFailed(SetScaleY(_scaleTransform, (float)state.Scale), "Set overlay scale Y");
        ThrowIfFailed(
            SetVisualOffsetY(_visual, (float)(_paddingY + (state.OffsetY * _dpiScaleY))),
            "Set overlay Y offset");
        _motionState = state;
    }

    private nint CreateAnimation(
        OverlayWindowScalarMotion motion,
        Func<double, double> mapValue)
    {
        ThrowIfFailed(CreateCompositionAnimation(_device, out nint animation), "Create composition animation");
        try
        {
            TimeSpan previousTime = TimeSpan.Zero;
            double previousValue = mapValue(motion.Start);
            foreach (OverlayWindowMotionKeyFrame keyFrame in motion.KeyFrames)
            {
                double begin = previousTime.TotalSeconds;
                double duration = Math.Max(0.001, (keyFrame.At - previousTime).TotalSeconds);
                double nextValue = mapValue(keyFrame.Value);
                CubicCoefficients coefficients = CreateCoefficients(
                    previousValue,
                    nextValue,
                    duration,
                    keyFrame.Curve);
                ThrowIfFailed(
                    AddCubic(
                        animation,
                        begin,
                        (float)coefficients.Constant,
                        (float)coefficients.Linear,
                        (float)coefficients.Quadratic,
                        (float)coefficients.Cubic),
                    "Add composition cubic segment");
                previousTime = keyFrame.At;
                previousValue = nextValue;
            }

            ThrowIfFailed(
                EndAnimation(animation, previousTime.TotalSeconds, (float)previousValue),
                "End composition animation");
            return animation;
        }
        catch
        {
            ReleaseCom(ref animation);
            throw;
        }
    }

    private static CubicCoefficients CreateCoefficients(
        double start,
        double end,
        double duration,
        OverlayWindowMotionCurve curve)
    {
        double delta = end - start;
        return curve switch
        {
            OverlayWindowMotionCurve.EaseOutCubic => new CubicCoefficients(
                start,
                (3 * delta) / duration,
                (-3 * delta) / (duration * duration),
                delta / (duration * duration * duration)),
            OverlayWindowMotionCurve.SmoothStep => new CubicCoefficients(
                start,
                0,
                (3 * delta) / (duration * duration),
                (-2 * delta) / (duration * duration * duration)),
            OverlayWindowMotionCurve.EaseInCubic => new CubicCoefficients(
                start,
                0,
                0,
                delta / (duration * duration * duration)),
            _ => new CubicCoefficients(start, delta / duration, 0, 0)
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ThrowIfFailed(int result, string operation)
    {
        if (result < 0)
        {
            throw new COMException(operation, result);
        }
    }

    private static unsafe nint VTableEntry(nint instance, int index)
    {
        return ((nint*)(*(nint*)instance))[index];
    }

    private static unsafe int Commit(nint device) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)VTableEntry(device, 3))(device);

    private static unsafe int CreateVisual(nint device, out nint visual)
    {
        fixed (nint* result = &visual)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTableEntry(device, 6))(device, result);
        }
    }

    private static unsafe int CreateScaleTransform(nint device, out nint transform)
    {
        fixed (nint* result = &transform)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTableEntry(device, 11))(device, result);
        }
    }

    private static unsafe int CreateEffectGroup(nint device, out nint effect)
    {
        fixed (nint* result = &effect)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTableEntry(device, 21))(device, result);
        }
    }

    private static unsafe int CreateCompositionAnimation(nint device, out nint animation)
    {
        fixed (nint* result = &animation)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTableEntry(device, 23))(device, result);
        }
    }

    private static unsafe int CreateTargetForHwnd(
        nint device,
        nint hwnd,
        bool topmost,
        out nint target)
    {
        fixed (nint* result = &target)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)VTableEntry(device, 24))(
                device,
                hwnd,
                topmost ? 1 : 0,
                result);
        }
    }

    private static unsafe int CreateSurfaceFromHwnd(
        nint device,
        nint hwnd,
        out nint surface)
    {
        fixed (nint* result = &surface)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)VTableEntry(device, 26))(
                device,
                hwnd,
                result);
        }
    }

    private static unsafe int SetTargetRoot(nint target, nint visual) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)VTableEntry(target, 3))(target, visual);

    private static unsafe int SetVisualOffsetX(nint visual, float value) =>
        ((delegate* unmanaged[Stdcall]<nint, float, int>)VTableEntry(visual, 4))(visual, value);

    private static unsafe int SetVisualOffsetYAnimation(nint visual, nint animation) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)VTableEntry(visual, 5))(visual, animation);

    private static unsafe int SetVisualOffsetY(nint visual, float value) =>
        ((delegate* unmanaged[Stdcall]<nint, float, int>)VTableEntry(visual, 6))(visual, value);

    private static unsafe int SetVisualTransform(nint visual, nint transform) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)VTableEntry(visual, 7))(visual, transform);

    private static unsafe int SetVisualEffect(nint visual, nint effect) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)VTableEntry(visual, 10))(visual, effect);

    private static unsafe int SetVisualContent(nint visual, nint content) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)VTableEntry(visual, 15))(visual, content);

    private static unsafe int SetScaleXAnimation(nint transform, nint animation) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)VTableEntry(transform, 3))(transform, animation);

    private static unsafe int SetScaleX(nint transform, float value) =>
        ((delegate* unmanaged[Stdcall]<nint, float, int>)VTableEntry(transform, 4))(transform, value);

    private static unsafe int SetScaleYAnimation(nint transform, nint animation) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)VTableEntry(transform, 5))(transform, animation);

    private static unsafe int SetScaleY(nint transform, float value) =>
        ((delegate* unmanaged[Stdcall]<nint, float, int>)VTableEntry(transform, 6))(transform, value);

    private static unsafe int SetScaleCenterX(nint transform, float value) =>
        ((delegate* unmanaged[Stdcall]<nint, float, int>)VTableEntry(transform, 8))(transform, value);

    private static unsafe int SetScaleCenterY(nint transform, float value) =>
        ((delegate* unmanaged[Stdcall]<nint, float, int>)VTableEntry(transform, 10))(transform, value);

    private static unsafe int SetEffectOpacityAnimation(nint effect, nint animation) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)VTableEntry(effect, 3))(effect, animation);

    private static unsafe int SetEffectOpacity(nint effect, float value) =>
        ((delegate* unmanaged[Stdcall]<nint, float, int>)VTableEntry(effect, 4))(effect, value);

    private static unsafe int AddCubic(
        nint animation,
        double begin,
        float constant,
        float linear,
        float quadratic,
        float cubic) =>
        ((delegate* unmanaged[Stdcall]<nint, double, float, float, float, float, int>)VTableEntry(animation, 5))(
            animation,
            begin,
            constant,
            linear,
            quadratic,
            cubic);

    private static unsafe int EndAnimation(nint animation, double end, float value) =>
        ((delegate* unmanaged[Stdcall]<nint, double, float, int>)VTableEntry(animation, 8))(
            animation,
            end,
            value);

    private static unsafe void ReleaseCom(ref nint instance)
    {
        if (instance == nint.Zero)
        {
            return;
        }

        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)VTableEntry(instance, 2))(instance);
        instance = nint.Zero;
    }

    private sealed record CubicCoefficients(
        double Constant,
        double Linear,
        double Quadratic,
        double Cubic);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("dcomp.dll", ExactSpelling = true)]
    private static extern int DCompositionCreateDevice2(
        nint renderingDevice,
        ref Guid iid,
        out nint dcompositionDevice);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmIsCompositionEnabled(out int enabled);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hwnd, int index, int value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CallWindowProc(
        nint previousWindowProcedure,
        nint hwnd,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);
}
