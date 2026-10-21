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
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
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
    // The artist changed after lyrics were already shown: re-check in the background.
    private bool _correctionPending;
    // Watermark used to keep the displayed line from stepping backwards for one tick.
    private int _lastLineIndex = -1;
    private TimeSpan _lastLinePosition;

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
        Loaded += async (_, _) => { _timer.Start(); _topmostTimer.Start(); EnsureTopmost(); await UpdateAsync(); };
        Closed += (_, _) => { _timer.Stop(); _topmostTimer.Stop(); };
        IsVisibleChanged += (_, _) => EnsureTopmost();
        Activated += (_, _) => EnsureTopmost();
        Deactivated += (_, _) => EnsureTopmost();
        SizeChanged += (_, _) => SaveBounds();
        LocationChanged += (_, _) => SaveBounds();
        _timer.Tick += async (_, _) => await UpdateAsync();
        _topmostTimer.Tick += (_, _) => EnsureTopmost();
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
        // A slow SMTC call can outlive the 350 ms timer; never run two updates at once.
        if (_updating) return;
        _updating = true;
        try
        {
            var playback = await _service.GetCurrentPlaybackAsync();
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

            if (_lines.Count == 0)
            {
                bool waitingForArtist = string.IsNullOrWhiteSpace(artist)
                    && _lookupStatus is null
                    && DateTime.UtcNow - _trackSeenUtc < TimeSpan.FromMilliseconds(1500);
                // Each track is looked up once. Connection failures and misses stay on screen
                // until the user hits 「重新查找歌词」 in settings (or the lyric context menu).
                if (!waitingForArtist && !_lookupRunning && _lookupStatus is null)
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
            Render(_lines[index].Text, index + 1 < _lines.Count ? _lines[index + 1].Text : string.Empty);
        }
        catch (Exception ex) { CrashLog.Write(ex, "LyricsWindow"); Render("歌词读取异常；详情已写入日志", ""); }
        finally { _updating = false; }
    }

    private void ResetLookupState()
    {
        _lines = Array.Empty<LyricLine>();
        _lookupStatus = null;
        _lookupRunning = false;
        _correctionPending = false;
        _lastLineIndex = -1;
        _lastLinePosition = TimeSpan.Zero;
        // Invalidate any lookup that is still in flight for the previous track.
        _generation++;
    }

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
        _lookupRunning = true;
        _correctionPending = false;
        int generation = _generation;
        bool forceRefresh = _forceRefresh;
        _forceRefresh = false;
        _ = LookupAsync(playback, key, generation, forceRefresh);
    }

    private async Task LookupAsync(PlaybackSnapshot playback, string key, int generation, bool forceRefresh)
    {
        LyricsLookupResult result;
        try
        {
            result = await _service.GetLyricsAsync(playback.Title, playback.Artist, playback.Album, playback.Duration, forceRefresh);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "LyricsWindow:Lookup");
            result = LyricsLookupResult.Unavailable;
        }

        // The user skipped to another track while the request was running: drop the result.
        if (generation != _generation || !string.Equals(key, _trackKey, StringComparison.Ordinal)) return;

        _lookupRunning = false;
        // One diagnostic line per lookup: enough to tell "provider has no lyrics" apart from
        // "rendering is broken" when a user reports that the overlay shows nothing.
        CrashLog.WriteNote("LyricsResult",
            $"{playback.Title} | {playback.Artist} | {result.Status} | {result.Lyrics?.Lines.Count ?? 0} lines" +
            (forceRefresh ? " | forced" : string.Empty));
        if (result.Lyrics is not null)
        {
            _lines = result.Lyrics.Lines;
            _lookupStatus = LyricsLookupStatus.Found;
            // Start from a clean watermark: the first lyric line of a fresh track may be behind the
            // previous track's last line index.
            _lastLineIndex = -1;
            _lastLinePosition = TimeSpan.Zero;
            return;
        }

        _lookupStatus = result.Status;
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
            LyricsLookupStatus.Unavailable => "无法连接歌词服务器。请到设置中点击「重新查找歌词」。",
            LyricsLookupStatus.NotFound => "未识别到歌词。请到设置中点击「重新查找歌词」。",
            LyricsLookupStatus.Found => "正在读取同步歌词……",
            _ => _lookupRunning ? "正在查找歌词……" : "正在读取同步歌词……"
        };
    }

    private void Render(string current, string next)
    {
        // Rebuilding the visual tree on every tick (even for unchanged text) made the overlay
        // flicker and burn CPU; only rebuild when the displayed text really changes.
        string signature = current + "\u001F" + next;
        if (string.Equals(signature, _renderSignature, StringComparison.Ordinal)) return;
        _renderSignature = signature;

        // The color and the overall opacity are read on every render so the overlay follows
        // the applied settings without rebuilding the window by hand.
        Brush lyricsBrush = LyricStyle.CreateBrush(_settings.LyricsColor);
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
        var alignment = _settings.LyricsAlignment switch { "Left" => HorizontalAlignment.Left, "Right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        if (_settings.LyricsOrientation == "Vertical")
        {
            // Vertical mode has its own horizontal anchor so the column can hug the window's
            // left or right edge independently of the horizontal-mode alignment.
            var anchor = _settings.LyricsVerticalAnchor switch
            {
                "Left" => HorizontalAlignment.Left,
                "Right" => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center
            };
            if (IsMostlyWordBased(current))
            {
                // More than half of the line is a space-delimited script ("I love you 我爱你",
                // English hook lines in a CJK song): read it as stacked words, right aligned,
                // instead of squeezing the words into CJK glyph columns.
                root.Child = CreateStackedWordLines(current, next, lyricsBrush, anchor);
            }
            else
            {
                // CJK and other non-word scripts: the window is split into four cells, the current
                // line fills the top-left cell, the next line the bottom-right cell.
                root.HorizontalAlignment = HorizontalAlignment.Stretch;
                root.VerticalAlignment = VerticalAlignment.Stretch;
                root.Child = CreateQuadrantLyrics(current, next, lyricsBrush, vertical: true);
            }
        }
        else if (!IsMostlyWordBased(current))
        {
            root.HorizontalAlignment = HorizontalAlignment.Stretch;
            root.VerticalAlignment = VerticalAlignment.Stretch;
            root.Child = CreateQuadrantLyrics(current, next, lyricsBrush, vertical: false);
        }
        else
        {
            var rows = new StackPanel { HorizontalAlignment = alignment };
            rows.Children.Add(CreateText(current, 1.0, lyricsBrush));
            if (!string.IsNullOrWhiteSpace(next)) rows.Children.Add(CreateText(next, 0.55, lyricsBrush));
            root.Child = rows;
        }
        Content = root;
    }

    /// <summary>
    /// Non-word lyrics (Chinese, Japanese, Korean): the window is split into four equal cells.
    /// The current sentence is laid out in the top-left cell (reads downwards, wrapping into new
    /// columns) and the next sentence in the bottom-right cell (anchored to the bottom edge, so it
    /// grows upwards). Each block is limited to half the window height, which keeps a long current
    /// line from running into the next sentence.
    /// </summary>
    private Grid CreateQuadrantLyrics(string current, string next, Brush brush, bool vertical)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        double cellWidth = Math.Max(1, ContentWidth() / 2);
        double cellHeight = Math.Max(1, ContentHeight() / 2);

        FrameworkElement currentBlock = vertical
            ? CreateVerticalLine(current, 1.0, brush, cellHeight)
            : CreateCornerText(current, 1.0, brush, TextAlignment.Left, cellWidth);
        currentBlock.HorizontalAlignment = HorizontalAlignment.Left;
        currentBlock.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(currentBlock, 0);
        Grid.SetRow(currentBlock, 0);
        grid.Children.Add(currentBlock);

        if (!string.IsNullOrWhiteSpace(next))
        {
            FrameworkElement nextBlock = vertical
                ? CreateVerticalLine(next, 0.55, brush, cellHeight)
                : CreateCornerText(next, 0.55, brush, TextAlignment.Right, cellWidth);
            nextBlock.HorizontalAlignment = HorizontalAlignment.Right;
            nextBlock.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(nextBlock, 1);
            Grid.SetRow(nextBlock, 1);
            grid.Children.Add(nextBlock);
        }
        return grid;
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

    private TextBlock CreateCornerText(string text, double opacity, Brush brush, TextAlignment alignment, double maxWidth) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = Math.Clamp(_settings.LyricsFontSize, 10, 120),
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

    private StackPanel CreateVerticalLine(string text, double opacity, Brush brush, double heightBudget)
    {
        double fontSize = Math.Clamp(_settings.LyricsFontSize, 10, 120);
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
        foreach (VerticalToken token in TokenizeVertical(text))
        {
            char character = token.Text[0];
            bool isGap = !token.IsWord && (char.IsWhiteSpace(character) || character is '\u3000');
            double cellHeight = token.IsWord ? WordCellHeight(fontSize) : GlyphCellHeight(character, fontSize);
            if (column is not null && usedHeight > 0 && usedHeight + cellHeight > available) column = null;
            if (column is null)
            {
                // Never open a column with a space: the gap would look like an empty row.
                if (isGap) continue;
                column = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 2, 0), UseLayoutRounding = false };
                columns.Children.Add(column);
                usedHeight = 0;
            }
            column.Children.Add(token.IsWord
                ? CreateWordCell(token.Text, fontSize, cellHeight, opacity, brush)
                : CreateGlyphCell(character, fontSize, cellHeight, opacity, brush, glyphFont));
            usedHeight += cellHeight;
        }
        if (columns.Children.Count == 0) columns.Children.Add(new StackPanel { Orientation = Orientation.Vertical });
        return columns;
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
        // A text block measured inside a quarter-width grid cell is squeezed to the cell width and
        // then clipped when arranged: a Latin word inside a CJK line was cut off exactly at the
        // middle of the overlay. Measure the word itself and give the cell that exact width; a word
        // wider than the whole overlay shrinks instead of being truncated.
        double maxWidth = ContentWidth();
        double naturalWidth = MeasureTextWidth(word, fontSize);
        double scale = naturalWidth > maxWidth ? maxWidth / naturalWidth : 1.0;
        double effectiveFontSize = Math.Max(6, fontSize * scale);
        double width = Math.Max(1, Math.Min(maxWidth, MeasureTextWidth(word, effectiveFontSize) + 2));

        var cell = new TextBlock
        {
            Text = word,
            // Word cells keep Latin metrics, so the glyph font used for CJK fallback is not needed.
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = brush,
            FontSize = effectiveFontSize,
            Width = width,
            LineHeight = Math.Max(1, cellHeight),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, Math.Max(1, fontSize * 0.16), 0),
            Height = Math.Max(1, cellHeight),
            MinHeight = Math.Max(1, cellHeight),
            MaxHeight = Math.Max(1, cellHeight),
            TextWrapping = TextWrapping.NoWrap,
            Opacity = opacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
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
            LineHeight = Math.Max(1, cellHeight),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Height = Math.Max(1, cellHeight),
            MinHeight = Math.Max(1, cellHeight),
            MaxHeight = Math.Max(1, cellHeight),
            TextWrapping = TextWrapping.NoWrap,
            Opacity = opacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        TextOptions.SetTextFormattingMode(glyph, TextFormattingMode.Display);
        return glyph;
    }

    private void DragAnywhere(object sender, MouseButtonEventArgs e)
    {
        if (_settings.LyricsBackgroundEnabled && e.ButtonState == MouseButtonState.Pressed) { DragMove(); e.Handled = true; }
    }

    private void SaveBounds()
    {
        // The vertical lyric layout is built from the window size, so a resize must rebuild it.
        _renderSignature = string.Empty;
        if (!IsLoaded || double.IsNaN(Left) || double.IsNaN(Top)) return;
        _settings.LyricsWindowWidth = ActualWidth; _settings.LyricsWindowHeight = ActualHeight; _settings.LyricsWindowLeft = Left; _settings.LyricsWindowTop = Top;
        _settingsService.Save(_settings);
    }

    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HideFromAltTab();
        EnsureTopmost();
    }

    /// <summary>
    /// Raises the overlay back to the top of the topmost band when another topmost window ended
    /// up above it (the plain Topmost flag is applied only once, so it is not enough). Moving,
    /// resizing and activating are suppressed: the overlay must never steal focus.
    /// The shell (Start menu, Alt-Tab, taskbar flyouts) is deliberately above topmost windows
    /// and is left alone, and the overlay's own context menu keeps its place while open.
    /// </summary>
    private void EnsureTopmost()
    {
        if (!IsVisible || _menuOpen) return;
        IntPtr handle = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !NeedsTopmostRaise(handle)) return;
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
