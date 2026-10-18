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
    private readonly LyricsService _service;
    private readonly OverlaySettings _settings;
    private readonly OverlaySettingsService _settingsService = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private string _trackKey = string.Empty;

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
        SourceInitialized += (_, _) => HideFromAltTab();
        PreviewMouseLeftButtonDown += DragAnywhere;
        Loaded += async (_, _) => { _timer.Start(); await UpdateAsync(); };
        Closed += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => SaveBounds();
        LocationChanged += (_, _) => SaveBounds();
        _timer.Tick += async (_, _) => await UpdateAsync();
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
        if (exitApplication is not null)
            menu.Items.Add(new MenuItem { Header = "退出 MusicFloat", Command = new RelayCommand(exitApplication) });
        if (menu.Items.Count > 0) ContextMenu = menu;
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
        try
        {
            var playback = await _service.GetCurrentPlaybackAsync();
            if (playback is null) { Render("未识别到正在播放的 Apple Music 歌曲", ""); return; }
            string key = $"{playback.Title}\n{playback.Artist}";
            if (key != _trackKey)
            {
                _trackKey = key;
                _lines = (await _service.GetLyricsAsync(playback.Title, playback.Artist, playback.Duration))?.Lines ?? Array.Empty<LyricLine>();
            }
            if (_lines.Count == 0) { Render("未找到歌词", ""); return; }
            // Positive offset means lyrics should appear later, so evaluate the timeline earlier.
            TimeSpan adjustedPosition = playback.Position - TimeSpan.FromMilliseconds(_settings.LyricsOffsetMilliseconds);
            int index = _lines.TakeWhile(line => line.Start <= adjustedPosition).Count() - 1;
            if (index < 0) index = 0;
            Render(_lines[index].Text, index + 1 < _lines.Count ? _lines[index + 1].Text : string.Empty);
        }
        catch (Exception ex) { CrashLog.Write(ex, "LyricsWindow"); Render("歌词读取异常；详情已写入日志", ""); }
    }

    private void Render(string current, string next)
    {
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
        if (!IsLoaded || double.IsNaN(Left) || double.IsNaN(Top)) return;
        _settings.LyricsWindowWidth = ActualWidth; _settings.LyricsWindowHeight = ActualHeight; _settings.LyricsWindowLeft = Left; _settings.LyricsWindowTop = Top;
        _settingsService.Save(_settings);
    }

    private void HideFromAltTab()
    {
        var helper = new WindowInteropHelper(this); const int GwlExstyle = -20, WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
        IntPtr style = GetWindowLongPtr(helper.Handle, GwlExstyle);
        SetWindowLongPtr(helper.Handle, GwlExstyle, new IntPtr((style.ToInt64() | WsExToolWindow) & ~WsExAppWindow));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
