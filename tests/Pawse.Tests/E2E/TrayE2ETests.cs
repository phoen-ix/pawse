using System.Reflection;
using System.Runtime.InteropServices;
using Pawse.Core;
using Pawse.UI;
using Xunit;
using static Pawse.Tests.E2E.Ui;

namespace Pawse.Tests.E2E;

/// <summary>The tray paw: its menu, and the click rules that keep a stray paw on the tray from
/// undoing the lock. Clicks are delivered the way the shell delivers them - as the icon's
/// callback message, sent to the window NotifyIcon listens on.</summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class TrayE2ETests
{
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;

    /// <summary>A tray wired the way App wires it: a toggle flips the lock and the icon follows.</summary>
    private sealed class Harness : IDisposable
    {
        public TrayIcon Tray { get; } = new();
        public bool Locked { get; private set; }
        public int Toggles { get; private set; }

        public Harness()
        {
            Tray.ToggleRequested += () =>
            {
                Toggles++;
                Locked = !Locked;
                Tray.SetLocked(Locked);
            };
        }

        public NotifyIcon Icon => (NotifyIcon)typeof(TrayIcon)
            .GetField("_ni", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Tray)!;

        public ContextMenuStrip Menu => Icon.ContextMenuStrip!;

        public ToolStripItem Item(string text) => Menu.Items.Cast<ToolStripItem>().Single(i => i.Text == text);

        /// <summary>The shell's callback message (NotifyIcon.WM_TRAYMOUSEMESSAGE) for one mouse event.</summary>
        public void Shell(int mouseMessage)
        {
            var window = (NativeWindow)typeof(NotifyIcon)
                .GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Icon)!;
            SendMessage(window.Handle, 0x0800, IntPtr.Zero, (IntPtr)mouseMessage);
        }

        public void Click()
        {
            Shell(WM_LBUTTONDOWN);
            Shell(WM_LBUTTONUP);
        }

        public void DoubleClick()
        {
            Click();
            Shell(WM_LBUTTONDBLCLK);
            Shell(WM_LBUTTONUP);
        }

        public void Dispose() => Tray.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void The_menu_offers_lock_settings_config_and_quit() => OnSta(() =>
    {
        using var h = new Harness();
        var items = h.Menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();
        var expected = new List<string?> { "Lock now", "Settings…", "Open config file" };
        // Only offered when this process is not elevated already (CI runners are).
        if (!Elevation.IsElevated()) expected.Add("Restart as administrator");
        expected.Add("Quit");
        Assert.Equal(expected, items);
        Assert.Equal("Pawse - unlocked", h.Icon.Text);
    });

    [Fact]
    public void Each_menu_item_asks_the_app_once_the_menu_has_closed() => OnSta(() =>
    {
        using var h = new Harness();
        var raised = new List<string>();
        h.Tray.SettingsRequested += () => raised.Add("settings");
        h.Tray.OpenConfigRequested += () => raised.Add("config");
        h.Tray.RestartAsAdminRequested += () => raised.Add("admin");
        h.Tray.QuitRequested += () => raised.Add("quit");

        foreach (var text in new[] { "Settings…", "Open config file", "Quit" })
        {
            h.Item(text).PerformClick();
            Assert.Empty(raised);                               // posted, not raised inside the click
            Flush();
        }
        Assert.Equal(new[] { "settings", "config", "quit" }, raised);

        if (!Elevation.IsElevated())
        {
            h.Item("Restart as administrator").PerformClick();
            Flush();
            Assert.Equal("admin", raised[^1]);
        }
    });

    [Fact]
    public void Lock_now_locks_and_the_menu_and_tooltip_follow() => OnSta(() =>
    {
        using var h = new Harness();
        h.Item("Lock now").PerformClick();
        Flush();
        Assert.True(h.Locked);
        Assert.Equal("Unlock", h.Menu.Items[0].Text);
        Assert.Equal("Pawse - locked", h.Icon.Text);

        h.Item("Unlock").PerformClick();
        Flush();
        Assert.False(h.Locked);
        Assert.Equal("Lock now", h.Menu.Items[0].Text);
        Assert.Equal("Pawse - unlocked", h.Icon.Text);
    });

    [Fact]
    public void One_click_locks() => OnSta(() =>
    {
        using var h = new Harness();
        h.Click();
        Assert.True(h.Locked);
        Assert.Equal(1, h.Toggles);
    });

    [Fact]
    public void While_locked_one_click_only_says_how_to_unlock() => OnSta(() =>
    {
        using var h = new Harness();
        h.Click();
        h.Click();
        Assert.True(h.Locked);
        Assert.Equal(1, h.Toggles);
        // After the double-click window has passed: a balloon, not an unlock.
        Assert.True(PumpUntil(() => h.Icon.BalloonTipText == "Locked - double-click the paw to unlock."),
                    "no hint balloon after a single click on the locked paw");
        Assert.True(h.Locked);
    });

    [Fact]
    public void While_locked_a_double_click_unlocks() => OnSta(() =>
    {
        using var h = new Harness();
        h.Click();
        h.DoubleClick();
        Assert.False(h.Locked);
        Assert.Equal(2, h.Toggles);
        PumpFor(SystemInformation.DoubleClickTime + 300);
        Assert.Equal("", h.Icon.BalloonTipText);            // the pending hint was called off
    });

    [Fact]
    public void A_fast_double_click_while_unlocked_only_locks() => OnSta(() =>
    {
        using var h = new Harness();
        h.DoubleClick();
        Assert.True(h.Locked);
        Assert.Equal(1, h.Toggles);
    });

    [Fact]
    public void With_double_click_off_one_click_unlocks_too() => OnSta(() =>
    {
        using var h = new Harness();
        h.Tray.DoubleClickUnlock = false;
        h.Click();
        h.Click();
        Assert.False(h.Locked);
        Assert.Equal(2, h.Toggles);
    });

    [Fact]
    public void A_right_click_opens_the_menu() => OnSta(() =>
    {
        using var h = new Harness();
        h.Shell(WM_RBUTTONUP);
        Assert.True(PumpUntil(() => h.Menu.Visible), "the menu did not open on a right-click");
        h.Menu.Close();
        Assert.Equal(0, h.Toggles);
    });
}
