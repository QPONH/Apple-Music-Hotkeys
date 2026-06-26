namespace AppleMusicOverlay.Models;

public sealed class OverlaySettings
{
    public int SchemaVersion { get; set; } = 1;
    public DisplayStyle DisplayStyle { get; set; } = DisplayStyle.MinimalCover;
    public double LeftPercent { get; set; } = 0.04;
    public double TopPercent { get; set; } = 0.42;
    public double ScalePercent { get; set; } = 100;
    public int DisplaySeconds { get; set; } = 5;
    public double CoverShadowSizePercent { get; set; } = 80;
    public bool ShowTitle { get; set; } = true;
    public bool ShowArtist { get; set; } = true;
    public bool ShowControls { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public bool AutoStart { get; set; } = false;
    public bool PauseOverlay { get; set; } = false;
    public bool AutoHideOnMouseNear { get; set; } = false;
    public string OverlayTrackFont { get; set; } = "default";
    public string LanguageCode { get; set; } = "zh-CN";
    public string CaptureSourceAppUserModelId { get; set; } = string.Empty;
    public string KeyboardPrevious { get; set; } = "Ctrl+Shift+Left";
    public string KeyboardNext { get; set; } = "Ctrl+Shift+Right";
    public string KeyboardToggle { get; set; } = "Ctrl+Shift+Down";
    public string KeyboardTestOverlay { get; set; } = "Ctrl+Shift+Up";
    public GamepadBindingSet XboxGamepadBindings { get; set; } = new();
    public GamepadBindingSet DualSenseGamepadBindings { get; set; } = new();
    public GamepadBindingSet CompatibleGamepadBindings { get; set; } = new();
}
