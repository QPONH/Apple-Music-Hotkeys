using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class AppleMusicFavoriteServiceTests
{
    [Fact]
    public async Task ReadCurrentStatusAsyncReadsStateWithoutInvokingFavorite()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "Song - Artist"));
        var service = new AppleMusicFavoriteService(automation, TimeSpan.FromMilliseconds(1), 3);

        AppleMusicFavoriteStatus status = await service.ReadCurrentStatusAsync();

        Assert.Equal(FavoriteButtonState.AlreadyFavorite, status.State);
        Assert.Equal("Song - Artist", status.TrackIdentity);
        Assert.Equal(0, automation.InvokeCount);
    }

    [Fact]
    public async Task ReadCurrentStatusAsyncFailsClosedWhenAutomationThrows()
    {
        var service = new AppleMusicFavoriteService(new ThrowingFavoriteAutomation());

        AppleMusicFavoriteStatus status = await service.ReadCurrentStatusAsync();

        Assert.Equal(FavoriteButtonState.Unknown, status.State);
        Assert.Null(status.TrackIdentity);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncInvokesOnlyWhenTrackIsNotFavorite()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a"),
            new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a"));
        var service = new AppleMusicFavoriteService(automation, TimeSpan.FromMilliseconds(1), 3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.Added, result.Kind);
        Assert.Equal(1, automation.InvokeCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncReportsObservedStateBeforeInvoke()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a"),
            new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a"));
        var service = new AppleMusicFavoriteService(automation, TimeSpan.FromMilliseconds(1), 3);
        FavoriteButtonState? observedState = null;
        bool observedBeforeInvoke = false;

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync(snapshot =>
        {
            observedState = snapshot.State;
            observedBeforeInvoke = automation.InvokeCount == 0;
        });

        Assert.Equal(AppleMusicFavoriteResultKind.Added, result.Kind);
        Assert.Equal(FavoriteButtonState.NotFavorite, observedState);
        Assert.True(observedBeforeInvoke);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncDoesNotInvokeWhenTrackIsAlreadyFavorite()
    {
        var automation = new FakeFavoriteAutomation(new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a"));
        var service = new AppleMusicFavoriteService(automation, TimeSpan.FromMilliseconds(1), 3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.AlreadyFavorite, result.Kind);
        Assert.Equal(0, automation.InvokeCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncFailsWhenStateIsUnknown()
    {
        var automation = new FakeFavoriteAutomation(new FavoriteButtonSnapshot(FavoriteButtonState.Unknown, "track-a"));
        var service = new AppleMusicFavoriteService(automation, TimeSpan.FromMilliseconds(1), 3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.StateUnknown, result.Kind);
        Assert.Equal(0, automation.InvokeCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncFailsWhenInvokeDoesNotBecomeFavorite()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a"),
            new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a"),
            new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a"));
        var service = new AppleMusicFavoriteService(automation, TimeSpan.FromMilliseconds(1), 2);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.Failed, result.Kind);
        Assert.Equal(1, automation.InvokeCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncKeepsPollingThroughTransientUnknownState()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a"),
            new FavoriteButtonSnapshot(FavoriteButtonState.Unknown, "track-a"),
            new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a"));
        var service = new AppleMusicFavoriteService(automation, TimeSpan.FromMilliseconds(1), 3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.Added, result.Kind);
        Assert.Equal(1, automation.InvokeCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncKeepsPollingThroughTransientReadFailure()
    {
        var automation = new TransientPostInvokeFailureAutomation();
        var service = new AppleMusicFavoriteService(automation, TimeSpan.FromMilliseconds(1), 3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.Added, result.Kind);
        Assert.Equal(1, automation.InvokeCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncDoesNotTreatAnotherTracksFavoriteStateAsSuccess()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a"),
            new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-b"),
            new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-b"));
        var service = new AppleMusicFavoriteService(automation, TimeSpan.FromMilliseconds(1), 2);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.Failed, result.Kind);
        Assert.Equal("track-a", result.TrackIdentity);
        Assert.Equal(1, automation.InvokeCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncCoalescesConcurrentRequests()
    {
        var automation = new BlockingFavoriteAutomation();
        var service = new AppleMusicFavoriteService(automation, TimeSpan.FromMilliseconds(1), 3);

        Task<AppleMusicFavoriteResult> first = service.FavoriteCurrentTrackAsync();
        Task<AppleMusicFavoriteResult> second = service.FavoriteCurrentTrackAsync();
        automation.AllowInvokeToComplete();

        AppleMusicFavoriteResult[] results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(AppleMusicFavoriteResultKind.Added, result.Kind));
        Assert.Equal(1, automation.InvokeCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncRestoresForegroundWhenAppleMusicTakesFocus()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a"),
            new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a"));
        var foreground = new FakeFavoriteForegroundGuard(
            originalForeground: new IntPtr(10),
            foregroundAfterInvoke: new IntPtr(20));
        var service = new AppleMusicFavoriteService(automation, foreground, TimeSpan.FromMilliseconds(1), 3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.Added, result.Kind);
        Assert.Equal(new IntPtr(10), foreground.RestoredForeground);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncPreparesAppleMusicWindowBeforeInvoking()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a", NativeButton: "button-a"),
            new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a", NativeButton: "button-a"));
        var windowController = new FakeFavoriteWindowController();
        var service = new AppleMusicFavoriteService(
            automation,
            new NoOpAppleMusicFavoriteForegroundGuard(),
            windowController,
            TimeSpan.FromMilliseconds(1),
            3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.Added, result.Kind);
        Assert.Equal(1, windowController.PrepareCount);
        Assert.Equal("button-a", windowController.PreparedNativeButton);
        Assert.True(windowController.PreparedBeforeInvoke);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncDoesNotPrepareWindowWhenTrackIsAlreadyFavorite()
    {
        var automation = new FakeFavoriteAutomation(new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a"));
        var windowController = new FakeFavoriteWindowController();
        var service = new AppleMusicFavoriteService(
            automation,
            new NoOpAppleMusicFavoriteForegroundGuard(),
            windowController,
            TimeSpan.FromMilliseconds(1),
            3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.AlreadyFavorite, result.Kind);
        Assert.Equal(0, windowController.PrepareCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncDoesNotInvokeWhenAppleMusicCannotBeMinimized()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(
                FavoriteButtonState.NotFavorite,
                "track-a",
                NativeButton: "button-a",
                AppleMusicWindowHandle: new IntPtr(30)));
        var windowController = new FakeFavoriteWindowController(preparationSucceeded: false);
        var service = new AppleMusicFavoriteService(
            automation,
            new NoOpAppleMusicFavoriteForegroundGuard(),
            windowController,
            TimeSpan.FromMilliseconds(1),
            3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.Failed, result.Kind);
        Assert.Equal(0, automation.InvokeCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncDoesNotRestoreAppleMusicWhenItWasForeground()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(
                FavoriteButtonState.NotFavorite,
                "track-a",
                AppleMusicWindowHandle: new IntPtr(30)),
            new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a"));
        var foreground = new RecordingFavoriteForegroundGuard(new IntPtr(30));
        var windowController = new FakeFavoriteWindowController();
        var service = new AppleMusicFavoriteService(
            automation,
            foreground,
            windowController,
            TimeSpan.FromMilliseconds(1),
            3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.Added, result.Kind);
        Assert.Equal(0, windowController.PrepareCount);
        Assert.Equal(IntPtr.Zero, foreground.RestoreSnapshot?.ForegroundWindow);
    }

    [Fact]
    public async Task WindowControllerDoesNothingWhenAppleMusicIsAlreadyMinimized()
    {
        var windows = new FakeAppleMusicWindowSystem(isMinimized: true);
        var controller = new AppleMusicFavoriteWindowController(
            windows,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.Zero);
        var snapshot = new FavoriteButtonSnapshot(
            FavoriteButtonState.NotFavorite,
            "track-a",
            AppleMusicWindowHandle: new IntPtr(30));

        bool prepared = await controller.PrepareForInvokeAsync(snapshot, CancellationToken.None);

        Assert.True(prepared);
        Assert.Equal(0, windows.MinimizeWithoutActivationCount);
        Assert.Equal(0, windows.ForceMinimizeCount);
    }

    [Fact]
    public async Task WindowControllerMinimizesVisibleAppleMusicBeforeInvoke()
    {
        var windows = new FakeAppleMusicWindowSystem(isMinimized: false)
        {
            MinimizeWithoutActivationSucceeds = true
        };
        var controller = new AppleMusicFavoriteWindowController(
            windows,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero);
        var snapshot = new FavoriteButtonSnapshot(
            FavoriteButtonState.NotFavorite,
            "track-a",
            AppleMusicWindowHandle: new IntPtr(30));

        bool prepared = await controller.PrepareForInvokeAsync(snapshot, CancellationToken.None);

        Assert.True(prepared);
        Assert.Equal(new IntPtr(30), windows.LastWindowHandle);
        Assert.Equal(1, windows.MinimizeWithoutActivationCount);
        Assert.Equal(0, windows.ForceMinimizeCount);
    }

    [Fact]
    public async Task WindowControllerFailsClosedWhenAppleMusicCannotBeMinimized()
    {
        var windows = new FakeAppleMusicWindowSystem(isMinimized: false);
        var controller = new AppleMusicFavoriteWindowController(
            windows,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.Zero);
        var snapshot = new FavoriteButtonSnapshot(
            FavoriteButtonState.NotFavorite,
            "track-a",
            AppleMusicWindowHandle: new IntPtr(30));

        bool prepared = await controller.PrepareForInvokeAsync(snapshot, CancellationToken.None);

        Assert.False(prepared);
        Assert.Equal(1, windows.MinimizeWithoutActivationCount);
        Assert.Equal(1, windows.ForceMinimizeCount);
    }

    [Fact]
    public async Task ForegroundGuardDoesNotRestoreWhenForegroundIsTemporarilyUnknown()
    {
        var windows = new FakeAppleMusicWindowSystem(isMinimized: true);
        windows.EnqueueForeground(IntPtr.Zero);
        windows.EnqueueForeground(new IntPtr(10));
        var guard = new AppleMusicFavoriteForegroundGuard(windows, TimeSpan.Zero);

        await guard.RestoreAfterInvokeAsync(
            new FavoriteForegroundSnapshot(new IntPtr(10)),
            CancellationToken.None);

        Assert.Equal(0, windows.SetForegroundCount);
    }

    [Fact]
    public async Task FavoriteCurrentTrackAsyncDoesNotRestoreForegroundWhenFocusDoesNotChange()
    {
        var automation = new FakeFavoriteAutomation(
            new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a"),
            new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a"));
        var foreground = new FakeFavoriteForegroundGuard(
            originalForeground: new IntPtr(10),
            foregroundAfterInvoke: new IntPtr(10));
        var service = new AppleMusicFavoriteService(automation, foreground, TimeSpan.FromMilliseconds(1), 3);

        AppleMusicFavoriteResult result = await service.FavoriteCurrentTrackAsync();

        Assert.Equal(AppleMusicFavoriteResultKind.Added, result.Kind);
        Assert.Null(foreground.RestoredForeground);
    }

    [Theory]
    [InlineData("U+DBC0 U+DEC2", FavoriteButtonState.NotFavorite)]
    [InlineData("U+DBC0 U+DEC3", FavoriteButtonState.AlreadyFavorite)]
    [InlineData("U+E006", FavoriteButtonState.Unknown)]
    [InlineData("", FavoriteButtonState.Unknown)]
    public void InterpretGlyphCodepointsMapsKnownAppleMusicFavoriteGlyphs(string codepoints, FavoriteButtonState expected)
    {
        Assert.Equal(expected, AppleMusicFavoriteAutomation.InterpretGlyphCodepoints(codepoints));
    }

    private sealed class FakeFavoriteAutomation(params FavoriteButtonSnapshot[] snapshots) : IAppleMusicFavoriteAutomation
    {
        private readonly Queue<FavoriteButtonSnapshot> _snapshots = new(snapshots);

        public int InvokeCount { get; private set; }

        public Task<FavoriteButtonSnapshot> ReadCurrentFavoriteButtonAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshots.Count == 0
                ? new FavoriteButtonSnapshot(FavoriteButtonState.Unknown, null)
                : _snapshots.Dequeue());
        }

        public Task InvokeFavoriteAsync(FavoriteButtonSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvokeCount++;
            FakeFavoriteWindowController.NotifyInvokeStarted();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingFavoriteAutomation : IAppleMusicFavoriteAutomation
    {
        public Task<FavoriteButtonSnapshot> ReadCurrentFavoriteButtonAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AppleMusicFavoriteException(AppleMusicFavoriteResultKind.ButtonNotFound);
        }

        public Task InvokeFavoriteAsync(FavoriteButtonSnapshot snapshot, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Read-only status lookup must not invoke Favorite.");
        }
    }

    private sealed class TransientPostInvokeFailureAutomation : IAppleMusicFavoriteAutomation
    {
        private int _readCount;

        public int InvokeCount { get; private set; }

        public Task<FavoriteButtonSnapshot> ReadCurrentFavoriteButtonAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _readCount++;
            return _readCount switch
            {
                1 => Task.FromResult(new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a")),
                2 => throw new AppleMusicFavoriteException(AppleMusicFavoriteResultKind.ButtonNotFound),
                _ => Task.FromResult(new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a"))
            };
        }

        public Task InvokeFavoriteAsync(FavoriteButtonSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvokeCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFavoriteWindowController : IAppleMusicFavoriteWindowController
    {
        private static int _invokeStarted;
        private readonly bool _preparationSucceeded;

        public FakeFavoriteWindowController(bool preparationSucceeded = true)
        {
            _preparationSucceeded = preparationSucceeded;
            Interlocked.Exchange(ref _invokeStarted, 0);
        }

        public int PrepareCount { get; private set; }

        public object? PreparedNativeButton { get; private set; }

        public bool PreparedBeforeInvoke { get; private set; }

        public static void NotifyInvokeStarted()
        {
            Interlocked.Exchange(ref _invokeStarted, 1);
        }

        public Task<bool> PrepareForInvokeAsync(FavoriteButtonSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCount++;
            PreparedNativeButton = snapshot.NativeButton;
            PreparedBeforeInvoke = Interlocked.CompareExchange(ref _invokeStarted, 0, 0) == 0;
            return Task.FromResult(_preparationSucceeded);
        }
    }

    private sealed class RecordingFavoriteForegroundGuard(IntPtr foregroundWindow) : IAppleMusicFavoriteForegroundGuard
    {
        public FavoriteForegroundSnapshot? RestoreSnapshot { get; private set; }

        public FavoriteForegroundSnapshot CaptureBeforeInvoke()
        {
            return new FavoriteForegroundSnapshot(foregroundWindow);
        }

        public Task RestoreAfterInvokeAsync(FavoriteForegroundSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreSnapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAppleMusicWindowSystem(bool isMinimized) : IAppleMusicWindowSystem
    {
        private readonly Queue<IntPtr> _foregroundWindows = new();
        private bool _isMinimized = isMinimized;

        public bool MinimizeWithoutActivationSucceeds { get; init; }

        public bool ForceMinimizeSucceeds { get; init; }

        public int MinimizeWithoutActivationCount { get; private set; }

        public int ForceMinimizeCount { get; private set; }

        public int SetForegroundCount { get; private set; }

        public IntPtr LastWindowHandle { get; private set; }

        public bool IsMinimized(IntPtr windowHandle)
        {
            LastWindowHandle = windowHandle;
            return _isMinimized;
        }

        public void MinimizeWithoutActivation(IntPtr windowHandle)
        {
            LastWindowHandle = windowHandle;
            MinimizeWithoutActivationCount++;
            _isMinimized = MinimizeWithoutActivationSucceeds;
        }

        public void ForceMinimize(IntPtr windowHandle)
        {
            LastWindowHandle = windowHandle;
            ForceMinimizeCount++;
            _isMinimized = ForceMinimizeSucceeds;
        }

        public IntPtr GetForegroundWindow()
        {
            return _foregroundWindows.Count == 0 ? IntPtr.Zero : _foregroundWindows.Dequeue();
        }

        public bool SetForegroundWindow(IntPtr windowHandle)
        {
            SetForegroundCount++;
            return true;
        }

        public void EnqueueForeground(IntPtr windowHandle)
        {
            _foregroundWindows.Enqueue(windowHandle);
        }
    }

    private sealed class FakeFavoriteForegroundGuard(IntPtr originalForeground, IntPtr foregroundAfterInvoke) : IAppleMusicFavoriteForegroundGuard
    {
        public IntPtr? RestoredForeground { get; private set; }

        public FavoriteForegroundSnapshot CaptureBeforeInvoke()
        {
            return new FavoriteForegroundSnapshot(originalForeground);
        }

        public Task RestoreAfterInvokeAsync(FavoriteForegroundSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (foregroundAfterInvoke != snapshot.ForegroundWindow)
            {
                RestoredForeground = snapshot.ForegroundWindow;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class BlockingFavoriteAutomation : IAppleMusicFavoriteAutomation
    {
        private readonly TaskCompletionSource _invokeCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _reads;

        public int InvokeCount { get; private set; }

        public Task<FavoriteButtonSnapshot> ReadCurrentFavoriteButtonAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            return Task.FromResult(_reads == 1
                ? new FavoriteButtonSnapshot(FavoriteButtonState.NotFavorite, "track-a")
                : new FavoriteButtonSnapshot(FavoriteButtonState.AlreadyFavorite, "track-a"));
        }

        public async Task InvokeFavoriteAsync(FavoriteButtonSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvokeCount++;
            await _invokeCompletion.Task.WaitAsync(cancellationToken);
        }

        public void AllowInvokeToComplete()
        {
            _invokeCompletion.TrySetResult();
        }
    }
}
