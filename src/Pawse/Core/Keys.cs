namespace Pawse.Core;

/// <summary>
/// Virtual-key constants, modifier normalization, VK↔name mapping (for the
/// config's chord strings) and a small VK→char map used by the passphrase
/// matcher. Kept deliberately simple - no layout awareness.
/// </summary>
public static class Keys
{
    // Modifiers (side-specific as reported by WH_KEYBOARD_LL)
    public const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    public const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    // Generic modifiers (normalized targets)
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    public const int VK_SPACE = 0x20, VK_ESCAPE = 0x1B, VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D, VK_BACK = 0x08;

    /// <summary>Collapse left/right modifier variants onto one generic code so a
    /// chord like "Ctrl+Shift+U" matches either Ctrl and either Shift.</summary>
    public static int Normalize(int vk) => vk switch
    {
        VK_LSHIFT or VK_RSHIFT => VK_SHIFT,
        VK_LCONTROL or VK_RCONTROL => VK_CONTROL,
        VK_LMENU or VK_RMENU => VK_MENU,
        VK_RWIN => VK_LWIN, // treat both Win keys as one
        _ => vk,
    };

    /// <summary>Map a config key name (case-insensitive) to a normalized VK.</summary>
    public static int? NameToVk(string raw)
    {
        var name = raw.Trim().ToUpperInvariant();
        switch (name)
        {
            case "CTRL": case "CONTROL": return VK_CONTROL;
            case "SHIFT": return VK_SHIFT;
            case "ALT": return VK_MENU;
            case "WIN": case "SUPER": case "META": case "CMD": return VK_LWIN;
            case "SPACE": return VK_SPACE;
            case "ESC": case "ESCAPE": return VK_ESCAPE;
            case "TAB": return VK_TAB;
            case "ENTER": case "RETURN": return VK_RETURN;
            case "BACKSPACE": case "BACK": return VK_BACK;
        }
        if (name.Length == 1)
        {
            char c = name[0];
            if (c is >= 'A' and <= 'Z') return c;             // 'A'..'Z' == 0x41..0x5A
            if (c is >= '0' and <= '9') return c;             // '0'..'9' == 0x30..0x39
        }
        if (name.Length >= 2 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
            return 0x70 + (fn - 1);                            // F1..F24 == 0x70..0x87
        return null;
    }

    public static string VkToName(int vk) => vk switch
    {
        VK_CONTROL => "Ctrl",
        VK_SHIFT => "Shift",
        VK_MENU => "Alt",
        VK_LWIN => "Win",
        VK_SPACE => "Space",
        VK_ESCAPE => "Esc",
        VK_TAB => "Tab",
        VK_RETURN => "Enter",
        VK_BACK => "Backspace",
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),
        _ => "0x" + vk.ToString("X2"),
    };

    /// <summary>Parse a list of key names into a normalized VK set (unknowns dropped).</summary>
    public static HashSet<int> ParseChord(IEnumerable<string> names)
    {
        var set = new HashSet<int>();
        foreach (var n in names)
        {
            var vk = NameToVk(n);
            if (vk.HasValue) set.Add(Normalize(vk.Value));
        }
        return set;
    }

    /// <summary>Parse a "Ctrl+Shift+U" string into a normalized name list.</summary>
    public static List<string> ParseChordText(string text)
    {
        var list = new List<string>();
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var vk = NameToVk(part);
            if (vk.HasValue) list.Add(VkToName(Normalize(vk.Value)));
        }
        return list;
    }

    public static string ChordToText(IEnumerable<string> names) => string.Join("+", names);

    /// <summary>Best-effort VK→character for passphrase matching (letters, digits, space).</summary>
    public static char? TryVkToChar(int vk)
    {
        if (vk is >= 'A' and <= 'Z') return (char)('a' + (vk - 'A'));
        if (vk is >= '0' and <= '9') return (char)vk;
        if (vk == VK_SPACE) return ' ';
        return null;
    }
}
