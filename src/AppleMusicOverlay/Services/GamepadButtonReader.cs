using AppleMusicOverlay.Models;
using Windows.Gaming.Input;

namespace AppleMusicOverlay.Services;

public sealed class GamepadButtonReader
{
    private const double TriggerPressThreshold = 0.65;
    private const double TriggerReleaseThreshold = 0.30;
    private bool _leftTriggerPressed;
    private bool _rightTriggerPressed;

    public IReadOnlySet<GamepadButton> Read(GamepadReading reading)
    {
        return Read(reading.LeftTrigger, reading.RightTrigger, reading.Buttons);
    }

    public IReadOnlySet<GamepadButton> Read(double leftTrigger, double rightTrigger, GamepadButtons buttons)
    {
        HashSet<GamepadButton> pressed = new();
        AddIfPressed(pressed, buttons, GamepadButtons.A, GamepadButton.FaceSouth);
        AddIfPressed(pressed, buttons, GamepadButtons.B, GamepadButton.FaceEast);
        AddIfPressed(pressed, buttons, GamepadButtons.X, GamepadButton.FaceWest);
        AddIfPressed(pressed, buttons, GamepadButtons.Y, GamepadButton.FaceNorth);
        AddIfPressed(pressed, buttons, GamepadButtons.LeftShoulder, GamepadButton.LeftShoulder);
        AddIfPressed(pressed, buttons, GamepadButtons.RightShoulder, GamepadButton.RightShoulder);
        AddIfPressed(pressed, buttons, GamepadButtons.View, GamepadButton.View);
        AddIfPressed(pressed, buttons, GamepadButtons.Menu, GamepadButton.Menu);
        AddIfPressed(pressed, buttons, GamepadButtons.DPadUp, GamepadButton.DPadUp);
        AddIfPressed(pressed, buttons, GamepadButtons.DPadDown, GamepadButton.DPadDown);
        AddIfPressed(pressed, buttons, GamepadButtons.DPadLeft, GamepadButton.DPadLeft);
        AddIfPressed(pressed, buttons, GamepadButtons.DPadRight, GamepadButton.DPadRight);
        AddIfPressed(pressed, buttons, GamepadButtons.LeftThumbstick, GamepadButton.LeftStick);
        AddIfPressed(pressed, buttons, GamepadButtons.RightThumbstick, GamepadButton.RightStick);

        _leftTriggerPressed = UpdateAnalogButton(_leftTriggerPressed, leftTrigger);
        _rightTriggerPressed = UpdateAnalogButton(_rightTriggerPressed, rightTrigger);
        if (_leftTriggerPressed)
        {
            pressed.Add(GamepadButton.LeftTrigger);
        }

        if (_rightTriggerPressed)
        {
            pressed.Add(GamepadButton.RightTrigger);
        }

        return pressed;
    }

    private static void AddIfPressed(HashSet<GamepadButton> pressed, GamepadButtons buttons, GamepadButtons mask, GamepadButton button)
    {
        if ((buttons & mask) == mask)
        {
            pressed.Add(button);
        }
    }

    internal static bool UpdateAnalogButton(bool previousPressed, double value)
    {
        if (previousPressed)
        {
            return value > TriggerReleaseThreshold;
        }

        return value >= TriggerPressThreshold;
    }
}
