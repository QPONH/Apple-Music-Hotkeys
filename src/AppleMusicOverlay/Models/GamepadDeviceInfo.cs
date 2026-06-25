namespace AppleMusicOverlay.Models;

public sealed record GamepadDeviceInfo(
    string RuntimeId,
    string DisplayName,
    GamepadDeviceKind Kind,
    bool HasStandardGamepad,
    ushort VendorId,
    ushort ProductId)
{
    private static AppleMusicOverlay.Services.LocalizationService Localizer => AppleMusicOverlay.Services.LocalizationService.Current;

    public string KindDisplayName => Kind switch
    {
        GamepadDeviceKind.Xbox => Localizer.Text("XboxGamepad"),
        GamepadDeviceKind.DualSense => Localizer.Text("DualSenseGamepad"),
        _ => Localizer.Text("CompatibleGamepad")
    };

    public string StatusText => Localizer.Format("ConnectedGamepadTemplate", KindDisplayName);
}
