namespace AppleMusicOverlay.Models;

public sealed class OverlaySettings
{
    public int SchemaVersion { get; set; } = 3;
    public bool CloseToTray { get; set; } = true;
    public bool HideWindowOnStartup { get; set; }
    public bool StartWithWindows { get; set; }
    public string KeyboardPrevious { get; set; } = "Ctrl+Shift+Left";
    public string KeyboardNext { get; set; } = "Ctrl+Shift+Right";
    public string KeyboardToggle { get; set; } = "Ctrl+Shift+Space";
    public string KeyboardVolumeUp { get; set; } = "Ctrl+Shift+Up";
    public string KeyboardVolumeDown { get; set; } = "Ctrl+Shift+Down";
}
