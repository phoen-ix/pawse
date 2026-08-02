using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

public class KeysTests
{
    [Theory]
    [InlineData("Ctrl", Keys.VK_CONTROL)]
    [InlineData("Control", Keys.VK_CONTROL)]
    [InlineData("ctrl", Keys.VK_CONTROL)]
    [InlineData("Shift", Keys.VK_SHIFT)]
    [InlineData("Alt", Keys.VK_MENU)]
    [InlineData("Win", Keys.VK_LWIN)]
    [InlineData("Super", Keys.VK_LWIN)]
    [InlineData("Meta", Keys.VK_LWIN)]
    [InlineData("Cmd", Keys.VK_LWIN)]
    [InlineData("Space", Keys.VK_SPACE)]
    [InlineData("Esc", Keys.VK_ESCAPE)]
    [InlineData("Escape", Keys.VK_ESCAPE)]
    [InlineData("Enter", Keys.VK_RETURN)]
    [InlineData("Backspace", Keys.VK_BACK)]
    public void NameToVk_maps_named_keys_and_aliases(string name, int expected)
        => Assert.Equal(expected, Keys.NameToVk(name));

    [Theory]
    [InlineData("A", 0x41)]
    [InlineData("z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("9", 0x39)]
    [InlineData("F1", 0x70)]
    [InlineData("F12", 0x7B)]
    [InlineData("F24", 0x87)]
    public void NameToVk_maps_letters_digits_and_function_keys(string name, int expected)
        => Assert.Equal(expected, Keys.NameToVk(name));

    [Theory]
    [InlineData("")]
    [InlineData("Bogus")]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("0x41")] // VkToName's hex fallback is deliberately not parseable back
    [InlineData("Numpad5")]
    public void NameToVk_returns_null_for_unknown_names(string name)
        => Assert.Null(Keys.NameToVk(name));

    public static TheoryData<string> CanonicalNames() => new()
    {
        "Ctrl", "Shift", "Alt", "Win", "Space", "Esc", "Tab", "Enter", "Backspace",
        "A", "M", "Z", "0", "5", "9", "F1", "F12", "F24",
    };

    [Theory]
    [MemberData(nameof(CanonicalNames))]
    public void Canonical_names_round_trip_through_vk_and_back(string name)
    {
        int? vk = Keys.NameToVk(name);
        Assert.NotNull(vk);
        Assert.Equal(name, Keys.VkToName(Keys.Normalize(vk!.Value)));
    }

    [Theory]
    [InlineData(Keys.VK_LSHIFT, Keys.VK_SHIFT)]
    [InlineData(Keys.VK_RSHIFT, Keys.VK_SHIFT)]
    [InlineData(Keys.VK_LCONTROL, Keys.VK_CONTROL)]
    [InlineData(Keys.VK_RCONTROL, Keys.VK_CONTROL)]
    [InlineData(Keys.VK_LMENU, Keys.VK_MENU)]
    [InlineData(Keys.VK_RMENU, Keys.VK_MENU)]
    [InlineData(Keys.VK_RWIN, Keys.VK_LWIN)]
    [InlineData(0x41, 0x41)] // non-modifiers pass through
    public void Normalize_collapses_side_specific_modifiers(int raw, int expected)
        => Assert.Equal(expected, Keys.Normalize(raw));

    [Fact]
    public void ParseChord_drops_unknowns_and_normalizes()
    {
        var set = Keys.ParseChord(new[] { "Control", "shift", "U", "Bogus" });
        Assert.Equal(new HashSet<int> { Keys.VK_CONTROL, Keys.VK_SHIFT, 0x55 }, set);
    }

    [Fact]
    public void ParseChordText_canonicalizes_aliases_and_spacing()
        => Assert.Equal(new List<string> { "Ctrl", "L" }, Keys.ParseChordText(" control +  l "));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("unlock", true)]
    [InlineData("Unlock 42", true)] // upper case folds to typeable lower case
    [InlineData("pass!", false)]    // '!' can never register through the hook
    [InlineData("päw", false)]
    public void IsTypeablePassphrase_accepts_only_hook_typeable_text(string? text, bool expected)
        => Assert.Equal(expected, Keys.IsTypeablePassphrase(text));
}
