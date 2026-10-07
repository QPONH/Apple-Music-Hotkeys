using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public static class GamepadBindingFormatter
{
    public static string Format(GamepadBinding? binding, GamepadDeviceKind kind)
    {
        if (binding == null || binding.IsEmpty)
        {
            return LocalizationService.Current.Text("ClickToBind");
        }

        return string.Join(" + ", binding.Buttons.Select(button => FormatButton(button, kind)));
    }

    public static string FormatButtons(IEnumerable<GamepadButton> buttons, GamepadDeviceKind kind)
    {
        IReadOnlyList<GamepadButton> normalized = GamepadBindingOrder.Normalize(buttons);
        return normalized.Count == 0 ? LocalizationService.Current.Text("Listening") : string.Join(" + ", normalized.Select(button => FormatButton(button, kind)));
    }

    public static string FormatButton(GamepadButton button, GamepadDeviceKind kind)
    {
        return kind switch
        {
            GamepadDeviceKind.DualSense => FormatDualSense(button),
            GamepadDeviceKind.Xbox => FormatXbox(button),
            _ => FormatCompatible(button)
        };
    }

    private static string FormatDualSense(GamepadButton button)
    {
        return button switch
        {
            GamepadButton.FaceSouth => "×",
            GamepadButton.FaceEast => "○",
            GamepadButton.FaceWest => "□",
            GamepadButton.FaceNorth => "△",
            GamepadButton.LeftShoulder => "L1",
            GamepadButton.RightShoulder => "R1",
            GamepadButton.LeftTrigger => "L2",
            GamepadButton.RightTrigger => "R2",
            GamepadButton.View => "Create",
            GamepadButton.Menu => "Options",
            GamepadButton.LeftStick => "L3",
            GamepadButton.RightStick => "R3",
            _ => FormatDirectionalOrCompatible(button)
        };
    }

    private static string FormatXbox(GamepadButton button)
    {
        return button switch
        {
            GamepadButton.FaceSouth => "A",
            GamepadButton.FaceEast => "B",
            GamepadButton.FaceWest => "X",
            GamepadButton.FaceNorth => "Y",
            GamepadButton.LeftShoulder => "LB",
            GamepadButton.RightShoulder => "RB",
            GamepadButton.LeftTrigger => "LT",
            GamepadButton.RightTrigger => "RT",
            GamepadButton.View => "View",
            GamepadButton.Menu => "Menu",
            GamepadButton.LeftStick => "LS",
            GamepadButton.RightStick => "RS",
            _ => FormatDirectionalOrCompatible(button)
        };
    }

    private static string FormatCompatible(GamepadButton button)
    {
        return button switch
        {
            GamepadButton.FaceSouth => LocalizationService.Current.Text("GamepadButtonSouth"),
            GamepadButton.FaceEast => LocalizationService.Current.Text("GamepadButtonEast"),
            GamepadButton.FaceWest => LocalizationService.Current.Text("GamepadButtonWest"),
            GamepadButton.FaceNorth => LocalizationService.Current.Text("GamepadButtonNorth"),
            _ => FormatDirectionalOrCompatible(button)
        };
    }

    private static string FormatDirectionalOrCompatible(GamepadButton button)
    {
        return button switch
        {
            GamepadButton.DPadUp => "↑",
            GamepadButton.DPadDown => "↓",
            GamepadButton.DPadLeft => "←",
            GamepadButton.DPadRight => "→",
            _ => button.ToString()
        };
    }
}
