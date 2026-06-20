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

    public GamepadBindingCaptureSession(GamepadDeviceKind deviceKind, AppAction targetAction, GamepadBindingSet bindings)
    {
        _deviceKind = deviceKind;
        _targetAction = targetAction;
        _bindings = bindings;
        State = GamepadCaptureState.WaitingForNeutral;
        Message = "请先松开手柄上的所有按键。";
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
        Message = "请先松开手柄上的所有按键。";
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
        Message = $"已自动保存：{DisplayText}";
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
        Message = "手柄已断开，录入已取消。";
    }

    private void UpdateWaitingForNeutral(IReadOnlyList<GamepadButton> normalizedPressed, TimeSpan elapsed)
    {
        if (normalizedPressed.Count > 0)
        {
            _neutralElapsed = TimeSpan.Zero;
            Message = "请先松开手柄上的所有按键。";
            return;
        }

        _neutralElapsed += elapsed;
        if (_neutralElapsed >= NeutralStableDuration)
        {
            State = GamepadCaptureState.Listening;
            Message = "按下要绑定的手柄按键或组合键，松开所有按键后完成，Esc 取消。";
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
                Message = "最多可以绑定 3 个手柄按键，请重新录入。";
                return;
            }
        }

        if (_pressedButtons.Count > 0)
        {
            _hasStartedRound = true;
            State = GamepadCaptureState.Capturing;
            Message = $"已检测：{DisplayText}\n松开所有按键后完成，Esc 取消。";
            return;
        }

        if (!_hasStartedRound)
        {
            State = GamepadCaptureState.Listening;
            Message = "按下要绑定的手柄按键或组合键，松开所有按键后完成，Esc 取消。";
            return;
        }

        GamepadBinding binding = GamepadBinding.FromButtons(_roundButtons);
        PendingBinding = binding;
        if (binding.Buttons.Count == 1)
        {
            State = GamepadCaptureState.SingleButtonWarning;
            Message = "单个按键可能与游戏操作冲突，推荐使用组合键。";
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
            Message = $"{GamepadBindingFormatter.Format(binding, _deviceKind)} 已用于“{GamepadBindingActions.GetLabel(conflict.Value)}”。";
            return;
        }

        State = GamepadCaptureState.Completed;
        Message = $"已自动保存：{GamepadBindingFormatter.Format(binding, _deviceKind)}";
    }
}
