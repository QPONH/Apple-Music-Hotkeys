using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Views;

public sealed class LyricsWindow : Window
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly LyricsService _service;
    private readonly OverlaySettings _settings;
    private readonly OverlaySettingsService _settingsService = new();
    // Line changes must feel immediate: a 350 ms poll on top of SMTC's one-second timeline
    // granularity made the lyrics visibly late. The timeline is now read at ~8 Hz (the position is
    // interpolated in SmtcMediaSessionService) and the tick runs at render priority so a busy UI
    // thread cannot delay it.
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(120) };
    // Topmost is only a one-shot z-order flag: another topmost window that appears later (or the
    // player itself) ends up above the overlay. Re-asserting it while visible keeps it on top.
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _boundsSaveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private string _trackKey = string.Empty;
    private string _titleKey = string.Empty;
    private string _artistKey = string.Empty;
    private DateTime _trackSeenUtc = DateTime.MinValue;
    private string _renderSignature = string.Empty;
    private LyricsLookupStatus? _lookupStatus;
    private bool _lookupRunning;
    private PlaybackSnapshot? _lastPlayback;
    private DateTime _lastPlaybackUtc = DateTime.MinValue;
    private int _generation;
    private IntPtr _hwnd;
    private bool _menuOpen;
    private bool _updating;
    private bool _forceRefresh;
    private CancellationTokenSource? _lookupCancellation;
    private string _prefetchAttemptedTrackKey = string.Empty;
    // The artist changed after lyrics were already shown: re-check in the background.
    private bool _correctionPending;
    // Watermark used to keep the displayed line from stepping backwards for one tick.
    private int _lastLineIndex = -1;
    private TimeSpan _lastLinePosition;
    // "无法连接歌词服务器" retries itself; the manual button only makes it immediate.
    // Initial lookup plus three fast retries. Each network attempt is capped at 3.25 s,
    // leaving room for UI timer ticks while completing the entire retry cycle inside 15 s.
    private const int MaxAutomaticRetries = 3;
    private static readonly TimeSpan LookupAttemptTimeout = TimeSpan.FromMilliseconds(3250);
    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(15);
    private int _retryAttempt;
    private DateTime _nextRetryUtc = DateTime.MaxValue;
    private DateTime _retryDeadlineUtc = DateTime.MaxValue;

    public LyricsWindow(LyricsService service, OverlaySettings settings, Action? openSettings = null, Action? exitApplication = null)
    {
        _service = service; _settings = settings;
        Width = Math.Max(180, _settings.LyricsWindowWidth); Height = Math.Max(70, _settings.LyricsWindowHeight);
        MinWidth = 180; MinHeight = 70; Topmost = true; ShowInTaskbar = false; AllowsTransparency = true;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; Background = Brushes.Transparent; ShowActivated = false;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(8), GlassFrameThickness = new Thickness(0) });
        // Restore the saved position only when it would still be visible. A saved position from a
        // different monitor layout used to put the overlay completely off-screen.
        bool restoreBounds = _settings.LyricsWindowLeft.HasValue && _settings.LyricsWindowTop.HasValue
            && IsOnScreen(_settings.LyricsWindowLeft.Value, _settings.LyricsWindowTop.Value, Width, Height);
        WindowStartupLocation = restoreBounds ? WindowStartupLocation.Manual : WindowStartupLocation.CenterScreen;
        if (restoreBounds)
        {
            Left = _settings.LyricsWindowLeft!.Value;
            Top = _settings.LyricsWindowTop!.Value;
        }
        SourceInitialized += (_, _) => OnSourceInitialized();
        PreviewMouseLeftButtonDown += DragAnywhere;
        Loaded += async (_, _) => { _timer.Start(); _topmostTimer.Start(); ApplyClickThrough(); EnsureTopmost(); await UpdateAsync(); };
        _boundsSaveTimer.Tick += (_, _) =>
        {
            _boundsSaveTimer.Stop();
            SaveBoundsCore();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _topmostTimer.Stop();
            _boundsSaveTimer.Stop();
            _lifetimeCancellation.Cancel();
            CancelCurrentLookup();
            SaveBoundsCore();
        };
        IsVisibleChanged += (_, _) => EnsureTopmost();
        Activated += (_, _) => EnsureTopmost();
        Deactivated += (_, _) => EnsureTopmost();
        SizeChanged += (_, _) => SaveBounds();
        LocationChanged += (_, _) => SaveBounds();
        _timer.Tick += async (_, _) => await UpdateAsync();
        _topmostTimer.Tick += (_, _) => EnsureTopmost(forceRaise: true);
        // The overlay's own context menu is a popup window; raising the overlay while it is open
        // would draw over it, so the topmost keeper pauses for the menu's lifetime.
        ContextMenuOpening += (_, _) => _menuOpen = true;
        // The lyrics overlay is often the only visible window (the settings window can be
        // hidden at startup), so give it its own entry point in case the tray icon is missing.
        BuildContextMenu(openSettings, exitApplication);
        Render("正在读取同步歌词……", "");
    }

    private void BuildContextMenu(Action? openSettings, Action? exitApplication)
    {
        var menu = new ContextMenu();
        if (openSettings is not null)
            menu.Items.Add(new MenuItem { Header = "打开设置", Command = new RelayCommand(openSettings) });
        menu.Items.Add(new MenuItem { Header = "重新查找歌词", Command = new RelayCommand(ForceReload) });
        if (exitApplication is not null)
            menu.Items.Add(new MenuItem { Header = "退出 MusicFloat", Command = new RelayCommand(exitApplication) });
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; EnsureTopmost(); };
        ContextMenu = menu;
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _action;
        public RelayCommand(Action action) => _action = action;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }

    private async Task UpdateAsync()
    {
        // A slow SMTC call can outlive the 120 ms timer; never run two updates at once.
        if (_updating) return;
        _updating = true;
        try
        {
            var playback = await _service.GetCurrentPlaybackAsync(_lifetimeCancellation.Token);
            if (playback is null)
            {
                // SMTC drops the Apple Music session for a moment during track changes and RPC
                // hiccups. Wiping the lyric immediately made a playing song look unrecognized.
                if (_lastPlayback is not null && DateTime.UtcNow - _lastPlaybackUtc < TimeSpan.FromSeconds(6))
                {
                    TimeSpan age = DateTime.UtcNow - _lastPlaybackUtc;
                    TimeSpan position = _lastPlayback.IsPlaying ? _lastPlayback.Position + age : _lastPlayback.Position;
                    if (_lastPlayback.Duration > TimeSpan.Zero && position > _lastPlayback.Duration) position = _lastPlayback.Duration;
                    playback = _lastPlayback with { Position = position };
                }
                else
                {
                    Render("未识别到正在播放的 Apple Music 歌曲", "");
                    return;
                }
            }
            else
            {
                _lastPlayback = playback;
                _lastPlaybackUtc = DateTime.UtcNow;
            }

            string title = playback.Title.Trim();
            string artist = playback.Artist.Trim();
            if (!string.Equals(title, _titleKey, StringComparison.Ordinal))
            {
                _titleKey = title;
                _artistKey = artist;
                _trackSeenUtc = DateTime.UtcNow;
                _trackKey = title + "\n" + artist;
                ResetLookupState();
            }
            else if (!string.Equals(artist, _artistKey, StringComparison.Ordinal))
            {
                // Apple Music often reports the new title one tick before the new artist. A lookup
                // started with the previous artist finds a different song's lyrics, so any artist
                // change re-runs the lookup once: immediately when nothing is on screen yet, and in
                // the background (keeping the current text visible) when lyrics are already shown.
                _artistKey = artist;
                _trackKey = title + "\n" + artist;
                _lookupRunning = false;
                _generation++;
                if (_lines.Count == 0)
                {
                    _lookupStatus = null;
                    _forceRefresh = true;
                }
                else
                {
                    _correctionPending = true;
                }
            }

            // When a song is nearly over and its lyrics are already available, opportunistically
            // warm the cache for the next track in the same album's catalog order. This cannot see
            // Apple Music's actual Up Next queue; random playlists simply fall back to normal lookup.
            MaybePrefetchNextAlbumTrack(playback, title, artist);

            if (_lines.Count == 0)
            {
                bool waitingForArtist = string.IsNullOrWhiteSpace(artist)
                    && _lookupStatus is null
                    && DateTime.UtcNow - _trackSeenUtc < TimeSpan.FromMilliseconds(1500);
                // A missing lyric stays settled for this track. Network failures get up to three
                // automatic retries after the initial attempt, with the whole cycle bounded to 15 s.
                // The overlay keeps the neutral placeholder and never exposes transient connection errors.
                DateTime now = DateTime.UtcNow;
                bool retryWindowExpired = _lookupStatus == LyricsLookupStatus.Unavailable
                    && now >= _retryDeadlineUtc;
                if (retryWindowExpired)
                {
                    _lookupStatus = LyricsLookupStatus.NotFound;
                    _nextRetryUtc = DateTime.MaxValue;
                }
                bool retryDue = _lookupStatus == LyricsLookupStatus.Unavailable
                    && _retryAttempt <= MaxAutomaticRetries
                    && now >= _nextRetryUtc
                    && now < _retryDeadlineUtc;
                if (!waitingForArtist && !_lookupRunning && (_lookupStatus is null || retryDue))
                    BeginLookup(playback, _trackKey);
                // A lookup that hits the on-disk cache finishes in a few milliseconds, so the
                // "正在查找歌词" text is suppressed for a short grace period instead of flashing.
                Render(waitingForArtist ? string.Empty : StatusMessage(), "");
                return;
            }

            if (_correctionPending && !_lookupRunning)
                BeginLookup(playback, _trackKey);

            // Positive offset means lyrics should appear later, so evaluate the timeline earlier.
            TimeSpan adjustedPosition = playback.Position - TimeSpan.FromMilliseconds(_settings.LyricsOffsetMilliseconds);
            int index = _lines.TakeWhile(line => line.Start <= adjustedPosition).Count() - 1;
            if (index < 0) index = 0;
            index = StableLineIndex(index, adjustedPosition);
            Render(_lines[index].Text, index + 1 < _lines.Count ? _lines[index + 1].Text : string.Empty, _lines[index].Translation ?? string.Empty, index + 1 < _lines.Count ? _lines[index + 1].Translation ?? string.Empty : string.Empty);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "LyricsWindow");
            if (!_lifetimeCancellation.IsCancellationRequested) Render("歌词读取异常；详情已写入日志", "");
        }
        finally { _updating = false; }
    }

    private void MaybePrefetchNextAlbumTrack(PlaybackSnapshot playback, string title, string artist)
    {
        if (_lines.Count == 0 || !playback.IsPlaying
            || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)
            || string.IsNullOrWhiteSpace(playback.Album)
            || playback.Duration < TimeSpan.FromSeconds(60)) return;

        double leadSeconds = Math.Clamp(playback.Duration.TotalSeconds * 0.25, 20, 60);
        if (playback.Duration - playback.Position > TimeSpan.FromSeconds(leadSeconds)) return;

        string key = title + "\n" + artist + "\n" + playback.Album;
        if (string.Equals(key, _prefetchAttemptedTrackKey, StringComparison.Ordinal)) return;
        _prefetchAttemptedTrackKey = key;
        _ = _service.PrefetchNextAlbumTrackLyricsAsync(playback, _lifetimeCancellation.Token);
    }

    private void CancelCurrentLookup()
    {
        CancellationTokenSource? current = _lookupCancellation;
        _lookupCancellation = null;
        if (current is null) return;
        try { current.Cancel(); }
        catch (ObjectDisposedException) { }
        current.Dispose();
    }

    private void ResetLookupState()
    {
        CancelCurrentLookup();
        _lines = Array.Empty<LyricLine>();
        _lookupStatus = null;
        _lookupRunning = false;
        _correctionPending = false;
        _retryAttempt = 0;
        _nextRetryUtc = DateTime.MaxValue;
        _retryDeadlineUtc = DateTime.MaxValue;
        _lastLineIndex = -1;
        _lastLinePosition = TimeSpan.Zero;
        // Invalidate any lookup that is still in flight for the previous track.
        _generation++;
    }

    /// <summary>Short retry spacing; the total attempt budget is bounded by RetryWindow.</summary>
    private static TimeSpan UnavailableRetryDelay() => TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The extrapolated SMTC position can dip by a few hundred milliseconds when a fresh sample
    /// arrives, which made the overlay flash the previous line for one tick and then jump back.
    /// A line only steps backwards after a real seek (more than two seconds).
    /// </summary>
    private int StableLineIndex(int index, TimeSpan position)
    {
        if (_lastLineIndex >= 0 && index < _lastLineIndex && position > _lastLinePosition - TimeSpan.FromSeconds(2))
            index = _lastLineIndex;

        if (index != _lastLineIndex)
        {
            _lastLineIndex = index;
            _lastLinePosition = position;
        }
        else if (position > _lastLinePosition)
        {
            _lastLinePosition = position;
        }
        return index;
    }

    /// <summary>Settings button and the lyric context menu both land here.</summary>
    public void RequestManualReload()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RequestManualReload);
            return;
        }
        ForceReload();
    }

    /// <summary>Re-runs the lookup for the current track.</summary>
    private void ForceReload()
    {
        _titleKey = string.Empty;
        _artistKey = string.Empty;
        ResetLookupState();
        _forceRefresh = true;
        Render("正在重新查找歌词……", "");
    }

    private void BeginLookup(PlaybackSnapshot playback, string key)
    {
        if (_retryDeadlineUtc == DateTime.MaxValue)
            _retryDeadlineUtc = DateTime.UtcNow + RetryWindow;
        CancelCurrentLookup();
        _lookupRunning = true;
        _correctionPending = false;
        int generation = _generation;
        bool forceRefresh = _forceRefresh;
        _forceRefresh = false;
        var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _lookupCancellation = lookupCancellation;
        _ = LookupAsync(playback, key, generation, forceRefresh, lookupCancellation.Token, lookupCancellation);
    }

    private async Task LookupAsync(PlaybackSnapshot playback, string key, int generation, bool forceRefresh,
        CancellationToken lookupToken, CancellationTokenSource lookupSource)
    {
        try
        {
            LyricsLookupResult result;
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(lookupToken);
            attemptTimeout.CancelAfter(LookupAttemptTimeout);
            try
            {
                result = await _service.GetLyricsAsync(
                    playback.Title, playback.Artist, playback.Album, playback.Duration,
                    forceRefresh, attemptTimeout.Token);
            }
            catch (OperationCanceledException) when (lookupToken.IsCancellationRequested)
            {
                // The track changed or the overlay closed; do not count cancellation as a network failure.
                return;
            }
            catch (OperationCanceledException) when (attemptTimeout.IsCancellationRequested)
            {
                // A single attempt must not consume the entire 15-second retry window.
                result = LyricsLookupResult.Unavailable;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "LyricsWindow:Lookup");
                result = LyricsLookupResult.Unavailable;
            }

            // The user skipped to another track while the request was running: drop the result.
            if (lookupToken.IsCancellationRequested || generation != _generation
                || !string.Equals(key, _trackKey, StringComparison.Ordinal)) return;

            _lookupRunning = false;
            CrashLog.WriteNote("LyricsResult",
                $"{playback.Title} | {playback.Artist} | {result.Status} | {result.Lyrics?.Lines.Count ?? 0} lines" +
                (forceRefresh ? " | forced" : string.Empty));
            if (result.Lyrics is not null)
            {
                _lines = result.Lyrics.Lines;
                _lookupStatus = LyricsLookupStatus.Found;
                _retryAttempt = 0;
                _nextRetryUtc = DateTime.MaxValue;
                _lastLineIndex = -1;
                _lastLinePosition = TimeSpan.Zero;
                return;
            }

            _lookupStatus = result.Status;
            if (result.Status == LyricsLookupStatus.Unavailable)
            {
                _retryAttempt++;
                if (_retryAttempt > MaxAutomaticRetries)
                {
                    // Initial lookup plus three retries have failed. Stop polling this track.
                    _lookupStatus = LyricsLookupStatus.NotFound;
                    _nextRetryUtc = DateTime.MaxValue;
                }
                else
                {
                    _nextRetryUtc = DateTime.UtcNow + UnavailableRetryDelay();
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_lookupCancellation, lookupSource))
            {
                _lookupCancellation = null;
                lookupSource.Dispose();
            }
        }
    }

    private string StatusMessage()
    {
        // A cache hit finishes within a few milliseconds, so the "正在查找歌词" placeholder only
        // appears when a real network lookup is still running after the grace period.
        if (_lookupStatus is null && DateTime.UtcNow - _trackSeenUtc < TimeSpan.FromMilliseconds(500))
            return string.Empty;

        if (_lookupRunning && _lookupStatus is null) return "正在查找歌词……";
        return _lookupStatus switch
        {
            LyricsLookupStatus.Unavailable => "正在查找歌词……",
            LyricsLookupStatus.NotFound => "本地歌曲 无歌词",
            LyricsLookupStatus.Found => "正在读取同步歌词……",
            _ => _lookupRunning ? "正在查找歌词……" : "正在读取同步歌词……"
        };
    }

    private void Render(string current, string next, string currentTranslation = "", string nextTranslation = "")
    {
        // The selected next-line mode applies to all orientations. In translation mode, the
        // current sentence's translation occupies the next-sentence position rather than being
        // drawn again in a separate translation slot.
        bool nextIsTranslation = _settings.LyricsTranslationEnabled
            && _settings.LyricsNextLineMode == "Translation"
            && !string.IsNullOrWhiteSpace(currentTranslation);
        if (_settings.LyricsNextLineMode == "Hidden")
        {
            next = string.Empty;
            nextTranslation = string.Empty;
        }
        else if (nextIsTranslation)
        {
            next = currentTranslation;
            currentTranslation = string.Empty;
            nextTranslation = string.Empty;
        }

        // Rebuilding the visual tree on every tick (even for unchanged text) made the overlay
        // flicker and burn CPU; only rebuild when the displayed text really changes.
        string signature = current + "\u001F" + next + "\u001F" + currentTranslation + "\u001F" + nextTranslation + "\u001F" + _settings.LyricsTranslationEnabled + "\u001F" + _settings.LyricsWordVerticalLayout + "\u001F" + _settings.LyricsNonWordVerticalLayout + "\u001F" + _settings.LyricsHorizontalTranslationLayout + "\u001F" + _settings.LyricsVerticalTranslationLayout + "\u001F" + _settings.LyricsNextLineMode + "\u001F" + _settings.LyricsTranslationFontSize + "\u001F" + _settings.LyricsCurrentColor + "\u001F" + _settings.LyricsNextColor;
        if (string.Equals(signature, _renderSignature, StringComparison.Ordinal)) return;
        _renderSignature = signature;

        // The color and the overall opacity are read on every render so the overlay follows
        // the applied settings without rebuilding the window by hand.
        Brush lyricsBrush = LyricStyle.CreateBrush(_settings.LyricsColor);
        Brush currentBrush = LyricStyle.CreateBrush(_settings.LyricsCurrentColor);
        Brush nextBrush = nextIsTranslation ? lyricsBrush : LyricStyle.CreateBrush(_settings.LyricsNextColor);
        var root = new Border
        {
            Background = _settings.LyricsBackgroundEnabled ? new SolidColorBrush(Color.FromArgb(155, 0, 0, 0)) : Brushes.Transparent,
            Padding = new Thickness(12),
            Opacity = LyricStyle.ClampOpacity(_settings.LyricsOpacity)
        };
        if (!_settings.LyricsBackgroundEnabled)
        {
            // Without the dark plate, white lyrics over a bright window (Apple Music itself, a
            // browser) are practically invisible. A soft outline keeps them readable.
            root.Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 7, Opacity = 0.9 };
        }

        // A track whose whole lyric is one line ("纯音乐，请欣赏" and similar notices) reads better
        // as a single block: vertically centred and hugging the right edge.
        // Status placeholders use the same single-line layout as instrumental tracks, so
        // "正在查找歌词" does not jump to a different alignment while the lookup is in flight.
        if ((_lines.Count == 1 || _lines.Count == 0) && !string.IsNullOrWhiteSpace(current)
            && string.IsNullOrWhiteSpace(next)
            && (!_settings.LyricsTranslationEnabled || string.IsNullOrWhiteSpace(currentTranslation)))
        {
            root.Child = CreateSingleLineLyrics(current, currentBrush);
            Content = root;
            return;
        }
        var alignment = _settings.LyricsAlignment switch { "Left" => HorizontalAlignment.Left, "Right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        if (_settings.LyricsOrientation == "Vertical")
        {
            bool wordBased = IsMostlyWordBased(current);
            string layout = wordBased ? _settings.LyricsWordVerticalLayout : _settings.LyricsNonWordVerticalLayout;
            if (layout == "Diagonal")
            {
                root.HorizontalAlignment = HorizontalAlignment.Stretch;
                root.VerticalAlignment = VerticalAlignment.Stretch;
                root.Child = CreateQuadrantLyrics(current, next, currentBrush, nextBrush, lyricsBrush, true, currentTranslation, nextTranslation, nextIsTranslation);
            }
            else
            {
                bool forceRightAlignment = layout == "RightAligned";
                var anchor = forceRightAlignment ? HorizontalAlignment.Right : _settings.LyricsVerticalAnchor switch
                {
                    "Left" => HorizontalAlignment.Left,
                    "Right" => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Right
                };
                root.Child = CreateRightStackedLyrics(current, next, currentTranslation, nextTranslation, currentBrush, nextBrush, lyricsBrush, wordBased, anchor, forceRightAlignment, nextIsTranslation);
            }
        }
        else if (!IsMostlyWordBased(current) && !_settings.LyricsTranslationEnabled)
        {
            root.HorizontalAlignment = HorizontalAlignment.Stretch;
            root.VerticalAlignment = VerticalAlignment.Stretch;
            root.Child = CreateQuadrantLyrics(current, next, currentBrush, nextBrush, lyricsBrush, false, currentTranslation, nextTranslation, nextIsTranslation);
        }
        else
        {
            if (_settings.LyricsTranslationEnabled)
                root.Child = CreateHorizontalTranslationLayout(current, next, currentTranslation, nextTranslation, currentBrush, nextBrush, lyricsBrush, alignment, nextIsTranslation);
            else
            {
                var rows = new StackPanel { HorizontalAlignment = alignment };
                rows.Children.Add(CreateText(current, 1.0, currentBrush));
                if (_settings.LyricsTranslationEnabled && !string.IsNullOrWhiteSpace(currentTranslation))
                    rows.Children.Add(CreateTranslationText(currentTranslation, 0.72, lyricsBrush));
                if (!string.IsNullOrWhiteSpace(next))
                {
                    if (nextIsTranslation)
                    {
                        rows.Children.Add(CreateTranslationText(next, 0.82, nextBrush, 1.0));
                    }
                    else
                    {
                        rows.Children.Add(CreateText(next, 0.55, nextBrush));
                        if (_settings.LyricsTranslationEnabled && !string.IsNullOrWhiteSpace(nextTranslation))
                            rows.Children.Add(CreateTranslationText(nextTranslation, 0.45, lyricsBrush));
                    }
                }
                root.Child = rows;
            }
        }
        Content = root;
    }

    /// <summary>
    /// One-line lyrics (instrumental notices): a vertical column for CJK, a horizontal line for
    /// word-based text, both pinned to the right edge and centred vertically.
    /// </summary>
    private FrameworkElement CreateHorizontalTranslationLayout(string current, string next, string currentTranslation, string nextTranslation, Brush currentBrush, Brush nextBrush, Brush translationBrush, HorizontalAlignment alignment, bool nextIsTranslation)
    {
        double w = ContentWidth(), h = ContentHeight();
        var canvas = new Canvas { Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        bool diagonal = _settings.LyricsHorizontalTranslationLayout == "Diagonal";
        bool leftLarge = _settings.LyricsHorizontalTranslationLayout == "LeftLarge";
        bool rightLarge = _settings.LyricsHorizontalTranslationLayout == "RightLarge";
        double lyricW = leftLarge || rightLarge ? w * 0.58 : w;
        TextBlock Add(string text, Brush brush, double opacity, double size, TextAlignment textAlignment, double width)
        {
            var block = new TextBlock { Text = text, Foreground = brush, Opacity = opacity, FontSize = size, TextWrapping = TextWrapping.Wrap, TextAlignment = textAlignment, Width = Math.Max(1, width) };
            canvas.Children.Add(block); return block;
        }
        double baseSize = Math.Clamp(_settings.LyricsFontSize, 10, 120);
        double translationBaseSize = GetTranslationFontSize(leftLarge || rightLarge ? 1.1 : 0.82);
        var currentBlock = Add(current, currentBrush, 1, leftLarge || rightLarge ? baseSize * .82 : baseSize, leftLarge ? TextAlignment.Right : alignment == HorizontalAlignment.Left ? TextAlignment.Left : alignment == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Center, lyricW);
        Canvas.SetLeft(currentBlock, leftLarge ? w - lyricW : 0); Canvas.SetTop(currentBlock, diagonal ? 0 : 2);
        if (!string.IsNullOrWhiteSpace(next))
        {
            double nextSize = nextIsTranslation ? GetTranslationFontSize(1.0) : leftLarge || rightLarge ? baseSize * .82 : baseSize;
            var nextBlock = Add(next, nextBrush, nextIsTranslation ? .82 : .58, nextSize, leftLarge ? TextAlignment.Right : alignment == HorizontalAlignment.Left ? TextAlignment.Left : alignment == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Center, lyricW);
            Canvas.SetLeft(nextBlock, leftLarge ? w - lyricW : 0); Canvas.SetTop(nextBlock, h * .55);
        }
        if (_settings.LyricsTranslationEnabled && !string.IsNullOrWhiteSpace(currentTranslation))
        {
            double tw = leftLarge || rightLarge ? w - lyricW : w * .48;
            var tr = Add(currentTranslation, translationBrush, .9, translationBaseSize, leftLarge ? TextAlignment.Left : TextAlignment.Right, tw);
            Canvas.SetLeft(tr, leftLarge ? 0 : rightLarge ? lyricW : diagonal ? w * .52 : w * .52);
            Canvas.SetTop(tr, diagonal ? 0 : leftLarge || rightLarge ? 0 : h * .28);
        }
        if (_settings.LyricsTranslationEnabled && !string.IsNullOrWhiteSpace(nextTranslation))
        {
            double tw = leftLarge || rightLarge ? w - lyricW : w * .48;
            var tr = Add(nextTranslation, translationBrush, .7, translationBaseSize, leftLarge ? TextAlignment.Left : TextAlignment.Right, tw);
            Canvas.SetLeft(tr, leftLarge ? 0 : rightLarge ? lyricW : diagonal ? 0 : w * .52);
            Canvas.SetTop(tr, diagonal ? h * .62 : leftLarge || rightLarge ? h * .55 : h * .78);
        }
        return canvas;
    }

    private FrameworkElement CreateSingleLineLyrics(string text, Brush brush)
    {
        if (_settings.LyricsOrientation == "Vertical" && !IsMostlyWordBased(text))
        {
            StackPanel column = CreateVerticalLine(text, 1.0, brush, ContentHeight());
            column.HorizontalAlignment = HorizontalAlignment.Right;
            column.VerticalAlignment = VerticalAlignment.Center;
            return column;
        }

        // A single horizontal line is shrunk to fit instead of wrapping mid-word.
        double maxWidth = ContentWidth();
        double fontSize = Math.Clamp(_settings.LyricsFontSize, 10, 120);
        double natural = MeasureTextWidth(text, fontSize);
        if (natural > maxWidth && natural > 0) fontSize = Math.Max(6, fontSize * maxWidth / natural);

        return new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = fontSize,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = maxWidth
        };
    }

    /// <summary>
    /// Non-word lyrics (Chinese, Japanese, Korean): the window is split into four equal cells.
    /// The current sentence is centred inside the top-left cell (reads downwards, wrapping into new
    /// columns at the cell height) and the next sentence is centred inside the bottom-right cell.
    /// Each block is limited to half the window height, so a long current line cannot run into the
    /// next sentence. Positions are computed on a Canvas instead of Grid cells: a Grid clips a
    /// child that is wider than its cell, which used to cut long Latin words exactly at the middle
    /// of the overlay. On a Canvas a wide block may extend into the empty neighbouring cell and is
    /// never clipped.
    /// </summary>
    private Canvas CreateQuadrantLyrics(string current, string next, Brush currentBrush, Brush nextBrush, Brush translationBrush, bool vertical, string currentTranslation, string nextTranslation, bool nextIsTranslation = false)
    {
        double contentWidth = ContentWidth();
        double contentHeight = ContentHeight();
        double cellWidth = Math.Max(1, contentWidth / 2);
        double cellHeight = Math.Max(1, contentHeight / 2);

        var canvas = new Canvas
        {
            Width = contentWidth,
            Height = contentHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        FrameworkElement currentBlock = vertical
            ? CreateVerticalLine(current, 1.0, currentBrush, cellHeight)
            : CreateCornerText(current, 1.0, currentBrush, TextAlignment.Left, cellWidth);
        Size currentSize = MeasureBlock(currentBlock);
        Canvas.SetLeft(currentBlock, currentSize.Width <= cellWidth ? Math.Max(0, (cellWidth - currentSize.Width) / 2) : 0);
        Canvas.SetTop(currentBlock, Math.Max(0, (cellHeight - currentSize.Height) / 2));
        canvas.Children.Add(currentBlock);

        if (!string.IsNullOrWhiteSpace(next))
        {
            FrameworkElement nextBlock = vertical
                ? CreateVerticalLine(next, nextIsTranslation ? 0.82 : 0.55, nextBrush, cellHeight, nextIsTranslation ? GetTranslationFontSize(1.0) : null)
                : CreateCornerText(next, nextIsTranslation ? 0.82 : 0.55, nextBrush, TextAlignment.Right, cellWidth, nextIsTranslation ? GetTranslationFontSize(1.0) : null);
            Size nextSize = MeasureBlock(nextBlock);
            double left = nextSize.Width <= cellWidth
                ? cellWidth + (cellWidth - nextSize.Width) / 2
                : Math.Max(0, contentWidth - nextSize.Width);
            Canvas.SetLeft(nextBlock, left);
            Canvas.SetTop(nextBlock, cellHeight + Math.Max(0, (cellHeight - nextSize.Height) / 2));
            canvas.Children.Add(nextBlock);
        }

        // In diagonal layout, translations occupy the opposite corners: current translation top-right,
        // next-line translation bottom-left. This keeps source lyrics and translations visually paired.
        if (_settings.LyricsTranslationEnabled && !string.IsNullOrWhiteSpace(currentTranslation))
        {
            FrameworkElement translation = vertical
                ? CreateVerticalLine(currentTranslation, 0.72, translationBrush, cellHeight, GetTranslationFontSize(0.9))
                : CreateCornerText(currentTranslation, 0.72, translationBrush, TextAlignment.Right, cellWidth, GetTranslationFontSize(0.9));
            Size size = MeasureBlock(translation);
            Canvas.SetLeft(translation, Math.Max(cellWidth, contentWidth - size.Width));
            Canvas.SetTop(translation, Math.Max(0, (cellHeight - size.Height) / 2));
            canvas.Children.Add(translation);
        }
        if (_settings.LyricsTranslationEnabled && !string.IsNullOrWhiteSpace(nextTranslation))
        {
            FrameworkElement translation = vertical
                ? CreateVerticalLine(nextTranslation, 0.45, translationBrush, cellHeight, GetTranslationFontSize(0.75))
                : CreateCornerText(nextTranslation, 0.45, translationBrush, TextAlignment.Left, cellWidth, GetTranslationFontSize(0.75));
            Size size = MeasureBlock(translation);
            Canvas.SetLeft(translation, Math.Max(0, (cellWidth - size.Width) / 2));
            Canvas.SetTop(translation, cellHeight + Math.Max(0, (cellHeight - size.Height) / 2));
            canvas.Children.Add(translation);
        }
        return canvas;
    }

    private FrameworkElement CreateRightStackedLyrics(string current, string next, string currentTranslation, string nextTranslation, Brush currentBrush, Brush nextBrush, Brush translationBrush, bool wordBased, HorizontalAlignment anchor, bool forceRightAlignment, bool nextIsTranslation)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = anchor,
            VerticalAlignment = _settings.LyricsVerticalAlignment switch { "Top" => VerticalAlignment.Top, "Bottom" => VerticalAlignment.Bottom, _ => VerticalAlignment.Center }
        };
        panel.Children.Add(CreateRightStackedSentence(current, currentTranslation, currentBrush, translationBrush, wordBased, anchor, forceRightAlignment, 1.0));
        if (!string.IsNullOrWhiteSpace(next))
        {
            // The explicit right-aligned vertical layout separates the two sentences more clearly.
            panel.Children.Add(new Border { Height = forceRightAlignment
                ? Math.Max(14, _settings.LyricsFontSize * 0.9)
                : Math.Max(4, _settings.LyricsFontSize * 0.35) });
            panel.Children.Add(CreateRightStackedSentence(next, nextIsTranslation ? string.Empty : nextTranslation, nextIsTranslation ? translationBrush : nextBrush, translationBrush, wordBased, anchor, forceRightAlignment, nextIsTranslation ? 0.82 : 0.55, nextIsTranslation));
        }
        return panel;
    }

    private FrameworkElement CreateRightStackedSentence(string text, string translation, Brush textBrush, Brush translationBrush, bool wordBased, HorizontalAlignment anchor, bool forceRightAlignment, double opacity, bool isTranslation = false)
    {
        FrameworkElement lyric = isTranslation
            ? wordBased
                ? CreateTranslationText(text, opacity, textBrush, 1.0)
                : CreateVerticalLine(text, opacity, textBrush, ContentHeight(), GetTranslationFontSize(1.0))
            : wordBased
                ? CreateStackedWordLines(text, string.Empty, textBrush, anchor)
                : CreateVerticalLine(text, opacity, textBrush, ContentHeight());
        lyric.HorizontalAlignment = anchor;

        bool hasTranslation = _settings.LyricsTranslationEnabled && !string.IsNullOrWhiteSpace(translation);
        string translationLayout = _settings.LyricsVerticalTranslationLayout;
        if (forceRightAlignment && hasTranslation && (translationLayout is "LeftVertical" or "RightVertical"))
        {
            FrameworkElement translationColumn = CreateVerticalLine(translation, opacity * 0.9, translationBrush, ContentHeight(), GetTranslationFontSize(1.0));
            translationColumn.VerticalAlignment = VerticalAlignment.Top;
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            var gap = new Border { Width = Math.Max(4, _settings.LyricsFontSize * 0.2) };
            if (translationLayout == "LeftVertical")
            {
                row.Children.Add(translationColumn);
                row.Children.Add(gap);
                row.Children.Add(lyric);
            }
            else
            {
                row.Children.Add(lyric);
                row.Children.Add(gap);
                row.Children.Add(translationColumn);
            }
            return row;
        }

        var block = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = anchor,
            VerticalAlignment = VerticalAlignment.Top
        };
        block.Children.Add(lyric);
        if (hasTranslation)
        {
            double translationScale = forceRightAlignment && translationLayout == "BottomSmall" ? 0.55 : 0.62;
            block.Children.Add(CreateTranslationText(translation, opacity * 0.72, translationBrush, translationScale));
        }
        return block;
    }

    private double GetTranslationFontSize(double scale = 1.0) => Math.Max(8, Math.Clamp(_settings.LyricsTranslationFontSize, 8, 96) * scale);

    private TextBlock CreateTranslationText(string text, double opacity, Brush brush, double sizeScale = 0.62) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = GetTranslationFontSize(sizeScale),
        Opacity = opacity,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Right,
        HorizontalAlignment = HorizontalAlignment.Right,
        MaxWidth = ContentWidth()
    };

    private static Size MeasureBlock(FrameworkElement block)
    {
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize;
    }

    /// <summary>Usable size inside the 12 px border padding; falls back to the configured size
    /// before the first layout pass (the constructor renders before the window is measured).</summary>
    private double ContentWidth() => Math.Max(1, (ActualWidth > 30 ? ActualWidth : Width) - 24);
    private double ContentHeight() => Math.Max(1, (ActualHeight > 30 ? ActualHeight : Height) - 24);

    /// <summary>True when the rectangle still overlaps the current desktop by a usable amount.</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var window = new Rect(left, top, Math.Max(1, width), Math.Max(1, height));
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Rect visible = Rect.Intersect(window, desktop);
        return !visible.IsEmpty && visible.Width >= 40 && visible.Height >= 40;
    }

    private TextBlock CreateCornerText(string text, double opacity, Brush brush, TextAlignment alignment, double maxWidth, double? fontSizeOverride = null) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = fontSizeOverride ?? Math.Clamp(_settings.LyricsFontSize, 10, 120),
        Opacity = opacity,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = alignment,
        MaxWidth = Math.Max(1, maxWidth),
        HorizontalAlignment = alignment == TextAlignment.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>
    /// Word-based sentence (English and other space-delimited scripts): every word is drawn
    /// horizontally on its own row, the current sentence above and the next sentence below.
    /// CJK characters keep their vertical reading (one glyph per row) and over-wide words are
    /// broken up instead of being clipped at the window edge.
    /// </summary>
    private StackPanel CreateStackedWordLines(string current, string next, Brush brush, HorizontalAlignment alignment)
    {
        double fontSize = Math.Clamp(_settings.LyricsFontSize, 10, 120);
        double maxWidth = ContentWidth();
        List<string> currentWords = SplitIntoWordRows(current, fontSize, maxWidth);
        List<string> nextWords = string.IsNullOrWhiteSpace(next) ? new List<string>() : SplitIntoWordRows(next, fontSize, maxWidth);
        double rowHeight = Math.Max(1, fontSize * 1.22);
        double gap = nextWords.Count == 0 ? 0 : Math.Max(4, fontSize * 0.55);
        double estimatedHeight = (currentWords.Count + nextWords.Count) * rowHeight + gap;
        double available = Math.Max(rowHeight, (ActualHeight > 30 ? ActualHeight : Height) - 26);
        var verticalAlignment = _settings.LyricsVerticalAlignment switch { "Top" => VerticalAlignment.Top, "Bottom" => VerticalAlignment.Bottom, _ => VerticalAlignment.Center };
        // A tall stack in a short window would be clipped at both ends when centred, so fall
        // back to top alignment to keep the current sentence readable from its first word.
        if (estimatedHeight > available) verticalAlignment = VerticalAlignment.Top;

        var lines = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = alignment,
            VerticalAlignment = verticalAlignment,
            UseLayoutRounding = false
        };
        AppendWordRows(lines, currentWords, 1.0, fontSize, brush);
        if (nextWords.Count > 0)
        {
            // A small gap separates the current sentence from the dimmed next sentence.
            lines.Children.Add(new Border { Height = gap });
            AppendWordRows(lines, nextWords, 0.55, fontSize, brush);
        }
        return lines;
    }

    private void AppendWordRows(StackPanel target, IEnumerable<string> words, double opacity, double fontSize, Brush brush)
    {
        foreach (string word in words)
        {
            var row = new TextBlock
            {
                Text = word,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = brush,
                FontSize = fontSize,
                Opacity = opacity,
                Height = Math.Max(1, fontSize * 1.22),
                LineHeight = Math.Max(1, fontSize * 1.22),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0)
            };
            TextOptions.SetTextFormattingMode(row, TextFormattingMode.Display);
            // Words follow the vertical anchor so a left/right anchored column stays flush.
            row.TextAlignment = _settings.LyricsVerticalAnchor switch
            {
                "Left" => TextAlignment.Left,
                "Right" => TextAlignment.Right,
                _ => TextAlignment.Center
            };
            row.HorizontalAlignment = HorizontalAlignment.Stretch;
            target.Children.Add(row);
        }
    }

    /// <summary>
    /// Rows of the stacked layout: words stay whole, CJK characters keep one glyph per row, and a
    /// token wider than the overlay is broken into readable chunks (never clipped).
    /// </summary>
    private static List<string> SplitIntoWordRows(string text, double fontSize, double maxWidth)
    {
        var tokens = new List<string>();
        foreach (string word in SplitIntoWords(text))
        {
            var buffer = new System.Text.StringBuilder();
            foreach (char c in word)
            {
                if (IsCjk(c))
                {
                    if (buffer.Length > 0) { tokens.Add(buffer.ToString()); buffer.Clear(); }
                    tokens.Add(c.ToString());
                }
                else
                {
                    buffer.Append(c);
                }
            }
            if (buffer.Length > 0) tokens.Add(buffer.ToString());
        }

        var rows = new List<string>();
        foreach (string token in tokens)
        {
            string remaining = token;
            while (remaining.Length > 0)
            {
                int take = remaining.Length;
                while (take > 1 && MeasureTextWidth(remaining[..take], fontSize) > maxWidth) take--;
                rows.Add(remaining[..take]);
                remaining = remaining[take..];
            }
        }
        return rows;
    }

    private static double MeasureTextWidth(string text, double fontSize)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize, Brushes.White, 1.0);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>Space-delimited words; trailing punctuation stays attached to its word.</summary>
    private static List<string> SplitIntoWords(string text)
    {
        var words = new List<string>();
        int index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index >= text.Length) break;
            int start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
            words.Add(text[start..index]);
        }
        return words;
    }

    /// <summary>
    /// True when more than half of the letters in the line belong to a space-delimited script
    /// (Latin/Greek/Cyrillic). A mixed hook such as "I love you 我爱你" is then read as words,
    /// right aligned, instead of being squeezed into CJK glyph columns.
    /// </summary>
    private static bool IsMostlyWordBased(string text)
    {
        int wordCharacters = 0;
        int cjkCharacters = 0;
        foreach (char c in text)
        {
            if (IsCjk(c)) cjkCharacters++;
            else if (char.IsLetterOrDigit(c)) wordCharacters++;
        }
        return wordCharacters > 0 && wordCharacters > cjkCharacters;
    }

    private static bool IsCjk(char c) =>
        (c >= '\u3040' && c <= '\u30ff') || (c >= '\u3400' && c <= '\u9fff') || (c >= '\uf900' && c <= '\ufaff')
        || (c >= '\uff66' && c <= '\uff9d') || (c >= '\uac00' && c <= '\ud7af');

    private TextBlock CreateText(string text, double opacity, Brush brush) => new() { Text = text, Foreground = brush, FontSize = Math.Clamp(_settings.LyricsFontSize, 10, 120), Opacity = opacity, TextWrapping = TextWrapping.Wrap, TextAlignment = _settings.LyricsAlignment switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center } };

    /// <summary>One vertical column cell: either a single glyph or a whole word drawn horizontally.</summary>
    private readonly record struct VerticalToken(string Text, bool IsWord);

    private StackPanel CreateVerticalLine(string text, double opacity, Brush brush, double heightBudget, double? fontSizeOverride = null)
    {
        double fontSize = fontSizeOverride ?? Math.Clamp(_settings.LyricsFontSize, 10, 120);
        // WPF fallback fonts can have different vertical metrics. Chinese glyphs rendered
        // through a fallback font may therefore create visibly larger gaps than Japanese.
        // Select a script-appropriate font and constrain every glyph cell to one em.
        bool containsJapaneseKana = text.Any(c => (c >= '\u3040' && c <= '\u30ff') || (c >= '\uff66' && c <= '\uff9d'));
        bool containsHan = text.Any(c => c >= '\u3400' && c <= '\u9fff');
        FontFamily glyphFont = new(containsJapaneseKana ? "Yu Gothic UI" : containsHan ? "Microsoft YaHei UI" : "Segoe UI");

        // Word-based languages (English and other space-delimited scripts) must not be split
        // letter by letter in vertical mode: keep every word as one horizontal cell and let
        // the column wrap into additional columns when the cell is too short.
        double available = Math.Max(fontSize * 2, heightBudget);
        var columns = new StackPanel { Orientation = Orientation.Horizontal, UseLayoutRounding = false };
        StackPanel? column = null;
        double usedHeight = 0;
        double columnWidth = 0;

        void CloseColumn()
        {
            if (column is null) return;
            // Explicit width: a long Latin word widens its column instead of spilling over the
            // neighbouring one, so glyphs and words never draw on top of each other.
            column.Width = Math.Max(1, columnWidth);
        }

        foreach (VerticalToken token in TokenizeVertical(text))
        {
            char character = token.Text[0];
            bool isGap = !token.IsWord && (char.IsWhiteSpace(character) || character is '\u3000');
            double cellHeight = token.IsWord ? WordCellHeight(fontSize) : GlyphCellHeight(character, fontSize);
            if (column is not null && usedHeight > 0 && usedHeight + cellHeight > available)
            {
                CloseColumn();
                column = null;
            }
            if (column is null)
            {
                // Never open a column with a space: the gap would look like an empty row.
                if (isGap) continue;
                column = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 2, 0), UseLayoutRounding = false };
                columns.Children.Add(column);
                usedHeight = 0;
                columnWidth = 0;
            }

            FrameworkElement cell = token.IsWord
                ? CreateWordCell(token.Text, fontSize, cellHeight, opacity, brush)
                : CreateGlyphCell(character, fontSize, cellHeight, opacity, brush, glyphFont);
            // Cells line up on the column's left edge so a widened column still reads as one column.
            cell.HorizontalAlignment = HorizontalAlignment.Left;
            column.Children.Add(cell);
            columnWidth = Math.Max(columnWidth, MeasureCellWidth(cell));
            usedHeight += cellHeight;
        }

        CloseColumn();
        if (columns.Children.Count == 0) columns.Children.Add(new StackPanel { Orientation = Orientation.Vertical });
        return columns;
    }

    private static double MeasureCellWidth(FrameworkElement cell)
    {
        cell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(1, cell.DesiredSize.Width);
    }

    private static double WordCellHeight(double fontSize) => fontSize;

    // A full-em cell for every character makes punctuation and spaces look like full lyric
    // glyphs. Keep normal CJK characters at one em, but compact these low-density characters
    // to reduce the artificial vertical gaps.
    private static double GlyphCellHeight(char character, double fontSize)
    {
        bool isWhitespace = char.IsWhiteSpace(character) || character is '\u3000';
        bool isPunctuation = char.IsPunctuation(character) || char.IsSymbol(character)
            || "、。，．・：；？！ー〜～…‥「」『』（）［］｛｝〈〉《》【】〔〕〝〟〰〽".Contains(character);
        return fontSize * (isWhitespace ? (character == '\u3000' ? 0.45 : 0.28) : isPunctuation ? 0.68 : 1.0);
    }

    /// <summary>Splits a lyric line into CJK glyph cells and whole-word cells for Latin-script words.</summary>
    private static List<VerticalToken> TokenizeVertical(string text)
    {
        var tokens = new List<VerticalToken>(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            if (IsWordCharacter(text[index]))
            {
                int start = index;
                index++;
                while (index < text.Length && IsWordCharacter(text[index])) index++;
                string run = text[start..index];
                // A single letter keeps the shared glyph-cell metrics; real words stay horizontal.
                tokens.Add(new VerticalToken(run, run.Length > 1));
                continue;
            }
            tokens.Add(new VerticalToken(text[index].ToString(), false));
            index++;
        }
        return tokens;
    }

    private static bool IsWordCharacter(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
        || (c >= '\u00C0' && c <= '\u00D6') || (c >= '\u00D8' && c <= '\u00F6') || (c >= '\u00F8' && c <= '\u024F')
        || (c >= '\u0370' && c <= '\u03FF') || (c >= '\u0400' && c <= '\u04FF')
        || c == '&' || c == '\'' || c == '\u2019';

    private TextBlock CreateWordCell(string word, double fontSize, double cellHeight, double opacity, Brush brush)
    {
        // The word keeps its font size. Its cell is simply as wide as the word itself; the column
        // it lives in grows with it, so the word may extend past the quarter cell (into the empty
        // neighbouring cell) without ever covering other lyrics.
        double width = Math.Max(fontSize * 1.5, MeasureTextWidth(word, fontSize) + 4);

        var cell = new TextBlock
        {
            Text = word,
            // Word cells keep Latin metrics, so the glyph font used for CJK fallback is not needed.
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = brush,
            FontSize = fontSize,
            Width = width,
            Margin = new Thickness(0, 0, Math.Max(1, fontSize * 0.16), 0)
        };
        ConfigureVerticalCell(cell, cellHeight, opacity);
        TextOptions.SetTextFormattingMode(cell, TextFormattingMode.Display);
        return cell;
    }

    private static TextBlock CreateGlyphCell(char character, double fontSize, double cellHeight, double opacity, Brush brush, FontFamily glyphFont)
    {
        bool isWhitespace = char.IsWhiteSpace(character) || character is '\u3000';
        bool isPunctuation = char.IsPunctuation(character) || char.IsSymbol(character)
            || "、。，．・：；？！ー〜～…‥「」『』（）［］｛｝〈〉《》【】〔〕〝〟〰〽".Contains(character);
        double glyphSize = isWhitespace ? 1.0 : isPunctuation ? fontSize * 0.82 : fontSize;
        var glyph = new TextBlock
        {
            Text = isWhitespace ? string.Empty : character.ToString(),
            FontFamily = glyphFont,
            Foreground = brush,
            FontSize = glyphSize,
            Margin = new Thickness(0)
        };
        ConfigureVerticalCell(glyph, cellHeight, opacity);
        TextOptions.SetTextFormattingMode(glyph, TextFormattingMode.Display);
        return glyph;
    }

    private static void ConfigureVerticalCell(TextBlock cell, double cellHeight, double opacity)
    {
        double height = Math.Max(1, cellHeight);
        cell.LineHeight = height;
        cell.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        cell.Padding = new Thickness(0);
        cell.Height = cell.MinHeight = cell.MaxHeight = height;
        cell.TextWrapping = TextWrapping.NoWrap;
        cell.Opacity = opacity;
        cell.HorizontalAlignment = HorizontalAlignment.Center;
        cell.VerticalAlignment = VerticalAlignment.Center;
    }

    private void DragAnywhere(object sender, MouseButtonEventArgs e)
    {
        if (_settings.LyricsBackgroundEnabled && e.ButtonState == MouseButtonState.Pressed) { DragMove(); e.Handled = true; }
    }

    private void SaveBounds()
    {
        // Rebuild width-dependent layouts immediately, but persist the final bounds only after
        // the drag/resize settles. Saving settings.json for every pointer movement caused needless
        // I/O and made a partial file much more likely if the process was interrupted.
        _renderSignature = string.Empty;
        if (!IsLoaded || !double.IsFinite(Left) || !double.IsFinite(Top)) return;
        _boundsSaveTimer.Stop();
        _boundsSaveTimer.Start();
    }

    private void SaveBoundsCore()
    {
        if (!IsLoaded || !double.IsFinite(Left) || !double.IsFinite(Top)
            || !double.IsFinite(ActualWidth) || !double.IsFinite(ActualHeight)
            || ActualWidth < 1 || ActualHeight < 1) return;

        _settings.LyricsWindowWidth = ActualWidth;
        _settings.LyricsWindowHeight = ActualHeight;
        _settings.LyricsWindowLeft = Left;
        _settings.LyricsWindowTop = Top;
        try { _settingsService.Save(_settings); }
        catch (Exception ex) { CrashLog.Write(ex, "LyricsWindow:SaveBounds"); }
    }

    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HideFromAltTab();
        ApplyClickThrough();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WindowMessageHook);
        EnsureTopmost();
    }

    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);

    /// <summary>
    /// Second safety net for the click-through mode: WS_EX_TRANSPARENT already keeps the window out
    /// of hit testing, and HTTRANSPARENT covers shells where the style alone is not enough.
    /// </summary>
    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmNcHitTest && !_settings.LyricsBackgroundEnabled)
        {
            handled = true;
            return HtTransparent;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// With the dark plate switched off the overlay is just floating text: WS_EX_TRANSPARENT makes
    /// the whole window invisible to the mouse so clicks reach the app underneath (even on a
    /// glyph). With the plate on it stays interactive for dragging and the context menu.
    /// </summary>
    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;

        long style = GetWindowLongPtr(_hwnd, GwlExstyle).ToInt64();
        style = _settings.LyricsBackgroundEnabled ? style & ~WsExTransparent : style | WsExTransparent;
        SetWindowLongPtr(_hwnd, GwlExstyle, new IntPtr(style));
    }

    /// <summary>
    /// Raises the overlay back to the top of the topmost band when another topmost window ended
    /// up above it (the plain Topmost flag is applied only once, so it is not enough). Moving,
    /// resizing and activating are suppressed: the overlay must never steal focus.
    /// The shell (Start menu, Alt-Tab, taskbar flyouts) is deliberately above topmost windows
    /// and is left alone, and the overlay's own context menu keeps its place while open.
    /// </summary>
    private void EnsureTopmost(bool forceRaise = false)
    {
        if (!IsVisible || _menuOpen) return;
        IntPtr handle = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || (!forceRaise && !NeedsTopmostRaise(handle))) return;
        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static bool NeedsTopmostRaise(IntPtr handle)
    {
        // Losing the topmost ex-style (for example after a shell transition) always needs a fix.
        if ((GetWindowLongPtr(handle, GwlExstyle).ToInt64() & WsExTopmost) == 0) return true;

        // GetWindow(GW_HWNDPREV) is the window directly above ours in z-order. No window above
        // means the overlay already owns the top of the band.
        IntPtr above = GetWindow(handle, GwHwndPrev);
        if (above == IntPtr.Zero) return false;
        if ((GetWindowLongPtr(above, GwlExstyle).ToInt64() & WsExTopmost) == 0) return false;

        // Start menu / Alt-Tab / shell flyouts are topmost on purpose; do not cover them.
        return !IsShellForeground();
    }

    private static bool IsShellForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == 0) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return ShellProcessNames.Contains(process.ProcessName);
        }
        catch
        {
            // The foreground process may exit or be protected; assume it is not the shell.
            return false;
        }
    }

    private static readonly HashSet<string> ShellProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "TextInputHost", "LockApp"
    };

    private const int GwlExstyle = -20;
    private const int WsExTopmost = 0x8;
    private const long WsExTransparent = 0x20;
    private const uint GwHwndPrev = 3;

    private void HideFromAltTab()
    {
        var helper = new WindowInteropHelper(this); const int WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
        IntPtr style = GetWindowLongPtr(helper.Handle, GwlExstyle);
        SetWindowLongPtr(helper.Handle, GwlExstyle, new IntPtr((style.ToInt64() | WsExToolWindow) & ~WsExAppWindow));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
