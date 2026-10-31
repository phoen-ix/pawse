using System.Windows;
using Pawse.Core;
using Pawse.UI;
using Xunit;
using static Pawse.Tests.E2E.Ui;

namespace Pawse.Tests.E2E;

/// <summary>The lock popup, both looks: the hint names exactly the unlock methods that work,
/// and holding the button or the paw unlocks - only when held long enough, and only when the
/// mouse is not blocked.</summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class OverlayE2ETests
{
    private static Config Cfg(Action<Config>? tweak = null)
    {
        var c = new Config();
        c.Unlock.MouseHold.HoldMs = Config.MouseHoldCfg.MinHoldMs;   // 100 ms: quick holds
        tweak?.Invoke(c);
        return c;
    }

    private static OverlayWindow Popup(Config cfg)
    {
        var w = new OverlayWindow();
        w.Configure(cfg);
        Show(w);
        return w;
    }

    private static void Dispose(OverlayWindow w)
    {
        w.AllowClose = true;
        w.Close();
    }

    public static TheoryData<string, string> Hints() => new()
    {
        { "defaults", "To unlock: press Ctrl+L, or hold the button below." },
        { "lockphrase shown", "To unlock: press Ctrl+L, or type “purr”, or hold the button below." },
        { "lockphrase hidden", "To unlock: press Ctrl+L, or type your lockphrase, or hold the button below." },
        { "untypeable lockphrase", "To unlock: press Ctrl+L, or hold the button below." },
        { "mouse blocked", "To unlock: press Ctrl+L." },
        { "timer only", "Unlocks automatically." },
        { "nothing", "To unlock: use the tray icon." },
    };

    private static Config HintCfg(string variant) => Cfg(c =>
    {
        switch (variant)
        {
            case "lockphrase shown":
                c.Unlock.Lockphrase.Enabled = true;
                c.Unlock.Lockphrase.Text = "purr";
                break;
            case "lockphrase hidden":
                c.Unlock.Lockphrase.Enabled = true;
                c.Unlock.Lockphrase.Text = "purr";
                c.Overlay.ShowLockphrase = false;
                break;
            case "untypeable lockphrase":
                c.Unlock.Lockphrase.Enabled = true;
                c.Unlock.Lockphrase.Text = "purr!";
                break;
            case "mouse blocked":
                c.General.BlockMouse = true;
                break;
            case "timer only":
                c.Unlock.Chord.Enabled = false;
                c.Unlock.MouseHold.Enabled = false;
                c.Unlock.Timer.Enabled = true;
                c.Unlock.Timer.Seconds = 90;
                break;
            case "nothing":
                c.Unlock.Chord.Enabled = false;
                c.Unlock.MouseHold.Enabled = false;
                break;
        }
    });

    [Theory]
    [MemberData(nameof(Hints))]
    public void The_card_names_only_the_ways_out_that_work(string variant, string hint) => OnSta(() =>
    {
        var cfg = HintCfg(variant);
        var w = Popup(cfg);
        Assert.Equal(Visibility.Visible, w.CardRoot.Visibility);
        Assert.Equal(Visibility.Collapsed, w.PawRoot.Visibility);
        Assert.Equal(hint, w.HintText.Text);
        bool holdable = cfg.Unlock.MouseHold.Enabled && !cfg.General.BlockMouse;
        Assert.Equal(holdable ? Visibility.Visible : Visibility.Collapsed, w.HoldArea.Visibility);
        Dispose(w);
    });

    [Fact]
    public void The_card_counts_down_when_the_timer_is_on() => OnSta(() =>
    {
        var w = Popup(Cfg(c => { c.Unlock.Timer.Enabled = true; c.Unlock.Timer.Seconds = 90; }));
        Assert.Equal(Visibility.Visible, w.AutoUnlockText.Visibility);
        Assert.Equal("Auto-unlocks after 90s", w.AutoUnlockText.Text);
        w.Configure(Cfg());
        Assert.Equal(Visibility.Collapsed, w.AutoUnlockText.Visibility);
        Dispose(w);
    });

    [Fact]
    public void The_paw_look_has_its_size_hint_and_timer() => OnSta(() =>
    {
        var cfg = Cfg(c =>
        {
            c.Overlay.StyleValue = Config.OverlayStyle.Paw;
            c.Overlay.Paw.Size = 160;
            c.Unlock.Timer.Enabled = true;
            c.Unlock.Timer.Seconds = 30;
        });
        var w = Popup(cfg);
        Assert.Equal(Visibility.Collapsed, w.CardRoot.Visibility);
        Assert.Equal(Visibility.Visible, w.PawRoot.Visibility);
        Assert.Equal(160, w.PawButton.Width);
        Assert.Equal(Visibility.Visible, w.PawHintBox.Visibility);
        Assert.Equal("To unlock: press Ctrl+L, or hold the paw.", w.PawHintText.Text);
        Assert.Equal("Auto-unlocks after 30s", w.PawAutoUnlockText.Text);

        cfg.Overlay.Paw.ShowHint = false;
        w.Configure(cfg);
        Assert.Equal(Visibility.Collapsed, w.PawHintBox.Visibility);
        Dispose(w);
    });

    [Fact]
    public void Opacity_never_drops_below_the_floor() => OnSta(() =>
    {
        var w = Popup(Cfg(c => c.Overlay.Opacity = 0.05));
        Assert.Equal(Config.OverlayCfg.MinOpacity, w.Opacity, 3);
        Dispose(w);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Holding_the_button_or_the_paw_unlocks(bool paw) => OnSta(() =>
    {
        var w = Popup(Cfg(c => { if (paw) c.Overlay.StyleValue = Config.OverlayStyle.Paw; }));
        int unlocks = 0;
        w.UnlockByHold += () => unlocks++;
        MouseDown(paw ? w.PawButton : w.HoldButton);
        Assert.True(PumpUntil(() => unlocks > 0), "a 100 ms hold did not unlock within 5 s");
        PumpFor(300);
        Assert.Equal(1, unlocks);
        Dispose(w);
    });

    [Fact]
    public void Letting_go_early_or_leaving_the_button_does_not_unlock() => OnSta(() =>
    {
        var w = Popup(Cfg(c => c.Unlock.MouseHold.HoldMs = 2000));
        int unlocks = 0;
        w.UnlockByHold += () => unlocks++;

        MouseDown(w.HoldButton);
        Assert.True(PumpUntil(() => w.HoldFill.Width > 0), "the hold bar never started to fill");
        MouseUp(w.HoldButton);
        Assert.Equal(0, w.HoldFill.Width);

        MouseDown(w.HoldButton);
        Assert.True(PumpUntil(() => w.HoldFill.Width > 0));
        MouseLeave(w.HoldButton);
        Assert.Equal(0, w.HoldFill.Width);

        PumpFor(2500);
        Assert.Equal(0, unlocks);
        Dispose(w);
    });

    [Fact]
    public void With_the_mouse_blocked_a_hold_does_nothing() => OnSta(() =>
    {
        var w = Popup(Cfg(c => { c.General.BlockMouse = true; c.Overlay.StyleValue = Config.OverlayStyle.Paw; }));
        int unlocks = 0;
        w.UnlockByHold += () => unlocks++;
        MouseDown(w.PawButton);
        PumpFor(500);
        Assert.Equal(0, unlocks);
        Assert.Equal(System.Windows.Input.Cursors.Arrow, w.PawButton.Cursor);
        Dispose(w);
    });

    [Fact]
    public void Alt_F4_only_hides_the_popup_until_the_app_lets_it_close() => OnSta(() =>
    {
        var w = Popup(Cfg());
        w.HideLocked();
        w.ShowLocked();
        Assert.True(w.IsVisible);
        Assert.True(w.Topmost);

        w.Close();                                            // what Alt+F4 does
        Flush();
        Assert.False(w.IsVisible);
        w.ShowLocked();                                       // still usable on the next lock
        Assert.True(w.IsVisible);

        bool closed = false;
        w.Closed += (_, _) => closed = true;
        Dispose(w);
        Assert.True(closed);
    });
}
