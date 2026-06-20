using AppleMusicOverlay.Models;
using Windows.Gaming.Input;

namespace AppleMusicOverlay.Services;

public sealed class GamepadInputService : IDisposable
{
    private static readonly TimeSpan ReadInterval = TimeSpan.FromMilliseconds(1000d / 60d);

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, Gamepad> _standardGamepadsByRuntimeId = new(StringComparer.Ordinal);
    private readonly SynchronizationContext? _syncContext;
    private readonly XInputGamepadReader _xInputReader = new();
    private GamepadButtonReader _buttonReader = new();
    private CancellationTokenSource? _readLoopCts;
    private bool _started;
    private bool _disposed;
    private List<GamepadDeviceInfo> _devices = new();
    private string? _selectedRuntimeId;
    private int? _selectedXInputSlot;
    private IReadOnlySet<GamepadButton> _currentSelectedButtons = new HashSet<GamepadButton>();

    public event EventHandler? DevicesChanged;
    public event EventHandler<GamepadButtonsChangedEventArgs>? SelectedButtonsChanged;

    public GamepadInputService()
    {
        _syncContext = SynchronizationContext.Current;
    }

    public IReadOnlyList<GamepadDeviceInfo> Devices
    {
        get
        {
            lock (_syncRoot)
            {
                return _devices.ToList();
            }
        }
    }

    public GamepadDeviceInfo? SelectedDevice
    {
        get
        {
            lock (_syncRoot)
            {
                return _devices.FirstOrDefault(device => device.RuntimeId == _selectedRuntimeId);
            }
        }
    }

    public string? SelectedRuntimeId
    {
        get
        {
            lock (_syncRoot)
            {
                return _selectedRuntimeId;
            }
        }
    }

    public IReadOnlySet<GamepadButton> CurrentSelectedButtons
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentSelectedButtons.ToHashSet();
            }
        }
    }

    public bool IsReadLoopActive
    {
        get
        {
            lock (_syncRoot)
            {
                return _readLoopCts != null;
            }
        }
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
        {
            RefreshDevices();
            return;
        }

        _started = true;
        Gamepad.GamepadAdded += Gamepad_Changed;
        Gamepad.GamepadRemoved += Gamepad_Changed;
        RawGameController.RawGameControllerAdded += RawGameController_Changed;
        RawGameController.RawGameControllerRemoved += RawGameController_Changed;
        RefreshDevices();
    }

    public void RefreshDevices()
    {
        ThrowIfDisposed();
        IReadOnlyList<GamepadDeviceInfo> devices = EnumerateDevices(out Dictionary<string, Gamepad> standardGamepadsByRuntimeId);
        bool changed;
        lock (_syncRoot)
        {
            changed = HasDeviceListChanged(_devices, devices);
            _devices = devices.ToList();
            _standardGamepadsByRuntimeId.Clear();
            foreach ((string runtimeId, Gamepad gamepad) in standardGamepadsByRuntimeId)
            {
                _standardGamepadsByRuntimeId[runtimeId] = gamepad;
            }

            string? previousSelectedRuntimeId = _selectedRuntimeId;
            if (_devices.All(device => device.RuntimeId != _selectedRuntimeId))
            {
                _selectedRuntimeId = _devices.FirstOrDefault()?.RuntimeId;
                changed = true;
            }

            if (!string.Equals(previousSelectedRuntimeId, _selectedRuntimeId, StringComparison.Ordinal))
            {
                ResetSelectedButtonsLocked();
            }

            UpdateReadLoopLocked();
        }

        if (changed)
        {
            RaiseDevicesChanged();
        }
    }

    public void SelectDevice(string? runtimeId)
    {
        ThrowIfDisposed();
        bool changed = false;
        lock (_syncRoot)
        {
            if (!string.IsNullOrWhiteSpace(runtimeId) &&
                _devices.Any(device => device.RuntimeId == runtimeId) &&
                _selectedRuntimeId != runtimeId)
            {
                _selectedRuntimeId = runtimeId;
                changed = true;
                ResetSelectedButtonsLocked();
            }
        }

        if (changed)
        {
            RaiseDevicesChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_started)
        {
            Gamepad.GamepadAdded -= Gamepad_Changed;
            Gamepad.GamepadRemoved -= Gamepad_Changed;
            RawGameController.RawGameControllerAdded -= RawGameController_Changed;
            RawGameController.RawGameControllerRemoved -= RawGameController_Changed;
        }

        lock (_syncRoot)
        {
            StopReadLoopLocked();
            _devices.Clear();
            _standardGamepadsByRuntimeId.Clear();
            _selectedRuntimeId = null;
            _selectedXInputSlot = null;
            ResetSelectedButtonsLocked();
        }

        _disposed = true;
    }

    private static IReadOnlyList<GamepadDeviceInfo> EnumerateDevices(out Dictionary<string, Gamepad> standardGamepadsByRuntimeId)
    {
        List<Gamepad> gamepads = Gamepad.Gamepads.ToList();
        List<RawGameController> rawControllers = RawGameController.RawGameControllers.ToList();
        Dictionary<Gamepad, string> runtimeIdsByGamepad = new();
        standardGamepadsByRuntimeId = new Dictionary<string, Gamepad>(StringComparer.Ordinal);
        List<GamepadDeviceDescriptor> descriptors = new();

        foreach (Gamepad gamepad in gamepads)
        {
            RawGameController? raw = rawControllers.FirstOrDefault(controller => TryGetStandardGamepad(controller) == gamepad);
            string runtimeId = CreateRuntimeId(gamepad, raw);
            runtimeIdsByGamepad[gamepad] = runtimeId;
            standardGamepadsByRuntimeId[runtimeId] = gamepad;
            descriptors.Add(new GamepadDeviceDescriptor(
                runtimeId,
                raw?.DisplayName ?? "兼容手柄",
                raw?.HardwareVendorId ?? 0,
                raw?.HardwareProductId ?? 0,
                HasStandardGamepad: true,
                MatchedStandardRuntimeId: null));
        }

        foreach (RawGameController raw in rawControllers)
        {
            Gamepad? matchedGamepad = TryGetStandardGamepad(raw);
            string? matchedRuntimeId = matchedGamepad != null && runtimeIdsByGamepad.TryGetValue(matchedGamepad, out string? runtimeId)
                ? runtimeId
                : null;
            descriptors.Add(new GamepadDeviceDescriptor(
                GetRawRuntimeId(raw),
                raw.DisplayName,
                raw.HardwareVendorId,
                raw.HardwareProductId,
                HasStandardGamepad: false,
                matchedRuntimeId));
        }

        return GamepadDeviceClassifier.MergeDescriptors(descriptors);
    }

    private static string CreateRuntimeId(Gamepad gamepad, RawGameController? raw)
    {
        return raw == null ? $"gamepad:{gamepad.GetHashCode()}" : GetRawRuntimeId(raw);
    }

    private static string GetRawRuntimeId(RawGameController raw)
    {
        return string.IsNullOrWhiteSpace(raw.NonRoamableId)
            ? $"raw:{raw.HardwareVendorId:X4}:{raw.HardwareProductId:X4}:{raw.GetHashCode()}"
            : raw.NonRoamableId;
    }

    private static Gamepad? TryGetStandardGamepad(RawGameController raw)
    {
        try
        {
            return Gamepad.FromGameController(raw);
        }
        catch
        {
            return null;
        }
    }

    private static bool HasDeviceListChanged(IReadOnlyList<GamepadDeviceInfo> current, IReadOnlyList<GamepadDeviceInfo> next)
    {
        if (current.Count != next.Count)
        {
            return true;
        }

        for (int i = 0; i < current.Count; i++)
        {
            if (current[i] != next[i])
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateReadLoopLocked()
    {
        if (_devices.Count == 0)
        {
            StopReadLoopLocked();
            return;
        }

        if (_readLoopCts != null)
        {
            return;
        }

        _readLoopCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
    }

    private void StopReadLoopLocked()
    {
        _readLoopCts?.Cancel();
        _readLoopCts?.Dispose();
        _readLoopCts = null;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ReadInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Gamepad? selectedGamepad;
                GamepadDeviceInfo? selectedDevice;
                int? previousXInputSlot;
                lock (_syncRoot)
                {
                    selectedDevice = _devices.FirstOrDefault(device => device.RuntimeId == _selectedRuntimeId);
                    previousXInputSlot = _selectedXInputSlot;
                    selectedGamepad = _selectedRuntimeId != null &&
                                      _standardGamepadsByRuntimeId.TryGetValue(_selectedRuntimeId, out Gamepad? gamepad)
                        ? gamepad
                        : null;
                }

                if (selectedDevice?.Kind == GamepadDeviceKind.Xbox)
                {
                    IReadOnlyList<XInputControllerSnapshot> slots = _xInputReader.ReadSlots();
                    XInputControllerSnapshot? selectedSlot = SelectXInputSlot(slots, previousXInputSlot);
                    if (selectedSlot == null)
                    {
                        UpdateSelectedXInputSlot(null);
                        UpdateCurrentButtons(new HashSet<GamepadButton>());
                        continue;
                    }

                    if (selectedSlot.Slot != previousXInputSlot)
                    {
                        _xInputReader.ResetSlot(selectedSlot.Slot);
                        UpdateSelectedXInputSlot(selectedSlot.Slot);
                        UpdateCurrentButtons(new HashSet<GamepadButton>());
                    }

                    UpdateCurrentButtons(selectedSlot.Buttons);
                    continue;
                }

                if (selectedGamepad == null)
                {
                    UpdateSelectedXInputSlot(null);
                    UpdateCurrentButtons(new HashSet<GamepadButton>());
                    continue;
                }

                IReadOnlySet<GamepadButton> pressed = _buttonReader.Read(selectedGamepad.GetCurrentReading());
                UpdateSelectedXInputSlot(null);
                UpdateCurrentButtons(pressed);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Gamepad_Changed(object? sender, Gamepad gamepad)
    {
        RefreshDevicesSafely();
    }

    private void RawGameController_Changed(object? sender, RawGameController rawGameController)
    {
        RefreshDevicesSafely();
    }

    private void RefreshDevicesSafely()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            RefreshDevices();
        }
        catch
        {
        }
    }

    private void RaiseDevicesChanged()
    {
        if (_syncContext != null)
        {
            _syncContext.Post(_ => DevicesChanged?.Invoke(this, EventArgs.Empty), null);
            return;
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCurrentButtons(IReadOnlySet<GamepadButton> pressed)
    {
        bool changed;
        IReadOnlySet<GamepadButton> snapshot = pressed.ToHashSet();
        lock (_syncRoot)
        {
            changed = !_currentSelectedButtons.SetEquals(snapshot);
            if (!changed)
            {
                return;
            }

            _currentSelectedButtons = snapshot;
        }

        RaiseSelectedButtonsChanged(snapshot);
    }

    private void ResetSelectedButtonsLocked()
    {
        _buttonReader = new GamepadButtonReader();
        _xInputReader.Reset();
        _selectedXInputSlot = null;
        _currentSelectedButtons = new HashSet<GamepadButton>();
    }

    private void RaiseSelectedButtonsChanged(IReadOnlySet<GamepadButton> pressed)
    {
        SelectedButtonsChanged?.Invoke(this, new GamepadButtonsChangedEventArgs(pressed));
    }

    private void UpdateSelectedXInputSlot(int? slot)
    {
        lock (_syncRoot)
        {
            _selectedXInputSlot = slot;
        }
    }

    private static XInputControllerSnapshot? SelectXInputSlot(IReadOnlyList<XInputControllerSnapshot> slots, int? preferredSlot)
    {
        List<XInputControllerSnapshot> connected = slots.Where(slot => slot.Connected).ToList();
        if (connected.Count == 0)
        {
            return null;
        }

        if (connected.Count == 1)
        {
            return connected[0];
        }

        if (preferredSlot != null)
        {
            XInputControllerSnapshot? preferred = connected.FirstOrDefault(slot => slot.Slot == preferredSlot.Value);
            if (preferred != null)
            {
                return preferred;
            }
        }

        return connected.FirstOrDefault(slot => slot.Buttons.Count > 0) ?? connected[0];
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GamepadInputService));
        }
    }
}

public sealed class GamepadButtonsChangedEventArgs : EventArgs
{
    public GamepadButtonsChangedEventArgs(IReadOnlySet<GamepadButton> buttons)
    {
        Buttons = buttons.ToHashSet();
    }

    public IReadOnlySet<GamepadButton> Buttons { get; }
}
