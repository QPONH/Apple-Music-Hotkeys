namespace AppleMusicOverlay.Models;

public sealed class OverlaySettings
{
    public int SchemaVersion { get; set; } = 7;
    public bool LyricsEnabled { get; set; }
    public double LyricsFontSize { get; set; } = 26;
    /// <summary>Base font size used by lyric translations, independently of the source lyric size.</summary>
    public double LyricsTranslationFontSize { get; set; } = 16;
    /// <summary>Overall opacity of the lyrics overlay (0.05 - 1.0).</summary>
    public double LyricsOpacity { get; set; } = 1.0;
    /// <summary>Hex foreground color of the lyric text, e.g. "#FFFFFF".</summary>
    public string LyricsColor { get; set; } = "#FFFFFF";
    public string LyricsCurrentColor { get; set; } = "#FFFFFF";
    public string LyricsNextColor { get; set; } = "#B8C0CC";
    public string LyricsAlignment { get; set; } = "Center";
    public string LyricsOrientation { get; set; } = "Horizontal";
    public string LyricsVerticalAlignment { get; set; } = "Center";
    /// <summary>Horizontal anchor of the lyric block in vertical mode: Left, Center or Right.</summary>
    public string LyricsVerticalAnchor { get; set; } = "Center";
    /// <summary>Vertical layout: RightStacked, Diagonal (upper-left/current and lower-right/next), or RightAligned (fixed right alignment with a larger sentence gap).</summary>
    public string LyricsWordVerticalLayout { get; set; } = "RightStacked";
    public string LyricsNonWordVerticalLayout { get; set; } = "Diagonal";
    /// <summary>Controls whether the next lyric is displayed, hidden, or replaced with the current line's translation.</summary>
    public string LyricsNextLineMode { get; set; } = "NextLine";
    public bool LyricsTranslationEnabled { get; set; }
    public string LyricsHorizontalTranslationLayout { get; set; } = "BottomRightSmall";
    /// <summary>Where translations are placed in the right-aligned vertical lyric layout.</summary>
    public string LyricsVerticalTranslationLayout { get; set; } = "BottomSmall";
    public int LyricsOffsetMilliseconds { get; set; }
    public bool LyricsBackgroundEnabled { get; set; } = true;
    public double LyricsWindowWidth { get; set; } = 620;
    public double LyricsWindowHeight { get; set; } = 180;
    public double? LyricsWindowLeft { get; set; }
    public double? LyricsWindowTop { get; set; }
    public bool CloseToTray { get; set; } = true;
    /// <summary>Set after the one-time hint that points at the notification area has been shown.</summary>
    public bool TrayHintShown { get; set; }
    /// <summary>
    /// Forces WPF's software renderer. Hardware rendering on some GPU drivers crashes the process
    /// without any managed exception (no crash.log entry), so this is the escape hatch for that.
    /// </summary>
    public bool SoftwareRendering { get; set; }
    public bool HideWindowOnStartup { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartupPlaybackRecovery { get; set; }
    public int RecoveryWaitSeconds { get; set; } = 2;
    public string RecoveryQueueMethod { get; set; } = "Keyboard";
    public string RecoverySongMethod { get; set; } = "Mouse";
    public int RecoveryQueueXPercent { get; set; } = 85;
    public int RecoveryQueueYPercent { get; set; } = 3;
    public int RecoverySongXPercent { get; set; } = 24;
    public int RecoverySongYPercent { get; set; } = 12;
    public string KeyboardPrevious { get; set; } = "Ctrl+Shift+Left";
    public string KeyboardNext { get; set; } = "Ctrl+Shift+Right";
    public string KeyboardToggle { get; set; } = "Ctrl+Shift+Space";
    public string KeyboardVolumeUp { get; set; } = "Ctrl+Shift+Up";
    public string KeyboardVolumeDown { get; set; } = "Ctrl+Shift+Down";
}
