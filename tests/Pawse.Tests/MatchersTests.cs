using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

public class ChordMatcherTests
{
    private static ChordMatcher CtrlL() => new(Keys.ParseChord(new[] { "Ctrl", "L" }));

    [Fact]
    public void Fires_exactly_on_the_key_down_that_completes_the_set()
    {
        var m = CtrlL();
        var pressed = new HashSet<int> { Keys.VK_CONTROL };
        Assert.False(m.Feed(pressed, Keys.VK_CONTROL, isDown: true));
        pressed.Add(0x4C);
        Assert.True(m.Feed(pressed, 0x4C, isDown: true));
    }

    [Fact]
    public void Does_not_fire_again_on_key_repeat_while_held()
    {
        var m = CtrlL();
        var pressed = new HashSet<int> { Keys.VK_CONTROL, 0x4C };
        Assert.True(m.Feed(pressed, 0x4C, isDown: true));
        Assert.False(m.Feed(pressed, 0x4C, isDown: true)); // OS auto-repeat
    }

    [Fact]
    public void Rearms_only_after_the_set_is_broken()
    {
        var m = CtrlL();
        var pressed = new HashSet<int> { Keys.VK_CONTROL, 0x4C };
        Assert.True(m.Feed(pressed, 0x4C, isDown: true));

        pressed.Remove(0x4C);
        Assert.False(m.Feed(pressed, 0x4C, isDown: false));

        pressed.Add(0x4C);
        Assert.True(m.Feed(pressed, 0x4C, isDown: true));
    }

    [Fact]
    public void Subset_mode_fires_with_extra_keys_held()
    {
        // Subset matching is the LOCK hotkey's mode: locking should be easy, so
        // Ctrl+Shift+L also triggers a Ctrl+L hotkey.
        var m = CtrlL();
        var pressed = new HashSet<int> { Keys.VK_CONTROL, Keys.VK_SHIFT, 0x4C };
        Assert.True(m.Feed(pressed, 0x4C, isDown: true));
    }

    [Fact]
    public void Does_not_fire_on_key_up()
    {
        var m = CtrlL();
        var pressed = new HashSet<int> { Keys.VK_CONTROL, 0x4C };
        Assert.False(m.Feed(pressed, 0x4C, isDown: false));
    }

    [Fact]
    public void Empty_target_never_fires()
    {
        var m = new ChordMatcher(new HashSet<int>());
        var pressed = new HashSet<int> { Keys.VK_CONTROL, 0x4C };
        Assert.False(m.Feed(pressed, 0x4C, isDown: true));
    }
}

/// <summary>Exact matching is the UNLOCK chord's mode: a cat sprawled over a dozen
/// keys that happen to include the chord must not unlock (see LockController).</summary>
public class ChordMatcherExactTests
{
    private static ChordMatcher CtrlLExact() =>
        new(Keys.ParseChord(new[] { "Ctrl", "L" }), requireExact: true);

    [Fact]
    public void Fires_when_the_held_set_equals_the_chord()
    {
        var m = CtrlLExact();
        var pressed = new HashSet<int> { Keys.VK_CONTROL };
        Assert.False(m.Feed(pressed, Keys.VK_CONTROL, isDown: true));
        pressed.Add(0x4C);
        Assert.True(m.Feed(pressed, 0x4C, isDown: true));
    }

    [Fact]
    public void Does_not_fire_with_extra_keys_held()
    {
        var m = CtrlLExact();
        var pressed = new HashSet<int> { Keys.VK_CONTROL, Keys.VK_SHIFT, 0x4C };
        Assert.False(m.Feed(pressed, 0x4C, isDown: true));
    }

    [Fact]
    public void Fires_after_extras_are_released_and_a_chord_key_is_pressed_again()
    {
        var m = CtrlLExact();
        var pressed = new HashSet<int> { Keys.VK_CONTROL, Keys.VK_SHIFT, 0x4C };
        Assert.False(m.Feed(pressed, 0x4C, isDown: true));

        pressed.Remove(Keys.VK_SHIFT);                          // extra released...
        Assert.False(m.Feed(pressed, Keys.VK_SHIFT, isDown: false)); // ...no fire on a key-up

        pressed.Remove(0x4C);
        Assert.False(m.Feed(pressed, 0x4C, isDown: false));
        pressed.Add(0x4C);                                      // deliberate re-press completes it
        Assert.True(m.Feed(pressed, 0x4C, isDown: true));
    }
}

public class PassphraseMatcherTests
{
    private static bool Type(PassphraseMatcher m, string input)
    {
        bool fired = false;
        foreach (char c in input) fired |= m.Feed(c);
        return fired;
    }

    [Fact]
    public void Completes_on_the_final_character_of_an_exact_match()
    {
        var m = new PassphraseMatcher("unlock", resetOnWrong: true);
        Assert.False(Type(m, "unloc"));
        Assert.True(m.Feed('k'));
    }

    [Fact]
    public void Matching_is_case_insensitive()
        => Assert.True(Type(new PassphraseMatcher("unlock", resetOnWrong: true), "UNLOCK"));

    [Fact]
    public void Wrong_key_restarts_progress_when_reset_is_on()
    {
        var m = new PassphraseMatcher("unlock", resetOnWrong: true);
        Assert.False(Type(m, "unx"));      // wrong key wipes progress...
        Assert.True(Type(m, "unlock"));    // ...so the full phrase works from scratch
    }

    [Fact]
    public void Wrong_key_that_is_the_first_letter_counts_as_a_fresh_start()
    {
        var m = new PassphraseMatcher("ab", resetOnWrong: true);
        Assert.False(m.Feed('a'));
        Assert.False(m.Feed('a')); // wrong for position 2, but restarts as position 1
        Assert.True(m.Feed('b'));
    }

    [Fact]
    public void Overlapping_prefixes_are_tracked()
    {
        // Consciously flipped when the matcher gained KMP failure links: "aaab"
        // contains "aab", and the mismatching third 'a' falls back to the "aa"
        // prefix instead of restarting from scratch.
        var m = new PassphraseMatcher("aab", resetOnWrong: true);
        Assert.True(Type(m, "aaab"));
    }

    [Fact]
    public void Fallback_keeps_multi_char_prefixes()
    {
        // "ababc": typing "abababc" mismatches at the second 'a' (expected 'c'),
        // falls back to the "abab" prefix ending there, and still completes.
        var m = new PassphraseMatcher("ababc", resetOnWrong: true);
        Assert.True(Type(m, "abababc"));
    }

    [Fact]
    public void Wrong_keys_are_ignored_when_reset_is_off()
    {
        var m = new PassphraseMatcher("unlock", resetOnWrong: false);
        Assert.True(Type(m, "uxnxlxoxcxk"));
    }

    [Fact]
    public void Reset_discards_progress()
    {
        var m = new PassphraseMatcher("unlock", resetOnWrong: false);
        Type(m, "unloc");
        m.Reset();
        Assert.False(m.Feed('k'));
        Assert.True(Type(m, "unlock"));
    }

    [Fact]
    public void Empty_text_never_fires()
        => Assert.False(Type(new PassphraseMatcher("", resetOnWrong: true), "anything"));
}
