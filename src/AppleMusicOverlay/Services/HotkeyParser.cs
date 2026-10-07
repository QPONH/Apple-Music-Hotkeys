using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public static class HotkeyParser
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Esc"] = 0x1B,
        ["Escape"] = 0x1B,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E
    };

    public static bool TryParse(string? text, out HotkeyDefinition hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint modifiers = 0;
        uint? virtualKey = null;
        string[] tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string token in tokens)
        {
            if (TryParseModifier(token, out uint modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (virtualKey.HasValue || !TryParseKey(token, out uint key))
            {
                return false;
            }

            virtualKey = key;
        }

        if (modifiers == 0 || !virtualKey.HasValue)
        {
            return false;
        }

        hotkey = new HotkeyDefinition(modifiers, virtualKey.Value);
        return true;
    }

    private static bool TryParseModifier(string token, out uint modifier)
    {
        modifier = token.ToUpperInvariant() switch
        {
            "ALT" => ModAlt,
            "CTRL" or "CONTROL" => ModControl,
            "SHIFT" => ModShift,
            "WIN" or "WINDOWS" => ModWin,
            _ => 0
        };

        return modifier != 0;
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        if (NamedKeys.TryGetValue(token, out virtualKey))
        {
            return true;
        }

        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z')
            {
                virtualKey = c;
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
        }

        if (token.Length is 2 or 3 && token[0] is 'F' or 'f' && int.TryParse(token[1..], out int fn) && fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + fn - 1);
            return true;
        }

        virtualKey = 0;
        return false;
    }
}
