using LlamaApp;
using Xunit;

namespace LlamaApp.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData(0, 0x004Cu, "L")]
    [InlineData(0x0001u, 0x0020u, "Alt+Space")]
    [InlineData(0x0002u, 0x004Cu, "Ctrl+L")]
    [InlineData(0x000Eu, 0x004Cu, "Win+Ctrl+Shift+L")]
    public void Format_Returns_Readable_Hotkey(uint modifiers, uint key, string expected)
    {
        Assert.Equal(expected, Hotkey.Format(modifiers, key));
    }

    [Fact]
    public void Format_Without_Modifiers_Returns_Key_Name()
    {
        Assert.Equal("K", Hotkey.Format(0, 0x004Bu));
    }

    [Theory]
    [InlineData(0x0001u)] // Alt
    [InlineData(0x0002u)] // Ctrl
    [InlineData(0x0004u)] // Shift
    [InlineData(0x0008u)] // Win
    [InlineData(0x000Fu)] // All modifiers
    public void IsValid_Accepts_Supported_Modifiers(uint modifiers)
    {
        Assert.True(Hotkey.IsValid(modifiers, 0x004Bu));
    }

    [Theory]
    [InlineData(0u)]        // No modifiers
    [InlineData(0x4000u)]   // MOD_NOREPEAT alone is not a keyboard modifier
    public void IsValid_Requires_At_Least_One_Modifier(uint modifiers)
    {
        Assert.False(Hotkey.IsValid(modifiers, 0x004Bu));
    }

    [Fact]
    public void IsValid_Rejects_No_Key()
    {
        Assert.False(Hotkey.IsValid(0x0001u, 0));
    }

    [Theory]
    [InlineData(0x0011u)] // Control
    [InlineData(0x00A2u)] // LeftControl
    [InlineData(0x00A3u)] // RightControl
    [InlineData(0x0010u)] // Shift
    [InlineData(0x00A0u)] // LeftShift
    [InlineData(0x00A1u)] // RightShift
    [InlineData(0x0012u)] // Alt (Menu)
    [InlineData(0x00A4u)] // LeftAlt
    [InlineData(0x00A5u)] // RightAlt
    [InlineData(0x005Bu)] // LeftWin
    [InlineData(0x005Cu)] // RightWin
    public void IsValid_Rejects_Modifier_Keys_As_Main_Key(uint key)
    {
        Assert.False(Hotkey.IsValid(0x0001u, key));
    }
}
