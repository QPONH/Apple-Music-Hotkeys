using System.Windows.Automation;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AppleMusicOverlay.Services;

public enum FavoriteButtonState
{
    Unknown = 0,
    NotFavorite = 1,
    AlreadyFavorite = 2
}

public enum AppleMusicFavoriteResultKind
{
    Added = 0,
    AlreadyFavorite = 1,
    AppleMusicNotFound = 2,
    ButtonNotFound = 3,
    StateUnknown = 4,
    Failed = 5
}

public sealed record FavoriteButtonSnapshot(
    FavoriteButtonState State,
    string? TrackIdentity,
    object? NativeButton = null,
    IntPtr AppleMusicWindowHandle = default);

public sealed record AppleMusicFavoriteStatus(
    FavoriteButtonState State,
    string? TrackIdentity);

public sealed record AppleMusicFavoriteResult(
    AppleMusicFavoriteResultKind Kind,
    string? TrackIdentity = null)
{
    public static AppleMusicFavoriteResult Added(string? trackIdentity) =>
        new(AppleMusicFavoriteResultKind.Added, trackIdentity);

    public static AppleMusicFavoriteResult AlreadyFavorite(string? trackIdentity) =>
        new(AppleMusicFavoriteResultKind.AlreadyFavorite, trackIdentity);
}

public interface IAppleMusicFavoriteAutomation
{
    Task<FavoriteButtonSnapshot> ReadCurrentFavoriteButtonAsync(CancellationToken cancellationToken);

    Task InvokeFavoriteAsync(FavoriteButtonSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed record FavoriteForegroundSnapshot(IntPtr ForegroundWindow);

public interface IAppleMusicFavoriteForegroundGuard
{
    FavoriteForegroundSnapshot CaptureBeforeInvoke();

    Task RestoreAfterInvokeAsync(FavoriteForegroundSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IAppleMusicFavoriteWindowController
{
    Task<bool> PrepareForInvokeAsync(FavoriteButtonSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IAppleMusicWindowSystem
{
    bool IsMinimized(IntPtr windowHandle);

    void MinimizeWithoutActivation(IntPtr windowHandle);

    void ForceMinimize(IntPtr windowHandle);

    IntPtr GetForegroundWindow();

    bool SetForegroundWindow(IntPtr windowHandle);
}

public sealed class AppleMusicFavoriteService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(180);
    private const int DefaultMaxPolls = 32;
    private readonly IAppleMusicFavoriteAutomation _automation;
    private readonly IAppleMusicFavoriteForegroundGuard _foregroundGuard;
    private readonly IAppleMusicFavoriteWindowController _windowController;
    private readonly TimeSpan _pollInterval;
    private readonly int _maxPolls;
    private readonly object _gate = new();
    private Task<AppleMusicFavoriteResult>? _activeRequest;

    public AppleMusicFavoriteService()
        : this(
            new AppleMusicFavoriteAutomation(),
            new AppleMusicFavoriteForegroundGuard(),
            new AppleMusicFavoriteWindowController())
    {
    }

    public AppleMusicFavoriteService(
        IAppleMusicFavoriteAutomation automation,
        TimeSpan? pollInterval = null,
        int maxPolls = DefaultMaxPolls)
        : this(automation, new NoOpAppleMusicFavoriteForegroundGuard(), new NoOpAppleMusicFavoriteWindowController(), pollInterval, maxPolls)
    {
    }

    public AppleMusicFavoriteService(
        IAppleMusicFavoriteAutomation automation,
        IAppleMusicFavoriteForegroundGuard foregroundGuard,
        TimeSpan? pollInterval = null,
        int maxPolls = DefaultMaxPolls)
        : this(automation, foregroundGuard, new NoOpAppleMusicFavoriteWindowController(), pollInterval, maxPolls)
    {
    }

    public AppleMusicFavoriteService(
        IAppleMusicFavoriteAutomation automation,
        IAppleMusicFavoriteForegroundGuard foregroundGuard,
        IAppleMusicFavoriteWindowController windowController,
        TimeSpan? pollInterval = null,
        int maxPolls = DefaultMaxPolls)
    {
        _automation = automation;
        _foregroundGuard = foregroundGuard;
        _windowController = windowController;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _maxPolls = Math.Max(1, maxPolls);
    }

    public Task<AppleMusicFavoriteResult> FavoriteCurrentTrackAsync(CancellationToken cancellationToken = default)
    {
        return StartFavoriteCurrentTrackAsync(stateObserved: null, cancellationToken);
    }

    public Task<AppleMusicFavoriteResult> FavoriteCurrentTrackAsync(
        Action<FavoriteButtonSnapshot> stateObserved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stateObserved);
        return StartFavoriteCurrentTrackAsync(stateObserved, cancellationToken);
    }

    private Task<AppleMusicFavoriteResult> StartFavoriteCurrentTrackAsync(
        Action<FavoriteButtonSnapshot>? stateObserved,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_activeRequest is { IsCompleted: false } active)
            {
                return active;
            }

            _activeRequest = Task.Run(() => FavoriteCurrentTrackCoreAsync(
                    stateObserved,
                    cancellationToken),
                cancellationToken);
            return _activeRequest;
        }
    }

    public async Task<AppleMusicFavoriteStatus> ReadCurrentStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            FavoriteButtonSnapshot snapshot = await Task.Run(
                () => _automation.ReadCurrentFavoriteButtonAsync(cancellationToken),
                cancellationToken);
            return new AppleMusicFavoriteStatus(snapshot.State, snapshot.TrackIdentity);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new AppleMusicFavoriteStatus(FavoriteButtonState.Unknown, null);
        }
    }

    private async Task<AppleMusicFavoriteResult> FavoriteCurrentTrackCoreAsync(
        Action<FavoriteButtonSnapshot>? stateObserved,
        CancellationToken cancellationToken)
    {
        try
        {
            FavoriteButtonSnapshot snapshot = await _automation.ReadCurrentFavoriteButtonAsync(cancellationToken);
            stateObserved?.Invoke(snapshot);
            return snapshot.State switch
            {
                FavoriteButtonState.AlreadyFavorite => AppleMusicFavoriteResult.AlreadyFavorite(snapshot.TrackIdentity),
                FavoriteButtonState.NotFavorite => await InvokeAndWaitForFavoriteAsync(snapshot, cancellationToken),
                _ => new AppleMusicFavoriteResult(AppleMusicFavoriteResultKind.StateUnknown, snapshot.TrackIdentity)
            };
        }
        catch (AppleMusicFavoriteException exception)
        {
            return new AppleMusicFavoriteResult(exception.Kind);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new AppleMusicFavoriteResult(AppleMusicFavoriteResultKind.Failed);
        }
    }

    private async Task<AppleMusicFavoriteResult> InvokeAndWaitForFavoriteAsync(
        FavoriteButtonSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        FavoriteForegroundSnapshot foregroundSnapshot = _foregroundGuard.CaptureBeforeInvoke();
        bool isAppleMusicForeground = snapshot.AppleMusicWindowHandle != IntPtr.Zero &&
                                      foregroundSnapshot.ForegroundWindow == snapshot.AppleMusicWindowHandle;
        if (!isAppleMusicForeground &&
            !await _windowController.PrepareForInvokeAsync(snapshot, cancellationToken))
        {
            return new AppleMusicFavoriteResult(AppleMusicFavoriteResultKind.Failed, snapshot.TrackIdentity);
        }

        if (isAppleMusicForeground)
        {
            foregroundSnapshot = new FavoriteForegroundSnapshot(IntPtr.Zero);
        }

        await _automation.InvokeFavoriteAsync(snapshot, cancellationToken);
        await _foregroundGuard.RestoreAfterInvokeAsync(foregroundSnapshot, cancellationToken);

        for (int attempt = 0; attempt < _maxPolls; attempt++)
        {
            await Task.Delay(_pollInterval, cancellationToken);
            FavoriteButtonSnapshot current;
            try
            {
                current = await _automation.ReadCurrentFavoriteButtonAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (current.State == FavoriteButtonState.AlreadyFavorite &&
                MatchesTrackIdentity(snapshot.TrackIdentity, current.TrackIdentity))
            {
                return AppleMusicFavoriteResult.Added(snapshot.TrackIdentity);
            }
        }

        return new AppleMusicFavoriteResult(AppleMusicFavoriteResultKind.Failed, snapshot.TrackIdentity);
    }

    private static bool MatchesTrackIdentity(string? expected, string? actual)
    {
        return !string.IsNullOrWhiteSpace(expected) &&
               !string.IsNullOrWhiteSpace(actual) &&
               string.Equals(expected.Trim(), actual.Trim(), StringComparison.Ordinal);
    }
}

public sealed class AppleMusicFavoriteWindowController : IAppleMusicFavoriteWindowController
{
    private readonly IAppleMusicWindowSystem _windows;
    private readonly TimeSpan _minimizeTimeout;
    private readonly TimeSpan _minimizePollInterval;

    public AppleMusicFavoriteWindowController()
        : this(new AppleMusicWindowSystem())
    {
    }

    public AppleMusicFavoriteWindowController(
        IAppleMusicWindowSystem windows,
        TimeSpan? minimizeTimeout = null,
        TimeSpan? minimizePollInterval = null)
    {
        _windows = windows;
        _minimizeTimeout = minimizeTimeout ?? TimeSpan.FromMilliseconds(300);
        _minimizePollInterval = minimizePollInterval ?? TimeSpan.FromMilliseconds(15);
    }

    public async Task<bool> PrepareForInvokeAsync(FavoriteButtonSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr windowHandle = snapshot.AppleMusicWindowHandle;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (_windows.IsMinimized(windowHandle))
        {
            return true;
        }

        _windows.MinimizeWithoutActivation(windowHandle);
        if (await WaitForMinimizedAsync(windowHandle, cancellationToken))
        {
            return true;
        }

        _windows.ForceMinimize(windowHandle);
        return await WaitForMinimizedAsync(windowHandle, cancellationToken);
    }

    private async Task<bool> WaitForMinimizedAsync(IntPtr windowHandle, CancellationToken cancellationToken)
    {
        if (_windows.IsMinimized(windowHandle))
        {
            return true;
        }

        long timeoutTicks = Stopwatch.GetTimestamp() + (long)(_minimizeTimeout.TotalSeconds * Stopwatch.Frequency);
        TimeSpan pollInterval = _minimizePollInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(1)
            : _minimizePollInterval;

        while (Stopwatch.GetTimestamp() < timeoutTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(pollInterval, cancellationToken);
            if (_windows.IsMinimized(windowHandle))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class AppleMusicWindowSystem : IAppleMusicWindowSystem
{
    private const int SwShowMinNoActive = 7;
    private const int SwForceMinimize = 11;

    public bool IsMinimized(IntPtr windowHandle)
    {
        return IsIconic(windowHandle);
    }

    public void MinimizeWithoutActivation(IntPtr windowHandle)
    {
        ShowWindowAsync(windowHandle, SwShowMinNoActive);
    }

    public void ForceMinimize(IntPtr windowHandle)
    {
        ShowWindowAsync(windowHandle, SwForceMinimize);
    }

    public IntPtr GetForegroundWindow()
    {
        return GetForegroundWindowNative();
    }

    public bool SetForegroundWindow(IntPtr windowHandle)
    {
        return SetForegroundWindowNative(windowHandle);
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool SetForegroundWindowNative(IntPtr hWnd);
}

public sealed class AppleMusicFavoriteForegroundGuard : IAppleMusicFavoriteForegroundGuard
{
    private readonly IAppleMusicWindowSystem _windows;
    private readonly TimeSpan _followUpRestoreDelay;

    public AppleMusicFavoriteForegroundGuard()
        : this(new AppleMusicWindowSystem())
    {
    }

    public AppleMusicFavoriteForegroundGuard(
        IAppleMusicWindowSystem windows,
        TimeSpan? followUpRestoreDelay = null)
    {
        _windows = windows;
        _followUpRestoreDelay = followUpRestoreDelay ?? TimeSpan.FromMilliseconds(60);
    }

    public FavoriteForegroundSnapshot CaptureBeforeInvoke()
    {
        return new FavoriteForegroundSnapshot(_windows.GetForegroundWindow());
    }

    public async Task RestoreAfterInvokeAsync(FavoriteForegroundSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.ForegroundWindow == IntPtr.Zero)
        {
            return;
        }

        RestoreForegroundIfNeeded(snapshot.ForegroundWindow);
        if (_followUpRestoreDelay > TimeSpan.Zero)
        {
            await Task.Delay(_followUpRestoreDelay, cancellationToken);
        }

        RestoreForegroundIfNeeded(snapshot.ForegroundWindow);
    }

    private void RestoreForegroundIfNeeded(IntPtr foregroundWindow)
    {
        IntPtr currentForeground = _windows.GetForegroundWindow();
        if (currentForeground != IntPtr.Zero && currentForeground != foregroundWindow)
        {
            _windows.SetForegroundWindow(foregroundWindow);
#if DEBUG
            WriteDebugLog($"Restored foreground from 0x{currentForeground.ToInt64():X} to 0x{foregroundWindow.ToInt64():X}.");
#endif
        }
    }

#if DEBUG
    private static void WriteDebugLog(string message)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "musicfloat-favorite-focus.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            Debug.WriteLine(message);
        }
    }
#endif

}

public sealed class NoOpAppleMusicFavoriteForegroundGuard : IAppleMusicFavoriteForegroundGuard
{
    public FavoriteForegroundSnapshot CaptureBeforeInvoke()
    {
        return new FavoriteForegroundSnapshot(IntPtr.Zero);
    }

    public Task RestoreAfterInvokeAsync(FavoriteForegroundSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class NoOpAppleMusicFavoriteWindowController : IAppleMusicFavoriteWindowController
{
    public Task<bool> PrepareForInvokeAsync(FavoriteButtonSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}

public sealed class AppleMusicFavoriteException : Exception
{
    public AppleMusicFavoriteException(AppleMusicFavoriteResultKind kind, string? message = null, Exception? innerException = null)
        : base(message ?? kind.ToString(), innerException)
    {
        Kind = kind;
    }

    public AppleMusicFavoriteResultKind Kind { get; }
}

public sealed class AppleMusicFavoriteAutomation : IAppleMusicFavoriteAutomation
{
    private const string AppleMusicWindowName = "Apple Music";
    private const string AppleMusicWindowClassName = "WinUIDesktopWin32WindowClass";
    private const string CurrentTrackAutomationId = "LCD";
    private static readonly string[] FavoriteButtonNames = ["喜爱", "Favorite", "Favourite"];

    public Task<FavoriteButtonSnapshot> ReadCurrentFavoriteButtonAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FoundFavoriteButton found = FindCurrentTrackFavoriteButton(cancellationToken);
        AutomationElement button = found.Button;
        string codepoints = ReadFirstRawChildCodepoints(button);
        FavoriteButtonState state = InterpretGlyphCodepoints(codepoints);
        string? trackIdentity = TryGetCurrentTrackIdentity(button);
        IntPtr windowHandle = new(found.Window.Current.NativeWindowHandle);
        return Task.FromResult(new FavoriteButtonSnapshot(state, trackIdentity, button, windowHandle));
    }

    public Task InvokeFavoriteAsync(FavoriteButtonSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.NativeButton is not AutomationElement button)
        {
            button = FindCurrentTrackFavoriteButton(cancellationToken).Button;
        }

        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern) || pattern is not InvokePattern invokePattern)
        {
            throw new AppleMusicFavoriteException(AppleMusicFavoriteResultKind.ButtonNotFound, "Favorite button has no InvokePattern.");
        }

        invokePattern.Invoke();
        return Task.CompletedTask;
    }

    public static FavoriteButtonState InterpretGlyphCodepoints(string? codepoints)
    {
        return codepoints switch
        {
            "U+DBC0 U+DEC2" => FavoriteButtonState.NotFavorite,
            "U+DBC0 U+DEC3" => FavoriteButtonState.AlreadyFavorite,
            _ => FavoriteButtonState.Unknown
        };
    }

    private sealed record FoundFavoriteButton(AutomationElement Window, AutomationElement Button);

    private static FoundFavoriteButton FindCurrentTrackFavoriteButton(CancellationToken cancellationToken)
    {
        AutomationElement appleMusicWindow = FindAppleMusicWindow()
            ?? throw new AppleMusicFavoriteException(AppleMusicFavoriteResultKind.AppleMusicNotFound);
        cancellationToken.ThrowIfCancellationRequested();

        List<AutomationElement> lcdElements = FindDescendants(appleMusicWindow)
            .Where(element => string.Equals(element.Current.AutomationId, CurrentTrackAutomationId, StringComparison.Ordinal))
            .ToList();
        if (lcdElements.Count != 1)
        {
            throw new AppleMusicFavoriteException(AppleMusicFavoriteResultKind.ButtonNotFound, $"Expected one LCD element, got {lcdElements.Count}.");
        }

        AutomationElement lcd = lcdElements[0];
        var walker = TreeWalker.RawViewWalker;
        List<AutomationElement> favoriteButtons = [];
        for (AutomationElement? child = walker.GetFirstChild(lcd); child != null; child = walker.GetNextSibling(child))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AutomationElement.AutomationElementInformation current = child.Current;
            if (current.ControlType == ControlType.Button &&
                FavoriteButtonNames.Any(name => string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                favoriteButtons.Add(child);
            }
        }

        if (favoriteButtons.Count != 1)
        {
            throw new AppleMusicFavoriteException(AppleMusicFavoriteResultKind.ButtonNotFound, $"Expected one LCD favorite button, got {favoriteButtons.Count}.");
        }

        return new FoundFavoriteButton(appleMusicWindow, favoriteButtons[0]);
    }

    private static AutomationElement? FindAppleMusicWindow()
    {
        AutomationElementCollection windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        for (int i = 0; i < windows.Count; i++)
        {
            AutomationElement window = windows[i];
            AutomationElement.AutomationElementInformation current = window.Current;
            if (string.Equals(current.Name, AppleMusicWindowName, StringComparison.Ordinal) &&
                string.Equals(current.ClassName, AppleMusicWindowClassName, StringComparison.Ordinal))
            {
                return window;
            }
        }

        return null;
    }

    private static IEnumerable<AutomationElement> FindDescendants(AutomationElement root)
    {
        AutomationElementCollection descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        for (int i = 0; i < descendants.Count; i++)
        {
            yield return descendants[i];
        }
    }

    private static string ReadFirstRawChildCodepoints(AutomationElement button)
    {
        AutomationElement? child = TreeWalker.RawViewWalker.GetFirstChild(button);
        return child == null ? string.Empty : ToCodepoints(child.Current.Name);
    }

    private static string? TryGetCurrentTrackIdentity(AutomationElement button)
    {
        AutomationElement? parent = TreeWalker.RawViewWalker.GetParent(button);
        string name = parent?.Current.Name ?? string.Empty;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string ToCodepoints(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Join(" ", value.Select(character => $"U+{(int)character:X4}"));
    }
}
