using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using Pawse.Core;
using Pawse.UI;
using Xunit;
using static Pawse.Tests.E2E.Ui;
using Button = System.Windows.Controls.Button;
using Key = System.Windows.Input.Key;
using RadioButton = System.Windows.Controls.RadioButton;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Pawse.Tests.E2E;

/// <summary>The Settings window, page by page: every control round-trips through Save into the
/// config, Cancel changes nothing, and each warning, guard and state says what it should.</summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class SettingsE2ETests : IDisposable
{
    public void Dispose() => ResetDeployment();

    /// <summary>The defaults, with the popup on "All displays" so Save's display corrections
    /// stay out of tests that are not about them.</summary>
    private static Config Fixture()
    {
        var c = new Config();
        c.Overlay.AllDisplays = true;
        return c;
    }

    /// <summary>The window plus what App would hold: the lock state it reads live, and the
    /// recorder it opens (here: answers with <see cref="Recorded"/>).</summary>
    private sealed class Harness
    {
        public SettingsWindow Window { get; set; } = null!;
        public bool Locked { get; set; }
        public List<string>? Recorded { get; set; }
        public int RecorderOpened { get; set; }
        public int Applied { get; set; }
    }

    private static Harness Open(Config cfg, DeploymentMode mode = DeploymentMode.Installed, bool locked = false)
    {
        ForceDeployment(mode);
        var h = new Harness { Locked = locked };
        h.Window = new SettingsWindow(cfg, () => h.Locked, _ => { h.RecorderOpened++; return h.Recorded; });
        h.Window.Applied += () => h.Applied++;
        Show(h.Window);
        return h;
    }

    private static void Page(SettingsWindow w, string header)
    {
        var tabs = Descendants(w).OfType<TabControl>().Single();
        var tab = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == header);
        tab.IsSelected = true;
        Flush();
        Assert.True(tab.IsSelected);
    }

    private static void Save(SettingsWindow w) => Click(ByText<Button>(w, "Save"));

    private static bool Shows(TextBlock label, string text) =>
        label.Visibility == Visibility.Visible && label.Text == text;

    // ---- breadth: every page ----

    [Theory]
    [InlineData("General")]
    [InlineData("Locking")]
    [InlineData("Unlocking")]
    [InlineData("Lock popup")]
    [InlineData("About")]
    public void Every_page_opens_on_its_title(string page) => OnSta(() =>
    {
        var h = Open(Fixture());
        Page(h.Window, page);
        var title = Descendants(h.Window).OfType<TextBlock>().First(t => t.Text == page);
        Assert.True(title.IsVisible, $"the {page} page's title is not on screen");
        Assert.StartsWith("Pawse settings - v", h.Window.Title);
        h.Window.Close();
    });

    // ---- every checkbox round-trips through Save ----

    private static readonly Dictionary<string, Func<Config, bool>> Boxes = new()
    {
        ["Lock immediately when Pawse starts"] = c => c.General.StartLocked,
        ["Tray icon needs a double-click to unlock"] = c => c.General.TrayDoubleClickUnlock,
        ["Write a log file next to Pawse"] = c => c.General.Logging,
        ["Lock with a keyboard shortcut"] = c => c.LockHotkey.Enabled,
        ["Also block the mouse while locked"] = c => c.General.BlockMouse,
        ["Also block on-screen keyboards while locked"] = c => c.General.BlockScreenKeyboard,
        ["Block Win+L (lock-screen shortcut)"] = c => c.SystemBlock.WinLock,
        ["Block browser / calculator / media keys"] = c => c.SystemBlock.LaunchMediaKeys,
        ["Unlock with a key combination"] = c => c.Unlock.Chord.Enabled,
        ["Unlock by typing a lockphrase"] = c => c.Unlock.Lockphrase.Enabled,
        ["A wrong key restarts the lockphrase"] = c => c.Unlock.Lockphrase.ResetOnWrongKey,
        ["Unlock by holding the popup button or the paw icon"] = c => c.Unlock.MouseHold.Enabled,
        ["Automatically unlock after a delay"] = c => c.Unlock.Timer.Enabled,
        ["Show the floating lock popup"] = c => c.Overlay.Enabled,
        ["Show how to unlock beneath the paw"] = c => c.Overlay.Paw.ShowHint,
        ["Show the lockphrase in the popup"] = c => c.Overlay.ShowLockphrase,
    };

    public static TheoryData<string, string> BoxRows() => new()
    {
        { "General", "Lock immediately when Pawse starts" },
        { "General", "Tray icon needs a double-click to unlock" },
        { "General", "Write a log file next to Pawse" },
        { "Locking", "Lock with a keyboard shortcut" },
        { "Locking", "Also block the mouse while locked" },
        { "Locking", "Also block on-screen keyboards while locked" },
        { "Locking", "Block Win+L (lock-screen shortcut)" },
        { "Locking", "Block browser / calculator / media keys" },
        { "Unlocking", "Unlock with a key combination" },
        { "Unlocking", "Unlock by typing a lockphrase" },
        { "Unlocking", "A wrong key restarts the lockphrase" },
        { "Unlocking", "Unlock by holding the popup button or the paw icon" },
        { "Unlocking", "Automatically unlock after a delay" },
        { "Lock popup", "Show the floating lock popup" },
        { "Lock popup", "Show how to unlock beneath the paw" },
        { "Lock popup", "Show the lockphrase in the popup" },
    };

    [Theory]
    [MemberData(nameof(BoxRows))]
    public void A_checkbox_is_saved_into_the_config(string page, string label) => OnSta(() =>
    {
        var cfg = Fixture();
        bool before = Boxes[label](cfg);
        var h = Open(cfg);
        using var dialogs = new DialogWatch();
        Page(h.Window, page);
        // The paw's own options only show with the paw style picked.
        if (page == "Lock popup") Select(h.Window.RbStylePaw);
        var box = Box(h.Window, label);
        Assert.True(box.IsVisible, $"\"{label}\" is not on screen");
        Assert.Equal(before, box.IsChecked);
        Toggle(box);
        Save(h.Window);

        Assert.Equal(!before, Boxes[label](cfg));
        Assert.Equal(1, h.Applied);
        Assert.False(h.Window.IsVisible);
        Assert.Empty(dialogs.Texts);
    });

    [Fact]
    public void Start_at_sign_in_is_reported_only_when_changed() => OnSta(() =>
    {
        // The Run key IS the setting, so Save hands App a change instead of writing pawse.json.
        var h = Open(Fixture());
        var box = Box(h.Window, "Start Pawse when I sign in to Windows");
        bool before = box.IsChecked == true;
        Save(h.Window);
        Assert.Null(h.Window.AutostartChange);

        h = Open(Fixture());
        Toggle(Box(h.Window, "Start Pawse when I sign in to Windows"));
        Save(h.Window);
        Assert.Equal(!before, h.Window.AutostartChange);
    });

    [Fact]
    public void Cancel_leaves_the_config_untouched() => OnSta(() =>
    {
        var cfg = Fixture();
        string before = cfg.ToJson();
        var h = Open(cfg);
        Toggle(Box(h.Window, "Write a log file next to Pawse"));
        Page(h.Window, "Unlocking");
        Fill(h.Window.TxtTimerSeconds, "42");
        Click(ByText<Button>(h.Window, "Cancel"));

        Assert.False(h.Window.IsVisible);
        Assert.Equal(0, h.Applied);
        Assert.Equal(before, cfg.ToJson());
    });

    // ---- text and number fields ----

    [Fact]
    public void Lockphrase_and_durations_are_saved() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        Page(h.Window, "Unlocking");
        Check(Box(h.Window, "Unlock by typing a lockphrase"), true);
        Fill(h.Window.TxtLockphrase, "  purr loudly  ");
        Fill(h.Window.TxtHoldMs, "800");
        Fill(h.Window.TxtTimerSeconds, "90");
        Save(h.Window);

        Assert.Equal("purr loudly", cfg.Unlock.Lockphrase.Text);
        Assert.Equal(800, cfg.Unlock.MouseHold.HoldMs);
        Assert.Equal(90, cfg.Unlock.Timer.Seconds);
    });

    [Fact]
    public void Duration_boxes_take_digits_only() => OnSta(() =>
    {
        var h = Open(Fixture());
        Page(h.Window, "Unlocking");
        foreach (var box in new[] { h.Window.TxtHoldMs, h.Window.TxtTimerSeconds })
        {
            Assert.True(TypeRefused(box, "a"));
            Assert.True(TypeRefused(box, "-"));
            Assert.False(TypeRefused(box, "7"));
            Assert.True(PasteRefused(box, "12x"));
            Assert.False(PasteRefused(box, "120"));
        }
        h.Window.Close();
    });

    [Fact]
    public void Out_of_range_durations_warn_and_are_clamped_on_save() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        Page(h.Window, "Unlocking");
        Fill(h.Window.TxtHoldMs, "50");
        Assert.True(Shows(h.Window.LblHoldWarn,
            "Enter a whole number from 100 to 10000 milliseconds - anything else is corrected when you save."));
        Fill(h.Window.TxtTimerSeconds, "0");
        Assert.True(Shows(h.Window.LblTimerWarn,
            "Enter a whole number from 1 to 86400 seconds - anything else is corrected when you save."));
        Fill(h.Window.TxtTimerSeconds, "");
        Assert.Equal(Visibility.Visible, h.Window.LblTimerWarn.Visibility);

        Fill(h.Window.TxtTimerSeconds, "999999");
        Save(h.Window);
        Assert.Equal(Config.MouseHoldCfg.MinHoldMs, cfg.Unlock.MouseHold.HoldMs);
        Assert.Equal(Config.TimerCfg.MaxSeconds, cfg.Unlock.Timer.Seconds);
    });

    [Fact]
    public void A_valid_duration_clears_its_warning() => OnSta(() =>
    {
        var h = Open(Fixture());
        Page(h.Window, "Unlocking");
        Fill(h.Window.TxtHoldMs, "99999");
        Assert.Equal(Visibility.Visible, h.Window.LblHoldWarn.Visibility);
        Fill(h.Window.TxtHoldMs, "1500");
        Assert.Equal(Visibility.Collapsed, h.Window.LblHoldWarn.Visibility);
        h.Window.Close();
    });

    [Fact]
    public void Lockphrase_warnings_follow_what_is_typed() => OnSta(() =>
    {
        var h = Open(Fixture());
        Page(h.Window, "Unlocking");
        var warn = h.Window.LblLockphraseWarn;
        Fill(h.Window.TxtLockphrase, "purr!");
        Assert.Equal(Visibility.Collapsed, warn.Visibility);   // lockphrase is off: nothing to warn about

        Check(Box(h.Window, "Unlock by typing a lockphrase"), true);
        Assert.True(Shows(warn, "Only letters, digits and spaces can be typed while locked - "
                              + "this lockphrase could never unlock. Remove the other characters."));
        Fill(h.Window.TxtLockphrase, "   ");
        Assert.True(Shows(warn, "The lockphrase is empty - this unlock method won't do anything."));
        Fill(h.Window.TxtLockphrase, "purr 2");
        Assert.Equal(Visibility.Collapsed, warn.Visibility);
        h.Window.Close();
    });

    // ---- the two shortcut boxes ----

    [Fact]
    public void A_shortcut_is_recorded_by_pressing_it_in_the_box() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        Page(h.Window, "Locking");
        FocusArrives(h.Window.TxtLockHotkey);
        Assert.Equal("Press keys…", h.Window.TxtLockHotkey.Text);
        Chord(h.Window.TxtLockHotkey, Key.LeftCtrl, Key.LeftShift, Key.K);
        Assert.Equal("Ctrl+Shift+K", h.Window.TxtLockHotkey.Text);

        Page(h.Window, "Unlocking");
        FocusArrives(h.Window.TxtChord);
        Chord(h.Window.TxtChord, Key.RightAlt, Key.F9);
        Assert.Equal("Alt+F9", h.Window.TxtChord.Text);
        Save(h.Window);

        Assert.Equal(new[] { "Ctrl", "Shift", "K" }, cfg.LockHotkey.Keys);
        Assert.Equal(new[] { "Alt", "F9" }, cfg.Unlock.Chord.Keys);
    });

    [Fact]
    public void Esc_in_a_shortcut_box_keeps_the_old_shortcut() => OnSta(() =>
    {
        var h = Open(Fixture());
        Page(h.Window, "Unlocking");
        FocusArrives(h.Window.TxtChord);
        Key(h.Window.TxtChord, Key.LeftCtrl, down: true);
        Key(h.Window.TxtChord, Key.Escape, down: true);
        Flush();
        Assert.Equal(new[] { "Ctrl", "L" }, h.Window.TxtChord.Chord);
        Assert.Equal("Ctrl+L", h.Window.TxtChord.Text);
        h.Window.Close();
    });

    [Fact]
    public void A_key_that_cannot_be_a_shortcut_is_refused_and_the_old_one_kept() => OnSta(() =>
    {
        var h = Open(Fixture());
        Page(h.Window, "Unlocking");
        FocusArrives(h.Window.TxtChord);
        Chord(h.Window.TxtChord, Key.LeftCtrl, Key.Left);
        Assert.True(Shows(h.Window.LblChordWarn,
            "Left can't be part of a shortcut - use letters, digits, F-keys, Space, Tab, Enter or "
            + "Backspace with the modifiers. The previous shortcut was kept."));
        Assert.Equal(new[] { "Ctrl", "L" }, h.Window.TxtChord.Chord);
        h.Window.Close();
    });

    [Fact]
    public void A_modifiers_only_shortcut_warns() => OnSta(() =>
    {
        var h = Open(Fixture());
        Page(h.Window, "Locking");
        FocusArrives(h.Window.TxtLockHotkey);
        Chord(h.Window.TxtLockHotkey, Key.LeftCtrl, Key.LeftAlt);
        Assert.True(Shows(h.Window.LblLockHotkeyWarn,
            "This is modifiers-only - it triggers the instant you hold those keys. Add a normal key."));
        h.Window.Close();
    });

    [Fact]
    public void Set_opens_the_recorder_and_takes_what_it_captured() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        Page(h.Window, "Unlocking");
        h.Recorded = null;                                   // cancelled
        Click(h.Window.BtnSetChord);
        Assert.Equal(1, h.RecorderOpened);
        Assert.Equal("Ctrl+L", h.Window.TxtChord.Text);

        h.Recorded = new List<string> { "Ctrl", "Alt", "P" };
        Click(h.Window.BtnSetChord);
        Assert.Equal("Ctrl+Alt+P", h.Window.TxtChord.Text);

        Page(h.Window, "Locking");
        h.Recorded = new List<string> { "Win", "F12" };
        Click(h.Window.BtnSetLockHotkey);
        Assert.Equal("Win+F12", h.Window.TxtLockHotkey.Text);
        Save(h.Window);

        Assert.Equal(new[] { "Ctrl", "Alt", "P" }, cfg.Unlock.Chord.Keys);
        Assert.Equal(new[] { "Win", "F12" }, cfg.LockHotkey.Keys);
    });

    [Fact]
    public void Clear_empties_a_shortcut() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        Page(h.Window, "Locking");
        Click(ByText<Button>(LogicalTreeHelper.GetParent(h.Window.TxtLockHotkey), "Clear"));
        Assert.Equal("(not set)", h.Window.TxtLockHotkey.Text);
        Page(h.Window, "Unlocking");
        Click(ByText<Button>(LogicalTreeHelper.GetParent(h.Window.TxtChord), "Clear"));
        Assert.Equal("(not set)", h.Window.TxtChord.Text);
        Save(h.Window);

        Assert.Empty(cfg.LockHotkey.Keys);
        Assert.Empty(cfg.Unlock.Chord.Keys);
    });

    // ---- the lock popup page ----

    [Fact]
    public void Popup_style_position_opacity_and_size_are_saved() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        Page(h.Window, "Lock popup");
        Assert.Equal(Visibility.Collapsed, h.Window.PnlPawOptions.Visibility);
        Select(ByText<RadioButton>(h.Window, "Paw icon"));
        Assert.Equal(Visibility.Visible, h.Window.PnlPawOptions.Visibility);
        Slide(h.Window.SldOpacity, 0.5);
        Slide(h.Window.SldHorizontal, 20);
        Slide(h.Window.SldVertical, 80);
        Slide(h.Window.SldPawSize, 200);
        Save(h.Window);

        Assert.Equal(Config.OverlayStyle.Paw, cfg.Overlay.StyleValue);
        Assert.Equal(0.5, cfg.Overlay.Opacity, 3);
        Assert.Equal(20, cfg.Overlay.HorizontalPercent);
        Assert.Equal(80, cfg.Overlay.VerticalPercent);
        Assert.Equal(200, cfg.Overlay.Paw.Size);

        h = Open(cfg);
        Page(h.Window, "Lock popup");
        Select(ByText<RadioButton>(h.Window, "Window"));
        Assert.Equal(Visibility.Collapsed, h.Window.PnlPawOptions.Visibility);
        Save(h.Window);
        Assert.Equal(Config.OverlayStyle.Window, cfg.Overlay.StyleValue);
    });

    [Fact]
    public void Display_list_is_live_only_for_selected_displays() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        Page(h.Window, "Lock popup");
        Assert.Equal("All displays", h.Window.CmbDisplayMode.SelectedItem);
        Assert.False(h.Window.PnlDisplays.IsEnabled);
        h.Window.CmbDisplayMode.SelectedIndex = 1;
        Flush();
        Assert.True(h.Window.PnlDisplays.IsEnabled);
        // One box per attached display, "Display 1 - WxH (primary)" and so on.
        Assert.Equal(System.Windows.Forms.Screen.AllScreens.Length, h.Window.PnlDisplays.Children.Count);
        Assert.StartsWith("Display 1", (string)((System.Windows.Controls.CheckBox)h.Window.PnlDisplays.Children[0]).Content);
        h.Window.CmbDisplayMode.SelectedIndex = 0;
        Flush();
        Assert.False(h.Window.PnlDisplays.IsEnabled);
        Save(h.Window);
        Assert.True(cfg.Overlay.AllDisplays);
    });

    [Fact]
    public void Displays_that_are_unplugged_survive_a_save() => OnSta(() =>
    {
        var cfg = Fixture();
        cfg.Overlay.AllDisplays = false;
        cfg.Overlay.Displays = new List<int> { 7 };          // a docking-station monitor
        var h = Open(cfg);
        using var dialogs = new DialogWatch();
        Toggle(Box(h.Window, "Write a log file next to Pawse"));
        Save(h.Window);
        Assert.Equal(new[] { 7 }, cfg.Overlay.Displays);
        Assert.False(cfg.Overlay.AllDisplays);
        Assert.Empty(dialogs.Texts);
    });

    [Fact]
    public void Ticking_no_display_switches_the_popup_off_and_says_so() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        using var dialogs = new DialogWatch();
        Page(h.Window, "Lock popup");
        h.Window.CmbDisplayMode.SelectedIndex = 1;
        Flush();
        foreach (var box in h.Window.PnlDisplays.Children.OfType<System.Windows.Controls.CheckBox>())
            Check(box, false);
        Save(h.Window);

        Assert.False(cfg.Overlay.Enabled);
        Assert.Equal(new[] { "No display is selected for the lock popup, so it was switched off.\n\n"
                             + "Pick at least one display to show it again." }, dialogs.Texts);
    });

    [Fact]
    public void Ticking_every_display_becomes_all_displays_and_says_so() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        using var dialogs = new DialogWatch();
        Page(h.Window, "Lock popup");
        h.Window.CmbDisplayMode.SelectedIndex = 1;
        Flush();
        foreach (var box in h.Window.PnlDisplays.Children.OfType<System.Windows.Controls.CheckBox>())
            Check(box, true);
        Save(h.Window);

        Assert.True(cfg.Overlay.AllDisplays);
        Assert.Equal(new[] { "Every display was selected, so the lock popup is set to \"All displays\".\n\n"
                             + "It will follow any monitor you plug in or unplug from now on." }, dialogs.Texts);
    });

    [Fact]
    public void An_unrelated_save_says_nothing_about_displays() => OnSta(() =>
    {
        // Every attached display ticked - which is what a fresh config looks like on a PC with
        // one monitor. Saving the lockphrase used to answer with "Every display was selected".
        var cfg = new Config();
        cfg.Overlay.AllDisplays = false;
        cfg.Overlay.Displays = Enumerable.Range(0, System.Windows.Forms.Screen.AllScreens.Length).ToList();
        var h = Open(cfg);
        using var dialogs = new DialogWatch();
        Page(h.Window, "Unlocking");
        Check(Box(h.Window, "Unlock by typing a lockphrase"), true);
        Save(h.Window);

        Assert.Empty(dialogs.Texts);
        Assert.True(cfg.Unlock.Lockphrase.Enabled);
        Assert.True(cfg.Overlay.AllDisplays);                // still corrected, just not announced
    });

    // ---- the lockout guard ----

    [Fact]
    public void Saving_without_a_working_unlock_method_turns_the_chord_back_on() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        using var dialogs = new DialogWatch();
        Page(h.Window, "Unlocking");
        Check(Box(h.Window, "Unlock with a key combination"), false);
        Check(Box(h.Window, "Unlock by holding the popup button or the paw icon"), false);
        Save(h.Window);

        Assert.True(cfg.Unlock.Chord.Enabled);
        Assert.Equal(new[] { "Ctrl", "L" }, cfg.Unlock.Chord.Keys);
        Assert.Equal(new[] { "At least one working unlock method is required, so the keyboard chord was enabled." },
                     dialogs.Texts);
    });

    [Fact]
    public void An_empty_chord_is_reseeded_to_Ctrl_L_by_the_lockout_guard() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        using var dialogs = new DialogWatch();
        Page(h.Window, "Unlocking");
        Click(ByText<Button>(LogicalTreeHelper.GetParent(h.Window.TxtChord), "Clear"));
        Check(Box(h.Window, "Unlock by holding the popup button or the paw icon"), false);
        Save(h.Window);

        Assert.True(cfg.Unlock.Chord.Enabled);
        Assert.Equal(new[] { "Ctrl", "L" }, cfg.Unlock.Chord.Keys);
        Assert.Equal(new[] { "At least one working unlock method is required, so the keyboard chord was enabled"
                             + " and set to Ctrl+L." }, dialogs.Texts);
    });

    // ---- while locked ----

    [Fact]
    public void While_locked_the_banner_shows_and_typing_fields_are_off() => OnSta(() =>
    {
        var h = Open(Fixture(), locked: true);
        Assert.Equal(Visibility.Visible, h.Window.PnlLockedBanner.Visibility);
        Page(h.Window, "Unlocking");
        foreach (var box in new[] { h.Window.TxtLockphrase, h.Window.TxtHoldMs, h.Window.TxtTimerSeconds })
        {
            Assert.False(box.IsEnabled);
            Assert.Throws<ElementNotEnabledException>(() => Fill(box, "1"));
        }

        int asked = 0;
        h.Window.UnlockRequested += () => asked++;
        Click(h.Window.BtnBannerUnlock);
        Assert.Equal(1, asked);
        h.Window.Close();
    });

    [Fact]
    public void While_locked_no_shortcut_can_be_recorded() => OnSta(() =>
    {
        var h = Open(Fixture(), locked: true);
        Page(h.Window, "Unlocking");
        FocusArrives(h.Window.TxtChord);
        Assert.True(Shows(h.Window.LblChordWarn, "Unlock Pawse first to record a shortcut."));
        Assert.Equal("Ctrl+L", h.Window.TxtChord.Text);

        Page(h.Window, "Locking");
        Click(h.Window.BtnSetLockHotkey);
        Assert.Equal(0, h.RecorderOpened);
        Assert.True(Shows(h.Window.LblLockHotkeyWarn, "Unlock Pawse first to record a shortcut."));
        h.Window.Close();
    });

    [Fact]
    public void An_unlock_while_open_gives_the_fields_back() => OnSta(() =>
    {
        var h = Open(Fixture(), locked: true);
        Page(h.Window, "Unlocking");
        FocusArrives(h.Window.TxtChord);
        h.Locked = false;
        h.Window.SetLocked(false);
        Flush();
        Assert.Equal(Visibility.Collapsed, h.Window.PnlLockedBanner.Visibility);
        Assert.Equal(Visibility.Collapsed, h.Window.LblChordWarn.Visibility);
        Fill(h.Window.TxtTimerSeconds, "60");
        Assert.Equal("60", h.Window.TxtTimerSeconds.Text);
        h.Window.Close();
    });

    // ---- a portable copy ----

    [Fact]
    public void A_portable_copy_greys_out_what_would_touch_the_registry_and_keeps_its_values() => OnSta(() =>
    {
        var cfg = Fixture();
        cfg.SystemBlock.WinLock = true;
        var h = Open(cfg, DeploymentMode.Portable);
        foreach (var label in new[] { "Start Pawse when I sign in to Windows" })
            Assert.False(Box(h.Window, label).IsEnabled);
        Assert.Equal(Visibility.Visible, h.Window.LblAutostartPortable.Visibility);
        Page(h.Window, "Locking");
        Assert.False(Box(h.Window, "Block Win+L (lock-screen shortcut)").IsEnabled);
        Assert.False(Box(h.Window, "Block browser / calculator / media keys").IsEnabled);
        Assert.Throws<ElementNotEnabledException>(() => Toggle(Box(h.Window, "Block Win+L (lock-screen shortcut)")));
        Assert.Equal(Visibility.Visible, h.Window.LblSystemKeysPortable.Visibility);
        Page(h.Window, "About");
        Assert.Equal(Visibility.Visible, h.Window.LblPortable.Visibility);
        Save(h.Window);
        Assert.True(cfg.SystemBlock.WinLock);
    });

    [Fact]
    public void An_installed_copy_offers_every_option() => OnSta(() =>
    {
        var h = Open(Fixture());
        Assert.True(Box(h.Window, "Start Pawse when I sign in to Windows").IsEnabled);
        Assert.Equal(Visibility.Collapsed, h.Window.LblAutostartPortable.Visibility);
        Page(h.Window, "About");
        Assert.Equal(Visibility.Collapsed, h.Window.LblPortable.Visibility);
        h.Window.Close();
    });

    // ---- About ----

    [Fact]
    public void About_names_the_version_and_links_home() => OnSta(() =>
    {
        var h = Open(Fixture());
        Page(h.Window, "About");
        Assert.Equal(App.Version == App.DevVersion ? $"Pawse {App.Version} - development build" : $"Pawse {App.Version}",
                     h.Window.LblVersion.Text);
        var links = Descendants(h.Window).OfType<Hyperlink>().Select(l => l.NavigateUri.AbsoluteUri).ToList();
        Assert.Equal(new[] { "https://www.pawse.at/", "https://github.com/phoen-ix/pawse" }, links);
        h.Window.Close();
    });

    [Theory]
    [InlineData("Only when I ask", Config.UpdateMode.Manual)]
    [InlineData("Tell me when an update is ready", Config.UpdateMode.Notify)]
    [InlineData("Download and install updates automatically", Config.UpdateMode.Automatic)]
    public void The_update_mode_is_saved(string label, Config.UpdateMode mode) => OnSta(() =>
    {
        var cfg = Fixture();
        cfg.Update.ModeValue = mode == Config.UpdateMode.Manual ? Config.UpdateMode.Notify : Config.UpdateMode.Manual;
        var h = Open(cfg);
        Page(h.Window, "About");
        Select(ByText<RadioButton>(h.Window, label));
        Save(h.Window);
        Assert.Equal(mode, cfg.Update.ModeValue);
    });

    [Fact]
    public void Check_now_reports_progress_failure_and_result() => OnSta(() =>
    {
        var cfg = Fixture();
        var h = Open(cfg);
        Page(h.Window, "About");
        Assert.Equal("Never checked", h.Window.LblUpdateStatus.Text);
        int checks = 0, downloads = 0;
        h.Window.CheckUpdatesRequested += () => checks++;
        h.Window.DownloadsPageRequested += () => downloads++;
        var check = ByText<Button>(h.Window, "Check now");

        Click(check);
        Assert.Equal(1, checks);
        Assert.False(check.IsEnabled);
        Assert.Equal("Checking…", h.Window.LblUpdateStatus.Text);
        h.Window.ShowUpdateProgress("Downloading Pawse 9.9.9…");
        Assert.Equal("Downloading Pawse 9.9.9…", h.Window.LblUpdateStatus.Text);

        h.Window.ShowUpdateFailure("Could not reach GitHub.");
        Flush();
        Assert.Equal("Try again", check.Content);
        Assert.True(check.IsEnabled);
        Click(ByText<Button>(h.Window, "Downloads page"));
        Assert.Equal(1, downloads);

        Click(check);
        Assert.Equal(2, checks);
        Assert.Equal(Visibility.Collapsed, h.Window.BtnDownloadsPage.Visibility);
        h.Window.ShowUpdateStatus("Pawse is up to date.");
        Flush();
        Assert.Equal("Check now", check.Content);
        Assert.True(check.IsEnabled);
        Assert.Equal("Pawse is up to date.", h.Window.LblUpdateStatus.Text);
        h.Window.Close();
    });

    [Fact]
    public void The_last_check_time_is_shown_in_local_time() => OnSta(() =>
    {
        var cfg = Fixture();
        var at = new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
        cfg.Update.LastCheckUtc = at;
        var h = Open(cfg);
        Assert.Equal($"Last checked {at.ToLocalTime():yyyy-MM-dd HH:mm}", h.Window.LblUpdateStatus.Text);
        h.Window.Close();
    });
}
