using System.Runtime.InteropServices;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

internal sealed class XInputGamepadReader
{
    private readonly Dictionary<int, TriggerState> _triggerStatesBySlot = new();

    public IReadOnlyList<XInputControllerSnapshot> ReadSlots()
    {
        List<XInputControllerSnapshot> snapshots = new();
        for (uint slot = 0; slot < 4; slot++)
        {
            uint result = XInputGetState(slot, out XInputState state);
            if (result != 0)
            {
                snapshots.Add(new XInputControllerSnapshot((int)slot, Connected: false, 0, new HashSet<GamepadButton>()));
                continue;
            }

            snapshots.Add(new XInputControllerSnapshot(
                (int)slot,
                Connected: true,
                state.PacketNumber,
                ReadButtons((int)slot, state.Gamepad)));
        }

        return snapshots;
    }

    public void ResetSlot(int slot)
    {
        _triggerStatesBySlot.Remove(slot);
    }

    public void Reset()
    {
        _triggerStatesBySlot.Clear();
    }

    private IReadOnlySet<GamepadButton> ReadButtons(int slot, XInputGamepad gamepad)
    {
        HashSet<GamepadButton> pressed = new();
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.A, GamepadButton.FaceSouth);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.B, GamepadButton.FaceEast);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.X, GamepadButton.FaceWest);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.Y, GamepadButton.FaceNorth);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.LeftShoulder, GamepadButton.LeftShoulder);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.RightShoulder, GamepadButton.RightShoulder);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.Back, GamepadButton.View);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.Start, GamepadButton.Menu);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.DPadUp, GamepadButton.DPadUp);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.DPadDown, GamepadButton.DPadDown);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.DPadLeft, GamepadButton.DPadLeft);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.DPadRight, GamepadButton.DPadRight);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.LeftThumb, GamepadButton.LeftStick);
        AddIfPressed(pressed, gamepad.Buttons, XInputButtons.RightThumb, GamepadButton.RightStick);

        TriggerState triggerState = GetTriggerState(slot);
        triggerState.LeftPressed = GamepadButtonReader.UpdateAnalogButton(triggerState.LeftPressed, gamepad.LeftTrigger / 255d);
        triggerState.RightPressed = GamepadButtonReader.UpdateAnalogButton(triggerState.RightPressed, gamepad.RightTrigger / 255d);
        if (triggerState.LeftPressed)
        {
            pressed.Add(GamepadButton.LeftTrigger);
        }

        if (triggerState.RightPressed)
        {
            pressed.Add(GamepadButton.RightTrigger);
        }

        return pressed;
    }

    private TriggerState GetTriggerState(int slot)
    {
        if (!_triggerStatesBySlot.TryGetValue(slot, out TriggerState? state))
        {
            state = new TriggerState();
            _triggerStatesBySlot[slot] = state;
        }

        return state;
    }

    private static void AddIfPressed(HashSet<GamepadButton> pressed, ushort buttons, ushort mask, GamepadButton button)
    {
        if ((buttons & mask) == mask)
        {
            pressed.Add(button);
        }
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    private sealed class TriggerState
    {
        public bool LeftPressed { get; set; }

        public bool RightPressed { get; set; }
    }

    private static class XInputButtons
    {
        public const ushort DPadUp = 0x0001;
        public const ushort DPadDown = 0x0002;
        public const ushort DPadLeft = 0x0004;
        public const ushort DPadRight = 0x0008;
        public const ushort Start = 0x0010;
        public const ushort Back = 0x0020;
        public const ushort LeftThumb = 0x0040;
        public const ushort RightThumb = 0x0080;
        public const ushort LeftShoulder = 0x0100;
        public const ushort RightShoulder = 0x0200;
        public const ushort A = 0x1000;
        public const ushort B = 0x2000;
        public const ushort X = 0x4000;
        public const ushort Y = 0x8000;
    }
}

internal sealed record XInputControllerSnapshot(
    int Slot,
    bool Connected,
    uint PacketNumber,
    IReadOnlySet<GamepadButton> Buttons);
