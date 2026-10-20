using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    // Topmost is only a one-shot z-order flag: another topmost window that appears later (or the
    // player itself) ends up above the overlay. Re-asserting it while visible keeps it on top.
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private string _trackKey = string.Empty;
    private string _renderSignature = string.Empty;
    private LyricsLookupStatus? _lookupStatus;
    private DateTime _nextLookupUtc = DateTime.MinValue;
    private int _lookupAttempts;
    private bool _lookupRunning;
    private int _generation;
    private IntPtr _hwnd;
    private bool _menuOpen;
    private bool _updating;
    private bool _forceRefresh;

    public LyricsWindow(LyricsService service, OverlaySettings settings, Action? openSettings = null, Action? exitApplication = null)
    {
        _service = service; _settings = settings;
        Width = Math.Max(180, _settings.LyricsWindowWidth); Height = Math.Max(70, _settings.LyricsWindowHeight);
        MinWidth = 180; MinHeight = 70; Topmost = true; ShowInTaskbar = false; AllowsTransparency = true;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; Background = Brushes.Transparent; ShowActivated = false;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(8), GlassFrameThickness = new Thickness(0) });
        WindowStartupLocation = _settings.LyricsWindowLeft.HasValue && _settings.LyricsWindowTop.HasValue ? WindowStartupLocation.Manual : WindowStartupLocation.CenterScreen;
        if (_settings.LyricsWindowLeft.HasValue) Left = _settings.LyricsWindowLeft.Value;
        if (_settings.LyricsWindowTop.HasValue) Top = _settings.LyricsWindowTop.Value;
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
                // A strict Apple Music session check runs inside the service: another player's
                // lyrics are never shown here once Apple Music is closed.
                Render("未识别到正在播放的 Apple Music 歌曲", "");
                return;
            }

            string key = $"{playback.Title}\n{playback.Artist}";
            if (key != _trackKey)
            {
                _trackKey = key;
                ResetLookupState();
            }

            if (_lines.Count == 0)
            {
                // Lyrics may only become available a moment later (slow provider, rate limit,
                // offline), so keep retrying with a back-off instead of caching "not found"
                // for the lifetime of the track.
                if (!_lookupRunning && DateTime.UtcNow >= _nextLookupUtc)
                    BeginLookup(playback, key);
                Render(StatusMessage(), "");
                return;
            }

            // Positive offset means lyrics should appear later, so evaluate the timeline earlier.
            TimeSpan adjustedPosition = playback.Position - TimeSpan.FromMilliseconds(_settings.LyricsOffsetMilliseconds);
            int index = _lines.TakeWhile(line => line.Start <= adjustedPosition).Count() - 1;
            if (index < 0) index = 0;
            Render(_lines[index].Text, index + 1 < _lines.Count ? _lines[index + 1].Text : string.Empty);
        }
        catch (Exception ex) { CrashLog.Write(ex, "LyricsWindow"); Render("歌词读取异常；详情已写入日志", ""); }
        finally { _updating = false; }
    }

    private void ResetLookupState()
    {
        _lines = Array.Empty<LyricLine>();
        _lookupStatus = null;
        _lookupAttempts = 0;
        _nextLookupUtc = DateTime.MinValue;
        _lookupRunning = false;
        // Invalidate any lookup that is still in flight for the previous track.
        _generation++;
    }

    /// <summary>Re-runs the lookup for the current track (context menu action).</summary>
    private void ForceReload()
    {
        ResetLookupState();
        _forceRefresh = true;
        _nextLookupUtc = DateTime.MinValue;
        Render("正在重新查找歌词……", "");
    }

    private void BeginLookup(PlaybackSnapshot playback, string key)
    {
        _lookupRunning = true;
        _lookupAttempts++;
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
            result = await _service.GetLyricsAsync(playback.Title, playback.Artist, playback.Duration, forceRefresh);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "LyricsWindow:Lookup");
            result = LyricsLookupResult.Unavailable;
        }

        // The user skipped to another track while the request was running: drop the result.
        if (generation != _generation || !string.Equals(key, _trackKey, StringComparison.Ordinal)) return;

        _lookupRunning = false;
        if (result.Lyrics is not null)
        {
            _lines = result.Lyrics.Lines;
            _lookupStatus = LyricsLookupStatus.Found;
            _nextLookupUtc = DateTime.MaxValue;
            return;
        }

        _lookupStatus = result.Status;
        _nextLookupUtc = DateTime.UtcNow + RetryDelay(_lookupAttempts, result.Status);
    }

    /// <summary>Back-off schedule; never stops retrying, but slows down to once every few minutes.</summary>
    private static TimeSpan RetryDelay(int attempt, LyricsLookupStatus status)
    {
        if (status == LyricsLookupStatus.Unavailable)
        {
            // 2s, 4s, 8s, 16s, then 30s, and 3 minutes once the network stays down.
            if (attempt >= 8) return TimeSpan.FromMinutes(3);
            return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Max(1, attempt))));
        }

        // 5s, 10s, 20s, 40s, then once a minute, then once every 5 minutes.
        if (attempt >= 8) return TimeSpan.FromMinutes(5);
        return TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Max(0, attempt - 1))));
    }

    private string StatusMessage()
    {
        if (_lookupRunning && _lookupStatus is null) return "正在查找歌词……";
        return _lookupStatus switch
        {
            LyricsLookupStatus.Unavailable => "歌词服务连接失败，正在重试……",
            LyricsLookupStatus.NotFound => "暂未找到歌词，正在重试……",
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
            if (IsWordBasedLyrics(current) && (string.IsNullOrWhiteSpace(next) || IsWordBasedLyrics(next)))
            {
                // Word-based languages read as stacked words: the current sentence on top and
                // the next sentence below it, never side by side.
                root.Child = CreateStackedWordLines(current, next, lyricsBrush, anchor);
            }
            else
            {
                var columns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = anchor, VerticalAlignment = _settings.LyricsVerticalAlignment switch { "Top" => VerticalAlignment.Top, "Bottom" => VerticalAlignment.Bottom, _ => VerticalAlignment.Center } };
                var currentBlock = CreateVerticalLine(current, 1.0, lyricsBrush);
                currentBlock.VerticalAlignment = VerticalAlignment.Top;
                currentBlock.Margin = new Thickness(0, 0, 18, 0);
                columns.Children.Add(currentBlock);
                if (!string.IsNullOrWhiteSpace(next))
                {
                    var nextBlock = CreateVerticalLine(next, 0.55, lyricsBrush);
                    nextBlock.VerticalAlignment = VerticalAlignment.Top;
                    columns.Children.Add(nextBlock);
                }
                root.Child = columns;
            }
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
    /// Word-based sentence (English and other space-delimited scripts): every word is drawn
    /// horizontally on its own row, the current sentence above and the next sentence below.
    /// </summary>
    private StackPanel CreateStackedWordLines(string current, string next, Brush brush, HorizontalAlignment alignment)
    {
        double fontSize = Math.Clamp(_settings.LyricsFontSize, 10, 120);
        List<string> currentWords = SplitIntoWords(current);
        List<string> nextWords = string.IsNullOrWhiteSpace(next) ? new List<string>() : SplitIntoWords(next);
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

    /// <summary>True for space-delimited scripts without CJK/Japanese/Korean characters.</summary>
    private static bool IsWordBasedLyrics(string text)
    {
        bool hasWord = false;
        foreach (char c in text)
        {
            if (IsCjk(c)) return false;
            if (char.IsLetterOrDigit(c)) hasWord = true;
        }
        return hasWord;
    }

    private static bool IsCjk(char c) =>
        (c >= '\u3040' && c <= '\u30ff') || (c >= '\u3400' && c <= '\u9fff') || (c >= '\uf900' && c <= '\ufaff')
        || (c >= '\uff66' && c <= '\uff9d') || (c >= '\uac00' && c <= '\ud7af');

    private TextBlock CreateText(string text, double opacity, Brush brush) => new() { Text = text, Foreground = brush, FontSize = Math.Clamp(_settings.LyricsFontSize, 10, 120), Opacity = opacity, TextWrapping = TextWrapping.Wrap, TextAlignment = _settings.LyricsAlignment switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center } };

    /// <summary>One vertical column cell: either a single glyph or a whole word drawn horizontally.</summary>
    private readonly record struct VerticalToken(string Text, bool IsWord);

    private StackPanel CreateVerticalLine(string text, double opacity, Brush brush)
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
        // the column wrap into additional columns when the window is too short.
        double available = Math.Max(fontSize * 2, (ActualHeight > 30 ? ActualHeight : Height) - 26);
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
        var cell = new TextBlock
        {
            Text = word,
            // Word cells keep Latin metrics, so the glyph font used for CJK fallback is not needed.
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = brush,
            FontSize = fontSize,
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
