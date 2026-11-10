using Windows.System;

namespace LlamaApp;

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000, // RegisterHotKey flag, not a physical key
    All = Alt | Control | Shift | Win, // all modifiers
}

internal static class Hotkey
{
    internal const uint DefaultModifiers = (uint)HotkeyModifiers.Alt;
    internal const uint DefaultVKey = (uint)VirtualKey.Space;

    /// <summary>
    /// Format the <paramref name="modifiers"/> and <paramref name="vKey"/> 
    /// combination into a readable friendly display string.
    /// </summary>
    public static string Format(uint modifiers, uint vKey)
    {
        var parts = new List<string>();
        if ((modifiers & (uint)HotkeyModifiers.Win) != 0) parts.Add("Win");
        if ((modifiers & (uint)HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & (uint)HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((modifiers & (uint)HotkeyModifiers.Alt) != 0) parts.Add("Alt");

        string keyName = vKey switch
        {
            0x20 => "Space",
            _ => ((Windows.System.VirtualKey)vKey).ToString()
        };
        parts.Add(keyName);

        return string.Join("+", parts);
    }

    /// <summary>Ensures that the given hotkey is a valid combination</summary>
    public static bool IsValid(uint modifiers, uint vKey)
    {
        // No virtual key pressed
        if (vKey == 0) return false;

        // At least one modifier key is pressed
        if ((modifiers & (uint)HotkeyModifiers.All) == 0) return false;

        // The virtual key must be a valid key (e.g. not a modifier)
        if (IsModifierKey(vKey)) return false;

        // All good
        return true;
    }

    /// <summary>Checks if the key is a modifier</summary>
    private static bool IsModifierKey(uint vKey) => (VirtualKey)vKey switch
    {
        VirtualKey.Control => true,
        VirtualKey.LeftControl => true,
        VirtualKey.RightControl => true,
        VirtualKey.Shift => true,
        VirtualKey.LeftShift => true,
        VirtualKey.RightShift => true,
        VirtualKey.Menu => true,
        VirtualKey.LeftMenu => true,
        VirtualKey.RightMenu => true,
        VirtualKey.LeftWindows => true,
        VirtualKey.RightWindows => true,
        _ => false
    };
}
