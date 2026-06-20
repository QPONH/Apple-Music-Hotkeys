using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed class GamepadShortcutRuntime
{
    public static readonly TimeSpan SingleButtonConfirmationWindow = TimeSpan.FromMilliseconds(100);

    private GamepadBinding? _lockedBinding;
    private PendingSingle? _pendingSingle;
    private bool _suppressUntilNeutral;

    public AppAction? Update(
        IEnumerable<GamepadButton> pressedButtons,
        TimeSpan elapsed,
        GamepadBindingSet bindings,
        bool isCaptureActive = false)
    {
        IReadOnlySet<GamepadButton> pressed = GamepadBindingOrder.Normalize(pressedButtons).ToHashSet();
        if (isCaptureActive)
        {
            Reset();
            _suppressUntilNeutral = true;
            return null;
        }

        if (_suppressUntilNeutral)
        {
            if (pressed.Count == 0)
            {
                _suppressUntilNeutral = false;
            }

            return null;
        }

        if (_lockedBinding != null)
        {
            if (_lockedBinding.Buttons.Any(pressed.Contains))
            {
                return null;
            }

            _lockedBinding = null;
        }

        IReadOnlyList<RuntimeBinding> runtimeBindings = GetRuntimeBindings(bindings);
        RuntimeBinding? matchedCombo = runtimeBindings
            .Where(binding => binding.Binding.Buttons.Count > 1 && IsPressed(binding.Binding, pressed))
            .OrderByDescending(binding => binding.Binding.Buttons.Count)
            .FirstOrDefault();

        if (matchedCombo != null)
        {
            _pendingSingle = null;
            _lockedBinding = matchedCombo.Binding;
            return matchedCombo.Action;
        }

        if (_pendingSingle != null)
        {
            _pendingSingle.Elapsed += elapsed;
            bool stillPossibleCombo = runtimeBindings.Any(binding =>
                binding.Binding.Buttons.Count > 1 &&
                binding.Binding.Buttons.Contains(_pendingSingle.Binding.Buttons[0]) &&
                binding.Binding.Buttons.Any(button => button != _pendingSingle.Binding.Buttons[0] && pressed.Contains(button)));

            if (_pendingSingle.Elapsed >= SingleButtonConfirmationWindow && !stillPossibleCombo)
            {
                AppAction action = _pendingSingle.Action;
                _lockedBinding = _pendingSingle.Binding;
                _pendingSingle = null;
                return action;
            }
        }

        RuntimeBinding? matchedSingle = runtimeBindings.FirstOrDefault(binding =>
            binding.Binding.Buttons.Count == 1 && IsPressed(binding.Binding, pressed));
        if (matchedSingle == null)
        {
            return null;
        }

        bool hasLongerCombo = runtimeBindings.Any(binding =>
            binding.Binding.Buttons.Count > 1 &&
            binding.Binding.Buttons.Contains(matchedSingle.Binding.Buttons[0]));
        if (hasLongerCombo)
        {
            if (_pendingSingle == null || _pendingSingle.Binding.Key != matchedSingle.Binding.Key)
            {
                _pendingSingle = new PendingSingle(matchedSingle.Action, matchedSingle.Binding, TimeSpan.Zero);
            }

            return null;
        }

        _lockedBinding = matchedSingle.Binding;
        return matchedSingle.Action;
    }

    public void Reset()
    {
        _lockedBinding = null;
        _pendingSingle = null;
        _suppressUntilNeutral = false;
    }

    public void ResumeAfterCapture(IEnumerable<GamepadButton> currentButtons)
    {
        Reset();
        IReadOnlySet<GamepadButton> pressed = GamepadBindingOrder.Normalize(currentButtons).ToHashSet();
        _suppressUntilNeutral = pressed.Count > 0;
    }

    private static bool IsPressed(GamepadBinding binding, IReadOnlySet<GamepadButton> pressed)
    {
        return binding.Buttons.All(pressed.Contains);
    }

    private static IReadOnlyList<RuntimeBinding> GetRuntimeBindings(GamepadBindingSet bindings)
    {
        return GamepadBindingActions.SupportedActions
            .Select(action => new RuntimeBinding(action, bindings.GetBinding(action)))
            .Where(binding => !binding.Binding.IsEmpty)
            .OrderByDescending(binding => binding.Binding.Buttons.Count)
            .ToList();
    }

    private sealed record RuntimeBinding(AppAction Action, GamepadBinding Binding);

    private sealed record PendingSingle(AppAction Action, GamepadBinding Binding, TimeSpan Elapsed)
    {
        public TimeSpan Elapsed { get; set; } = Elapsed;
    }
}
