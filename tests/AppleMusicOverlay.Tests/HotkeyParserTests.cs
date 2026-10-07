using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class HotkeyParserTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Right", 0x0002u | 0x0004u, 0x27u)]
    [InlineData("Alt+Left", 0x0001u, 0x25u)]
    [InlineData("Ctrl+Shift+Up", 0x0002u | 0x0004u, 0x26u)]
    [InlineData("Win+A", 0x0008u, 0x41u)]
    public void TryParseParsesSupportedCombinations(string text, uint modifiers, uint virtualKey)
    {
        bool ok = HotkeyParser.TryParse(text, out var result);

        Assert.True(ok);
        Assert.Equal(modifiers, result.Modifiers);
        Assert.Equal(virtualKey, result.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+UnknownKey")]
    [InlineData("Ctrl+Shift+Alt")]
    [InlineData("A+B")]
    public void TryParseRejectsInvalidInput(string text)
    {
        Assert.False(HotkeyParser.TryParse(text, out _));
    }
}
