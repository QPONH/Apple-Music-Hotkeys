namespace AppleMusicOverlay.Models;

public sealed class GamepadBinding
{
    public List<GamepadButton> Buttons { get; set; } = new();

    public bool IsEmpty => Buttons.Count == 0;

    public string Key => string.Join("+", Buttons.Select(button => button.ToString()));

    public static GamepadBinding Empty { get; } = new();

    public static GamepadBinding FromButtons(IEnumerable<GamepadButton> buttons)
    {
        return new GamepadBinding
        {
            Buttons = GamepadBindingOrder.Normalize(buttons).ToList()
        };
    }
}

public sealed class GamepadBindingSet
{
    public GamepadBinding Previous { get; set; } = new();
    public GamepadBinding Next { get; set; } = new();
    public GamepadBinding Toggle { get; set; } = new();
    public GamepadBinding ShowCurrent { get; set; } = new();
    public GamepadBinding Favorite { get; set; } = new();

    public GamepadBinding GetBinding(AppAction action)
    {
        return action switch
        {
            AppAction.PreviousTrack => Previous,
            AppAction.NextTrack => Next,
            AppAction.TogglePlayPause => Toggle,
            AppAction.ShowCurrentTrack => ShowCurrent,
            AppAction.FavoriteCurrentTrack => Favorite,
            _ => GamepadBinding.Empty
        };
    }

    public void SetBinding(AppAction action, GamepadBinding binding)
    {
        switch (action)
        {
            case AppAction.PreviousTrack:
                Previous = binding;
                break;
            case AppAction.NextTrack:
                Next = binding;
                break;
            case AppAction.TogglePlayPause:
                Toggle = binding;
                break;
            case AppAction.ShowCurrentTrack:
                ShowCurrent = binding;
                break;
            case AppAction.FavoriteCurrentTrack:
                Favorite = binding;
                break;
        }
    }

    public AppAction? FindAction(GamepadBinding binding, AppAction? exceptAction = null)
    {
        foreach (AppAction action in GamepadBindingActions.SupportedActions)
        {
            if (exceptAction == action)
            {
                continue;
            }

            GamepadBinding existing = GetBinding(action);
            if (!existing.IsEmpty && existing.Key.Equals(binding.Key, StringComparison.Ordinal))
            {
                return action;
            }
        }

        return null;
    }

    public void Clear(AppAction action)
    {
        SetBinding(action, new GamepadBinding());
    }
}

public static class GamepadBindingActions
{
    public static IReadOnlyList<AppAction> SupportedActions { get; } =
    [
        AppAction.PreviousTrack,
        AppAction.NextTrack,
        AppAction.TogglePlayPause,
        AppAction.ShowCurrentTrack,
        AppAction.FavoriteCurrentTrack
    ];

    public static string GetLabel(AppAction action)
    {
        AppleMusicOverlay.Services.LocalizationService localizer = AppleMusicOverlay.Services.LocalizationService.Current;
        return action switch
        {
            AppAction.PreviousTrack => localizer.Text("PreviousTrack"),
            AppAction.NextTrack => localizer.Text("NextTrack"),
            AppAction.TogglePlayPause => localizer.Text("TogglePlayPause"),
            AppAction.ShowCurrentTrack => localizer.Text("ShowOverlay"),
            AppAction.FavoriteCurrentTrack => localizer.Text("FavoriteCurrentTrack"),
            _ => localizer.Text("OtherAction")
        };
    }
}

public static class GamepadBindingOrder
{
    private static readonly Dictionary<GamepadButton, int> SortOrder = new()
    {
        [GamepadButton.View] = 0,
        [GamepadButton.Menu] = 1,
        [GamepadButton.LeftShoulder] = 2,
        [GamepadButton.RightShoulder] = 3,
        [GamepadButton.LeftTrigger] = 4,
        [GamepadButton.RightTrigger] = 5,
        [GamepadButton.DPadUp] = 6,
        [GamepadButton.DPadDown] = 7,
        [GamepadButton.DPadLeft] = 8,
        [GamepadButton.DPadRight] = 9,
        [GamepadButton.LeftStick] = 10,
        [GamepadButton.RightStick] = 11,
        [GamepadButton.FaceSouth] = 12,
        [GamepadButton.FaceEast] = 13,
        [GamepadButton.FaceWest] = 14,
        [GamepadButton.FaceNorth] = 15
    };

    public static IReadOnlyList<GamepadButton> Normalize(IEnumerable<GamepadButton> buttons)
    {
        return buttons
            .Distinct()
            .OrderBy(button => SortOrder.TryGetValue(button, out int order) ? order : int.MaxValue)
            .ToList();
    }
}
