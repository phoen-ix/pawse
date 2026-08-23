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
    [InlineData(/*lang=json*/ """{"Unlock": {"Chord": {"Enabled": true, "Keys": ["Ctrl", null]}}}""")]
    [InlineData(/*lang=json*/ """{"LockHotkey": {"Enabled": true, "Keys": [null, " ", "L"]}}""")]
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
        // Null/blank ELEMENTS are scrubbed too - a hand-edited ["Ctrl", null] used to
        // NRE inside HasUsableUnlock on every startup (and, being valid JSON, never
        // tripped the .bad-file recovery).
        Assert.DoesNotContain(cfg.Unlock.Chord.Keys, string.IsNullOrWhiteSpace);
        Assert.DoesNotContain(cfg.LockHotkey.Keys, string.IsNullOrWhiteSpace);
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

    /// <summary>The network stays untouched until the user asks for it - see UpdateCheck.</summary>
    [Fact]
    public void The_automatic_update_check_is_off_by_default()
    {
        var c = new Config();
        Assert.Equal(Config.UpdateMode.Manual, c.Update.ModeValue);
        Assert.Null(c.Update.LastCheckUtc);
    }

    [Fact]
    public void The_update_section_round_trips()
    {
        var c = new Config();
        c.Update.ModeValue = Config.UpdateMode.Automatic;
        c.Update.LastCheckUtc = new DateTime(2026, 8, 6, 10, 30, 0, DateTimeKind.Utc);
        c.Update.LastAutoAttemptVersion = "0.8.0";
        c.Update.LastAutoAttemptUtc = new DateTime(2026, 8, 6, 11, 0, 0, DateTimeKind.Utc);

        var back = Config.FromJson(c.ToJson());

        Assert.Equal(Config.UpdateMode.Automatic, back!.Update.ModeValue);
        Assert.Equal(c.Update.LastCheckUtc, back.Update.LastCheckUtc);
        Assert.Equal("0.8.0", back.Update.LastAutoAttemptVersion);
        Assert.Equal(c.Update.LastAutoAttemptUtc, back.Update.LastAutoAttemptUtc);
    }

    [Fact]
    public void A_nulled_update_section_is_reseeded()
    {
        var cfg = Config.FromJson("""{"Update": null}""");
        Assert.NotNull(cfg!.Update);
        Assert.Equal(Config.UpdateMode.Manual, cfg.Update.ModeValue);
    }
}

/// <summary>The three-level update setting, and the one-way migration from the bool it
/// replaced. The stakes on the parsing side are high: Config.Load answers a throw by keeping
/// the file as .bad and starting from defaults, so a typo here must never throw.</summary>
public class ConfigUpdateModeTests
{
    [Theory]
    [InlineData("Manual", Config.UpdateMode.Manual)]
    [InlineData("Notify", Config.UpdateMode.Notify)]
    [InlineData("Automatic", Config.UpdateMode.Automatic)]
    [InlineData("automatic", Config.UpdateMode.Automatic)]   // hand-edited, any casing
    [InlineData("  Notify  ", Config.UpdateMode.Notify)]     // and any stray whitespace
    [InlineData("1", Config.UpdateMode.Notify)]              // the number it sits at
    [InlineData("7", Config.UpdateMode.Manual)]              // a number no member sits at
    [InlineData("nonsense", Config.UpdateMode.Manual)]
    [InlineData("", Config.UpdateMode.Manual)]
    [InlineData(null, Config.UpdateMode.Manual)]
    public void An_unrecognised_mode_reads_as_manual_never_as_consent(string? text, Config.UpdateMode expected)
        => Assert.Equal(expected, Config.UpdateCfg.ParseMode(text));

    /// <summary>The whole reason Mode is a string and not an enum property.</summary>
    [Fact]
    public void A_typo_in_the_mode_costs_nothing_else_in_the_file()
    {
        var cfg = Config.FromJson("""{"Update":{"Mode":"nonsense"},"General":{"StartLocked":true}}""");

        Assert.Equal(Config.UpdateMode.Manual, cfg!.Update.ModeValue);
        Assert.True(cfg.General.StartLocked);   // the rest of the file survived
    }

    [Theory]
    [InlineData("""{"Update":{"AutoCheck":true}}""", Config.UpdateMode.Notify)]
    [InlineData("""{"Update":{"AutoCheck":false}}""", Config.UpdateMode.Manual)]
    [InlineData("""{"Update":{}}""", Config.UpdateMode.Manual)]
    public void The_old_auto_check_flag_migrates_once(string json, Config.UpdateMode expected)
        => Assert.Equal(expected, Config.FromJson(json)!.Update.ModeValue);

    /// <summary>A level already chosen wins over a stale flag left in the file.</summary>
    [Fact]
    public void An_explicit_mode_beats_the_old_flag()
    {
        var cfg = Config.FromJson("""{"Update":{"Mode":"Automatic","AutoCheck":true}}""");
        Assert.Equal(Config.UpdateMode.Automatic, cfg!.Update.ModeValue);
    }

    [Fact]
    public void The_old_flag_is_gone_from_the_next_save()
    {
        var cfg = Config.FromJson("""{"Update":{"AutoCheck":true}}""");
        Assert.DoesNotContain("AutoCheck", cfg!.ToJson());
    }
}

/// <summary>Which displays the lock popup lands on. Pure, so the awkward combinations - a
/// chosen monitor that is currently unplugged, a hand-edited list - are testable without a
/// screen attached.</summary>
public class OverlayDisplayTests
{
    private static Config.OverlayCfg Cfg(bool all, params int[] displays) =>
        new() { AllDisplays = all, Displays = displays.ToList() };

    [Fact]
    public void All_displays_covers_every_attached_one()
        => Assert.Equal(new[] { 0, 1, 2 }, Config.OverlayCfg.ResolveDisplays(Cfg(true), 3));

    /// <summary>Re-evaluated per call, which is the whole point of the option: plug a monitor
    /// in and it is covered without touching settings.</summary>
    [Fact]
    public void All_displays_follows_a_monitor_being_added()
    {
        var cfg = Cfg(true);
        Assert.Single(Config.OverlayCfg.ResolveDisplays(cfg, 1));
        Assert.Equal(2, Config.OverlayCfg.ResolveDisplays(cfg, 2).Count);
    }

    [Fact]
    public void A_selection_is_used_as_given()
        => Assert.Equal(new[] { 0, 2 }, Config.OverlayCfg.ResolveDisplays(Cfg(false, 0, 2), 3));

    [Fact]
    public void Displays_that_are_not_attached_are_skipped()
        => Assert.Equal(new[] { 1 }, Config.OverlayCfg.ResolveDisplays(Cfg(false, 1, 5), 2));

    [Fact]
    public void A_hand_edited_list_is_deduped_and_ordered()
        => Assert.Equal(new[] { 0, 1 }, Config.OverlayCfg.ResolveDisplays(Cfg(false, 1, 0, 1), 2));

    /// <summary>Undocked, having chosen only the external monitor. Showing nothing would leave a
    /// locked machine with no popup and so no on-screen hint of how to unlock it - worse than
    /// putting it somewhere unexpected.</summary>
    [Fact]
    public void Everything_chosen_being_unplugged_falls_back_to_the_primary()
        => Assert.Equal(new[] { 0 }, Config.OverlayCfg.ResolveDisplays(Cfg(false, 2), 1));

    [Fact]
    public void An_empty_selection_falls_back_to_the_primary()
        => Assert.Equal(new[] { 0 }, Config.OverlayCfg.ResolveDisplays(Cfg(false), 2));

    [Fact]
    public void No_screens_at_all_means_no_popup()
        => Assert.Empty(Config.OverlayCfg.ResolveDisplays(Cfg(true), 0));
}

/// <summary>The one-way migration from the single Monitor index.</summary>
public class OverlayMigrationTests
{
    [Fact]
    public void The_old_monitor_index_becomes_the_selected_display()
    {
        var cfg = Config.FromJson("""{"Overlay":{"Monitor":2}}""");
        Assert.Equal(new[] { 2 }, cfg!.Overlay.Displays);
        Assert.False(cfg.Overlay.AllDisplays);
    }

    [Fact]
    public void The_old_key_is_gone_from_the_next_save()
        => Assert.DoesNotContain("Monitor", Config.FromJson("""{"Overlay":{"Monitor":1}}""")!.ToJson());

    /// <summary>No build has ever written both keys, so a file carrying Monitor predates
    /// Displays and Monitor wins. Only reachable by hand-editing, and it self-corrects: the
    /// next save drops Monitor for good.</summary>
    [Fact]
    public void The_old_index_wins_when_a_hand_edited_file_has_both()
    {
        var cfg = Config.FromJson("""{"Overlay":{"Monitor":2,"Displays":[0,1]}}""");
        Assert.Equal(new[] { 2 }, cfg!.Overlay.Displays);
    }

    [Fact]
    public void All_displays_beats_the_old_index()
    {
        var cfg = Config.FromJson("""{"Overlay":{"Monitor":2,"AllDisplays":true}}""");
        Assert.True(cfg!.Overlay.AllDisplays);
    }

    [Fact]
    public void A_fresh_config_shows_on_the_primary_as_before()
    {
        var cfg = new Config();
        Assert.False(cfg.Overlay.AllDisplays);
        Assert.Equal(new[] { 0 }, cfg.Overlay.Displays);
    }

    [Fact]
    public void A_nulled_or_dirty_display_list_is_scrubbed()
    {
        Assert.NotNull(Config.FromJson("""{"Overlay":{"Displays":null}}""")!.Overlay.Displays);
        Assert.Equal(new[] { 0, 3 },
            Config.FromJson("""{"Overlay":{"Displays":[3,-1,0,3]}}""")!.Overlay.Displays);
    }

    [Fact]
    public void The_display_set_round_trips()
    {
        var cfg = new Config();
        cfg.Overlay.Displays = new List<int> { 0, 2 };
        Assert.Equal(new[] { 0, 2 }, Config.FromJson(cfg.ToJson())!.Overlay.Displays);
    }
}
