namespace AppleMusicOverlay.Models;

public sealed record GamepadDeviceInfo(
    string RuntimeId,
    string DisplayName,
    GamepadDeviceKind Kind,
    bool HasStandardGamepad,
    ushort VendorId,
    ushort ProductId)
{
    public string KindDisplayName => Kind switch
    {
        GamepadDeviceKind.Xbox => "Xbox 手柄",
        GamepadDeviceKind.DualSense => "DualSense 手柄",
        _ => "兼容手柄"
    };

    public string StatusText => $"已连接 {KindDisplayName}";
}
