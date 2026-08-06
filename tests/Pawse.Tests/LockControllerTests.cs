using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

/// <summary>
/// Drives <see cref="LockController.OnKeyboard"/> the way the hook does, with a stand-in for
/// the OS key-state table that behaves like the real one: it only records events the system
/// actually processed. A key-DOWN the hook swallows never registers, because the async key
/// state is updated downstream of WH_KEYBOARD_LL - which is why reconciling against it while
/// locked used to evict the very keys held for the unlock chord.
/// </summary>
public class LockControllerTests
{
    private const int L = 0x4C, A = 0x41, S = 0x53;

    private sealed class Harness
    {
        /// <summary>What GetAsyncKeyState would report right now (normalized VKs).</summary>
        public readonly HashSet<int> OsDown = new();
        public readonly LockController Controller;

        public Harness(Config? config = null)
        {
            Controller = new LockController(config ?? new Config(), OsDown.Contains, ClearModifiers);
        }

        /// <summary>Feed one key event; returns true if Pawse swallowed it. A swallowed event
        /// never reaches the stage that updates the OS key state, so <see cref="OsDown"/>
        /// only moves for events that passed through. <paramref name="injected"/> models an
        /// on-screen keyboard (or any other SendInput source).</summary>
        public bool Send(int vk, bool isDown, bool injected = false)
        {
            bool swallowed = Controller.OnKeyboard(vk, isDown, ours: false, injected: injected);
            if (!swallowed)
            {
                if (isDown) OsDown.Add(Keys.Normalize(vk));
                else OsDown.Remove(Keys.Normalize(vk));
            }
            return swallowed;
        }

        public void Tap(int vk)
        {
            Send(vk, true);
            Send(vk, false);
        }

        /// <summary>Engage injects modifier key-UPs, so the OS stops seeing them as held even
        /// while the user's finger is still on Ctrl.</summary>
        private void ClearModifiers() =>
            OsDown.RemoveWhere(vk => vk is Keys.VK_CONTROL or Keys.VK_SHIFT or Keys.VK_MENU or Keys.VK_LWIN);
    }

    [Fact]
    public void Ctrl_L_locks_and_a_fresh_Ctrl_L_unlocks()
    {
        var h = new Harness();

        h.Send(Keys.VK_LCONTROL, true);
        h.Send(L, true);
        Assert.True(h.Controller.IsLocked);

        h.Send(L, false);
        h.Send(Keys.VK_LCONTROL, false);

        h.Send(Keys.VK_LCONTROL, true);
        h.Send(L, true);
        Assert.False(h.Controller.IsLocked);
    }

    [Fact]
    public void Holding_ctrl_across_the_lock_still_toggles_with_a_second_L()
    {
        var h = new Harness();

        h.Send(Keys.VK_LCONTROL, true);
        h.Send(L, true);
        Assert.True(h.Controller.IsLocked);

        // Ctrl stays down the whole time - only L is released and pressed again.
        h.Send(L, false);
        h.Send(L, true);
        Assert.False(h.Controller.IsLocked);
    }

    [Fact]
    public void Autorepeat_of_the_held_hotkey_does_not_unlock()
    {
        var h = new Harness();

        h.Send(Keys.VK_LCONTROL, true);
        h.Send(L, true);
        Assert.True(h.Controller.IsLocked);

        for (int i = 0; i < 5; i++) h.Send(L, true); // the OS repeating the held key
        Assert.True(h.Controller.IsLocked);
    }

    [Fact]
    public void A_sprawl_of_keys_that_includes_the_chord_does_not_unlock()
    {
        var h = new Harness();
        h.Controller.Engage("test");

        h.Send(Keys.VK_LCONTROL, true);
        h.Send(A, true);
        h.Send(L, true);
        h.Send(S, true);
        Assert.True(h.Controller.IsLocked);

        // Letting go of the extras is a key-UP: the chord only fires on the key-DOWN that
        // completes it, so this must not unlock either.
        h.Send(A, false);
        h.Send(S, false);
        Assert.True(h.Controller.IsLocked);
    }

    [Fact]
    public void A_key_up_lost_to_another_desktop_blocks_the_chord_until_the_keys_are_forgotten()
    {
        var h = new Harness();
        h.Controller.Engage("test");

        h.Send(A, true); // released on the lock screen / behind a UAC prompt - we never see it

        h.Send(Keys.VK_LCONTROL, true);
        h.Send(L, true);
        Assert.True(h.Controller.IsLocked); // exact match: the phantom A is in the way

        h.Controller.ForgetHeldKeys();

        h.Send(Keys.VK_LCONTROL, true);
        h.Send(L, true);
        Assert.False(h.Controller.IsLocked);
    }

    [Fact]
    public void While_unlocked_a_phantom_key_is_still_pruned_against_the_OS()
    {
        var h = new Harness();

        h.Send(Keys.VK_LCONTROL, true);
        h.OsDown.Remove(Keys.VK_CONTROL); // released where no hook of ours could see it

        h.Send(L, true);
        Assert.False(h.Controller.IsLocked); // a lone L must not complete the lock hotkey
    }

    [Fact]
    public void Locked_keys_are_swallowed_but_a_leaked_key_up_gets_through()
    {
        var h = new Harness();

        Assert.False(h.Send(Keys.VK_LCONTROL, true)); // unlocked: passes to the foreground
        Assert.True(h.Send(L, true));                 // completes the hotkey - swallowed

        // Ctrl went down in the app before the lock, so its release must reach the app too,
        // or the foreground is left with a stuck modifier.
        Assert.False(h.Send(Keys.VK_LCONTROL, false));
        Assert.True(h.Send(A, true));                 // everything else stays swallowed
    }

    [Fact]
    public void An_on_screen_keyboard_keeps_typing_while_the_hardware_keyboard_is_locked()
    {
        var h = new Harness();
        h.Controller.Engage("test");

        Assert.False(h.Send(A, true, injected: true));  // reaches the focused app
        Assert.False(h.Send(A, false, injected: true));
        Assert.True(h.Send(A, true));                   // the real keyboard stays locked
    }

    [Fact]
    public void Blocking_on_screen_keyboards_swallows_injected_keys_too()
    {
        var config = new Config();
        config.General.BlockScreenKeyboard = true;

        var h = new Harness(config);
        h.Controller.Engage("test");

        Assert.True(h.Send(A, true, injected: true));
        Assert.True(h.Send(A, false, injected: true));
    }

    [Fact]
    public void The_unlock_chord_can_be_tapped_on_an_on_screen_keyboard()
    {
        var h = new Harness();
        h.Controller.Engage("test");

        h.Send(Keys.VK_LCONTROL, true, injected: true);
        Assert.True(h.Send(L, true, injected: true)); // the completing key is swallowed
        Assert.False(h.Controller.IsLocked);
    }

    [Fact]
    public void Passphrase_still_unlocks_while_the_chord_is_disabled()
    {
        var config = new Config();
        config.Unlock.Chord.Enabled = false;
        config.Unlock.Passphrase.Enabled = true;
        config.Unlock.Passphrase.Text = "as";

        var h = new Harness(config);
        h.Controller.Engage("test");

        h.Tap(A);
        h.Tap(S);
        Assert.False(h.Controller.IsLocked);
    }
}
