namespace AppleMusicOverlay.Models;

public sealed class OverlaySettings
{
    public int SchemaVersion { get; set; } = 5;
    public bool LyricsEnabled { get; set; }
    public double LyricsFontSize { get; set; } = 26;
    /// <summary>Overall opacity of the lyrics overlay (0.05 - 1.0).</summary>
    public double LyricsOpacity { get; set; } = 1.0;
    /// <summary>Hex foreground color of the lyric text, e.g. "#FFFFFF".</summary>
    public string LyricsColor { get; set; } = "#FFFFFF";
    public string LyricsAlignment { get; set; } = "Center";
    public string LyricsOrientation { get; set; } = "Horizontal";
    public string LyricsVerticalAlignment { get; set; } = "Center";
    /// <summary>Horizontal anchor of the lyric block in vertical mode: Left, Center or Right.</summary>
    public string LyricsVerticalAnchor { get; set; } = "Center";
    public int LyricsOffsetMilliseconds { get; set; }
    public bool LyricsBackgroundEnabled { get; set; } = true;
    public double LyricsWindowWidth { get; set; } = 620;
    public double LyricsWindowHeight { get; set; } = 180;
    public double? LyricsWindowLeft { get; set; }
    public double? LyricsWindowTop { get; set; }
    public bool CloseToTray { get; set; } = true;
    /// <summary>Set after the one-time hint that points at the notification area has been shown.</summary>
    public bool TrayHintShown { get; set; }
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
