using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public enum GamepadCaptureState
{
    WaitingForNeutral = 0,
    Listening = 1,
    Capturing = 2,
    SingleButtonWarning = 3,
    Conflict = 4,
    TooManyButtons = 5,
    Completed = 6,
    Cancelled = 7,
    DeviceDisconnected = 8
}

public sealed class GamepadBindingCaptureSession
{
    private static readonly TimeSpan NeutralStableDuration = TimeSpan.FromMilliseconds(100);
    private const int MaxButtons = 3;

    private readonly GamepadDeviceKind _deviceKind;
    private readonly AppAction _targetAction;
    private readonly GamepadBindingSet _bindings;
    private readonly HashSet<GamepadButton> _roundButtons = new();
    private readonly HashSet<GamepadButton> _pressedButtons = new();
    private TimeSpan _neutralElapsed;
    private bool _hasStartedRound;
    private static LocalizationService Localizer => LocalizationService.Current;

    public GamepadBindingCaptureSession(GamepadDeviceKind deviceKind, AppAction targetAction, GamepadBindingSet bindings)
    {
        _deviceKind = deviceKind;
        _targetAction = targetAction;
        _bindings = bindings;
        State = GamepadCaptureState.WaitingForNeutral;
        Message = Localizer.Text("GamepadReleaseAllButtons");
    }

    public GamepadCaptureState State { get; private set; }
    public GamepadBinding? PendingBinding { get; private set; }
    public AppAction? ConflictAction { get; private set; }
    public string DisplayText => PendingBinding != null
        ? GamepadBindingFormatter.Format(PendingBinding, _deviceKind)
        : GamepadBindingFormatter.FormatButtons(_roundButtons, _deviceKind);
    public string Message { get; private set; }

    public void Update(IEnumerable<GamepadButton> pressedButtons, TimeSpan elapsed)
    {
        if (State is GamepadCaptureState.Completed or GamepadCaptureState.Cancelled or GamepadCaptureState.Conflict or GamepadCaptureState.SingleButtonWarning or GamepadCaptureState.TooManyButtons or GamepadCaptureState.DeviceDisconnected)
        {
            return;
        }

        IReadOnlyList<GamepadButton> normalizedPressed = GamepadBindingOrder.Normalize(pressedButtons);
        if (State == GamepadCaptureState.WaitingForNeutral)
        {
            UpdateWaitingForNeutral(normalizedPressed, elapsed);
            return;
        }

        UpdateCaptureRound(normalizedPressed);
    }

    public void ConfirmSingleButton()
    {
        if (State != GamepadCaptureState.SingleButtonWarning || PendingBinding == null)
        {
            return;
        }

        CompleteOrReportConflict(PendingBinding);
    }

    public void Retry()
    {
        PendingBinding = null;
        ConflictAction = null;
        _roundButtons.Clear();
        _pressedButtons.Clear();
        _hasStartedRound = false;
        _neutralElapsed = TimeSpan.Zero;
        State = GamepadCaptureState.WaitingForNeutral;
        Message = Localizer.Text("GamepadReleaseAllButtons");
    }

    public void ReplaceConflict(GamepadBindingSet bindings)
    {
        if (State != GamepadCaptureState.Conflict || PendingBinding == null || ConflictAction == null)
        {
            return;
        }

        bindings.Clear(ConflictAction.Value);
        bindings.SetBinding(_targetAction, PendingBinding);
        State = GamepadCaptureState.Completed;
        Message = Localizer.Format("AutoSavedTemplate", DisplayText);
    }

    public void ApplyCompleted(GamepadBindingSet bindings)
    {
        if (State == GamepadCaptureState.Completed && PendingBinding != null)
        {
            bindings.SetBinding(_targetAction, PendingBinding);
        }
    }

    public void Cancel()
    {
        State = GamepadCaptureState.Cancelled;
        PendingBinding = null;
        ConflictAction = null;
    }

    public void Disconnect()
    {
        State = GamepadCaptureState.DeviceDisconnected;
        PendingBinding = null;
        ConflictAction = null;
        Message = Localizer.Text("GamepadDisconnectedCancelled");
    }

    private void UpdateWaitingForNeutral(IReadOnlyList<GamepadButton> normalizedPressed, TimeSpan elapsed)
    {
        if (normalizedPressed.Count > 0)
        {
            _neutralElapsed = TimeSpan.Zero;
            Message = Localizer.Text("GamepadReleaseAllButtons");
            return;
        }

        _neutralElapsed += elapsed;
        if (_neutralElapsed >= NeutralStableDuration)
        {
            State = GamepadCaptureState.Listening;
            Message = Localizer.Text("GamepadListenInstruction");
        }
    }

    private void UpdateCaptureRound(IReadOnlyList<GamepadButton> normalizedPressed)
    {
        _pressedButtons.Clear();
        foreach (GamepadButton button in normalizedPressed)
        {
            _pressedButtons.Add(button);
            if (_roundButtons.Add(button) && _roundButtons.Count > MaxButtons)
            {
                PendingBinding = null;
                State = GamepadCaptureState.TooManyButtons;
                Message = Localizer.Text("GamepadTooManyButtons");
                return;
            }
        }

        if (_pressedButtons.Count > 0)
        {
            _hasStartedRound = true;
            State = GamepadCaptureState.Capturing;
            Message = Localizer.Format("GamepadDetectedTemplate", DisplayText);
            return;
        }

        if (!_hasStartedRound)
        {
            State = GamepadCaptureState.Listening;
            Message = Localizer.Text("GamepadListenInstruction");
            return;
        }

        GamepadBinding binding = GamepadBinding.FromButtons(_roundButtons);
        PendingBinding = binding;
        if (binding.Buttons.Count == 1)
        {
            State = GamepadCaptureState.SingleButtonWarning;
            Message = Localizer.Text("GamepadSingleButtonWarning");
            return;
        }

        CompleteOrReportConflict(binding);
    }

    private void CompleteOrReportConflict(GamepadBinding binding)
    {
        AppAction? conflict = _bindings.FindAction(binding, _targetAction);
        if (conflict != null)
        {
            ConflictAction = conflict;
            State = GamepadCaptureState.Conflict;
            Message = Localizer.Format("HotkeyConflictTemplate", GamepadBindingFormatter.Format(binding, _deviceKind), GamepadBindingActions.GetLabel(conflict.Value));
            return;
        }

        State = GamepadCaptureState.Completed;
        Message = Localizer.Format("AutoSavedTemplate", GamepadBindingFormatter.Format(binding, _deviceKind));
    }
}
