using System.Diagnostics;
using System.Runtime.InteropServices;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public interface IGlobalShortcutSender
{
    Task<bool> TrySendAsync(
        HotkeyDefinition hotkey,
        CancellationToken cancellationToken = default);
}

public interface IHotkeyReleaseWaiter
{
    Task<bool> WaitForReleaseAsync(
        HotkeyDefinition hotkey,
        CancellationToken cancellationToken = default);
}

public sealed record GlobalShortcutDispatchPlan(
    IReadOnlyList<ushort> KeyDownOrder,
    IReadOnlyList<ushort> KeyUpOrder,
    TimeSpan HoldDuration);

public sealed class SendInputGlobalShortcutSender : IGlobalShortcutSender
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyAlt = 0x12;
    private const ushort VirtualKeyLeftWin = 0x5B;
    private static readonly TimeSpan DefaultHoldDuration = TimeSpan.FromMilliseconds(45);

    public static int NativeInputSize => Marshal.SizeOf<Input>();

    public async Task<bool> TrySendAsync(
        HotkeyDefinition hotkey,
        CancellationToken cancellationToken = default)
    {
        GlobalShortcutDispatchPlan plan = CreatePlan(hotkey);
        if (plan.KeyDownOrder.Count == 0 || plan.KeyUpOrder.Count == 0)
        {
            return false;
        }

        Input[] keyDownInputs = plan.KeyDownOrder
            .Select(key => CreateKeyboardInput(key, keyUp: false))
            .ToArray();
        Input[] keyUpInputs = plan.KeyUpOrder
            .Select(key => CreateKeyboardInput(key, keyUp: true))
            .ToArray();
        bool keyDownStarted = false;
        try
        {
            uint sentKeyDowns = SendInput(
                (uint)keyDownInputs.Length,
                keyDownInputs,
                NativeInputSize);
            keyDownStarted = sentKeyDowns > 0;
            if (sentKeyDowns != keyDownInputs.Length)
            {
                TryReleaseKeys(keyUpInputs);
                return false;
            }

            await Task.Delay(plan.HoldDuration, cancellationToken)
                .ConfigureAwait(false);
            uint sentKeyUps = SendInput(
                (uint)keyUpInputs.Length,
                keyUpInputs,
                NativeInputSize);
            if (sentKeyUps != keyUpInputs.Length)
            {
                TryReleaseKeys(keyUpInputs);
            }

            keyDownStarted = false;
            return sentKeyUps == keyUpInputs.Length;
        }
        catch (OperationCanceledException)
        {
            if (keyDownStarted)
            {
                TryReleaseKeys(keyUpInputs);
            }

            throw;
        }
        catch
        {
            if (keyDownStarted)
            {
                TryReleaseKeys(keyUpInputs);
            }

            return false;
        }
    }

    public static GlobalShortcutDispatchPlan CreatePlan(HotkeyDefinition hotkey)
    {
        var modifierKeys = new List<ushort>(4);
        AddModifier(modifierKeys, hotkey.Modifiers, ModControl, VirtualKeyControl);
        AddModifier(modifierKeys, hotkey.Modifiers, ModShift, VirtualKeyShift);
        AddModifier(modifierKeys, hotkey.Modifiers, ModAlt, VirtualKeyAlt);
        AddModifier(modifierKeys, hotkey.Modifiers, ModWin, VirtualKeyLeftWin);

        ushort mainKey = (ushort)hotkey.VirtualKey;
        ushort[] keyDownOrder = [.. modifierKeys, mainKey];
        ushort[] keyUpOrder = [mainKey, .. modifierKeys.AsEnumerable().Reverse()];
        return new GlobalShortcutDispatchPlan(
            keyDownOrder,
            keyUpOrder,
            DefaultHoldDuration);
    }

    private static void AddModifier(
        ICollection<ushort> keys,
        uint modifiers,
        uint modifier,
        ushort virtualKey)
    {
        if ((modifiers & modifier) != 0)
        {
            keys.Add(virtualKey);
        }
    }

    private static Input CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0
                }
            }
        };
    }

    private static void TryReleaseKeys(Input[] keyUpInputs)
    {
        try
        {
            SendInput(
                (uint)keyUpInputs.Length,
                keyUpInputs,
                NativeInputSize);
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // INPUT is a native union. Keeping every member is required even when
        // this sender only emits keyboard input: on x64 MOUSEINPUT is the
        // largest member and makes INPUT 40 bytes instead of 32.
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] Input[] inputs,
        int inputSize);
}

public sealed class AsyncKeyStateHotkeyReleaseWaiter : IHotkeyReleaseWaiter
{
    private const int KeyStateDownMask = 0x8000;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeyLeftShift = 0xA0;
    private const int VirtualKeyRightShift = 0xA1;
    private const int VirtualKeyLeftControl = 0xA2;
    private const int VirtualKeyRightControl = 0xA3;
    private const int VirtualKeyLeftAlt = 0xA4;
    private const int VirtualKeyRightAlt = 0xA5;
    private const int VirtualKeyLeftWin = 0x5B;
    private const int VirtualKeyRightWin = 0x5C;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private static readonly TimeSpan DefaultReleaseTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(12);
    private readonly TimeSpan _releaseTimeout;
    private readonly TimeSpan _pollInterval;

    public AsyncKeyStateHotkeyReleaseWaiter(
        TimeSpan? releaseTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _releaseTimeout = releaseTimeout ?? DefaultReleaseTimeout;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public async Task<bool> WaitForReleaseAsync(
        HotkeyDefinition hotkey,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        while (IsAnyHotkeyKeyDown(hotkey))
        {
            if (stopwatch.Elapsed >= _releaseTimeout)
            {
                return false;
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }

        return true;
    }

    private static bool IsAnyHotkeyKeyDown(HotkeyDefinition hotkey)
    {
        return IsKeyDown((int)hotkey.VirtualKey) ||
               HasPressedModifier(
                   hotkey.Modifiers,
                   ModControl,
                   VirtualKeyControl,
                   VirtualKeyLeftControl,
                   VirtualKeyRightControl) ||
               HasPressedModifier(
                   hotkey.Modifiers,
                   ModShift,
                   VirtualKeyShift,
                   VirtualKeyLeftShift,
                   VirtualKeyRightShift) ||
               HasPressedModifier(
                   hotkey.Modifiers,
                   ModAlt,
                   VirtualKeyAlt,
                   VirtualKeyLeftAlt,
                   VirtualKeyRightAlt) ||
               HasPressedModifier(
                   hotkey.Modifiers,
                   ModWin,
                   VirtualKeyLeftWin,
                   VirtualKeyLeftWin,
                   VirtualKeyRightWin);
    }

    private static bool HasPressedModifier(
        uint modifiers,
        uint modifier,
        int genericKey,
        int leftKey,
        int rightKey)
    {
        return (modifiers & modifier) != 0 &&
               (IsKeyDown(genericKey) ||
                IsKeyDown(leftKey) ||
                IsKeyDown(rightKey));
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & KeyStateDownMask) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
