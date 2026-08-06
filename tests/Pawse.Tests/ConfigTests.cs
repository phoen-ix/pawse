using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

public class HasUsableUnlockTests
{
    /// <summary>Every unlock method off - the baseline the tests switch one method back on from.</summary>
    private static Config AllOff()
    {
        var c = new Config();
        c.Unlock.Chord.Enabled = false;
        c.Unlock.Passphrase.Enabled = false;
        c.Unlock.MouseHold.Enabled = false;
        c.Unlock.Timer.Enabled = false;
        return c;
    }

    [Fact]
    public void Default_config_is_usable() => Assert.True(new Config().HasUsableUnlock());

    [Fact]
    public void Everything_disabled_is_not_usable() => Assert.False(AllOff().HasUsableUnlock());

    [Fact]
    public void Enabled_chord_needs_at_least_one_parseable_key()
    {
        var c = AllOff();
        c.Unlock.Chord.Enabled = true;
        c.Unlock.Chord.Keys = new() { "Bogus", "Nope" };
        Assert.False(c.HasUsableUnlock());

        c.Unlock.Chord.Keys = new() { "Ctrl", "L" };
        Assert.True(c.HasUsableUnlock());
    }

    [Fact]
    public void Enabled_passphrase_needs_hook_typeable_text()
    {
        var c = AllOff();
        c.Unlock.Passphrase.Enabled = true;
        c.Unlock.Passphrase.Text = "p@ss!";
        Assert.False(c.HasUsableUnlock());

        c.Unlock.Passphrase.Text = "unlock";
        Assert.True(c.HasUsableUnlock());
    }

    [Fact]
    public void Mouse_hold_needs_the_overlay_shown_and_the_mouse_unblocked()
    {
        var c = AllOff();
        c.Unlock.MouseHold.Enabled = true;
        Assert.True(c.HasUsableUnlock());

        c.Overlay.Enabled = false; // no overlay -> no hold button to click
        Assert.False(c.HasUsableUnlock());

        c.Overlay.Enabled = true;
        c.General.BlockMouse = true; // blocked mouse -> button can't be clicked
        Assert.False(c.HasUsableUnlock());
    }

    [Fact]
    public void Timer_needs_a_positive_delay()
    {
        var c = AllOff();
        c.Unlock.Timer.Enabled = true;
        c.Unlock.Timer.Seconds = 0;
        Assert.False(c.HasUsableUnlock());

        c.Unlock.Timer.Seconds = 300;
        Assert.True(c.HasUsableUnlock());
    }
}

public class EnsureUsableUnlockFallbackTests
{
    [Fact]
    public void Usable_config_is_left_untouched()
    {
        var c = new Config();
        Assert.False(c.EnsureUsableUnlockFallback(out bool reseeded));
        Assert.False(reseeded);
    }

    [Fact]
    public void Reenables_a_disabled_but_parseable_chord_without_reseeding()
    {
        var c = new Config();
        c.Unlock.Chord.Enabled = false;
        c.Unlock.MouseHold.Enabled = false;

        Assert.True(c.EnsureUsableUnlockFallback(out bool reseeded));
        Assert.False(reseeded);
        Assert.True(c.Unlock.Chord.Enabled);
        Assert.Equal(new List<string> { "Ctrl", "L" }, c.Unlock.Chord.Keys);
    }

    [Fact]
    public void Reseeds_an_unparseable_chord_to_the_default()
    {
        var c = new Config();
        c.Unlock.Chord.Keys = new() { "Bogus" };
        c.Unlock.MouseHold.Enabled = false;

        Assert.True(c.EnsureUsableUnlockFallback(out bool reseeded));
        Assert.True(reseeded);
        Assert.True(c.Unlock.Chord.Enabled);
        Assert.Equal(new List<string> { "Ctrl", "L" }, c.Unlock.Chord.Keys);
        Assert.True(c.HasUsableUnlock());
    }
}

public class ConfigJsonTests
{
    [Fact]
    public void Round_trips_modified_values()
    {
        var c = new Config();
        c.General.StartLocked = true;
        c.General.BlockMouse = true;
        c.General.BlockScreenKeyboard = true;
        c.Unlock.Passphrase.Enabled = true;
        c.Unlock.Passphrase.Text = "let me in";
        c.Unlock.Timer.Seconds = 42;
        c.Overlay.Opacity = 0.5;
        c.SystemBlock.WinLock = true;

        var back = Config.FromJson(c.ToJson());

        Assert.NotNull(back);
        Assert.True(back!.General.StartLocked);
        Assert.True(back.General.BlockMouse);
        Assert.True(back.General.BlockScreenKeyboard);
        Assert.True(back.Unlock.Passphrase.Enabled);
        Assert.Equal("let me in", back.Unlock.Passphrase.Text);
        Assert.Equal(42, back.Unlock.Timer.Seconds);
        Assert.Equal(0.5, back.Overlay.Opacity);
        Assert.True(back.SystemBlock.WinLock);
    }

    // Regression suite for the startup NRE: System.Text.Json writes JSON null into
    // non-nullable properties, and HasUsableUnlock used to blow up on the first access.
    [Theory]
    [InlineData(/*lang=json*/ """{"Unlock": null}""")]
    [InlineData(/*lang=json*/ """{"Unlock": {"Chord": null}}""")]
    [InlineData(/*lang=json*/ """{"Unlock": {"Chord": {"Enabled": true, "Keys": null}}}""")]
    [InlineData(/*lang=json*/ """{"Unlock": {"Passphrase": {"Enabled": true, "Text": null}}}""")]
    [InlineData(/*lang=json*/ """{"General": null, "LockHotkey": null, "Overlay": null, "SystemBlock": null}""")]
    public void Nulled_sections_are_reseeded_with_defaults(string json)
    {
        var cfg = Config.FromJson(json);
        Assert.NotNull(cfg);
        _ = cfg!.HasUsableUnlock(); // must not throw
        Assert.NotNull(cfg.Unlock.Chord.Keys);
        Assert.NotNull(cfg.Unlock.Passphrase.Text);
        Assert.NotNull(cfg.General);
        Assert.NotNull(cfg.Overlay);
    }

    [Fact]
    public void Hand_edited_json_may_have_trailing_commas_comments_and_any_casing()
    {
        const string json = """
            {
                // hand-tuned
                "unlock": {
                    "chord": { "enabled": true, "keys": ["Ctrl", "L"], },
                },
            }
            """;
        var cfg = Config.FromJson(json);
        Assert.NotNull(cfg);
        Assert.True(cfg!.Unlock.Chord.Enabled);
        Assert.True(cfg.HasUsableUnlock());
    }

    [Fact]
    public void Literal_null_document_yields_null()
        => Assert.Null(Config.FromJson("null"));
}

public class ConfigDefaultsTests
{
    /// <summary>The lock is about the hardware keyboard: an on-screen / touch keyboard keeps
    /// working unless the user turns this on (LockController.OnKeyboard).</summary>
    [Fact]
    public void On_screen_keyboards_are_not_blocked_by_default()
        => Assert.False(new Config().General.BlockScreenKeyboard);

    [Fact]
    public void The_mouse_is_not_blocked_by_default()
        => Assert.False(new Config().General.BlockMouse);
}
