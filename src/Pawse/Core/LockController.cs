namespace Pawse.Core;

/// <summary>
/// The lock state machine. Everything runs on the UI thread: the keyboard hook
/// callback is delivered there, tray clicks are there, and the auto-unlock timer
/// (owned by App) fires there - so <see cref="_pressed"/> and the matchers need
/// no locking.
///
/// <para><see cref="LockedChanged"/> is raised synchronously; App defers the
/// actual UI work (overlay show/hide, tray icon) onto the dispatcher queue so the
/// hook callback returns immediately (a slow callback gets the hook killed).</para>
/// </summary>
public sealed class LockController
{
    private readonly HashSet<int> _pressed = new();

    /// <summary>Raw (side-specific) VKs whose key-DOWN we passed to the foreground
    /// while unlocked and haven't seen released. If we lock mid-combo (e.g. the Ctrl
    /// of a Ctrl+L lock hotkey), these keys are still "held" by the app, so we let
    /// their key-UPs through while locked to avoid a stuck modifier.</summary>
    private readonly HashSet<int> _leakedDown = new();
    private ChordMatcher? _unlockChord;
    private ChordMatcher? _lockHotkey;
    private PassphraseMatcher? _passphrase;

    public Config Config { get; }
    public bool IsLocked { get; private set; }

    /// <summary>When true, the lock hotkey is ignored - set while the Settings window is
    /// open so recording a new shortcut can't lock the machine mid-capture.</summary>
    public bool SuppressLockHotkey { get; set; }

    /// <summary>Raised with the new locked state. Handlers should be quick or defer.</summary>
    public event Action<bool>? LockedChanged;

    public LockController(Config config)
    {
        Config = config;
        RebuildMatchers();
    }

    /// <summary>Rebuild matchers after a config change.</summary>
    public void RebuildMatchers()
    {
        _unlockChord = Config.Unlock.Chord.Enabled
            ? new ChordMatcher(Keys.ParseChord(Config.Unlock.Chord.Keys))
            : null;
        _lockHotkey = Config.LockHotkey.Enabled
            ? new ChordMatcher(Keys.ParseChord(Config.LockHotkey.Keys))
            : null;
        _passphrase = Config.Unlock.Passphrase.Enabled
            ? new PassphraseMatcher(Config.Unlock.Passphrase.Text, Config.Unlock.Passphrase.ResetOnWrongKey)
            : null;
    }

    /// <summary>
    /// Process one physical key event. Returns <c>true</c> to swallow it.
    /// While locked EVERYTHING is swallowed; while unlocked keys pass through and
    /// we only watch for the lock hotkey.
    /// </summary>
    public bool OnKeyboard(int vk, bool isDown, bool ours)
    {
        if (ours) return false; // our own injected modifier-clear - let it through

        int nv = Keys.Normalize(vk);
        if (isDown) _pressed.Add(nv); else _pressed.Remove(nv);

        if (IsLocked)
        {
            if (_unlockChord != null && _unlockChord.Feed(_pressed, nv, isDown))
            {
                Disengage("chord");
                return true;
            }
            if (isDown && _passphrase != null)
            {
                char? c = Keys.TryVkToChar(vk);
                if (c.HasValue && _passphrase.Feed(c.Value))
                {
                    Disengage("passphrase");
                    return true;
                }
            }
            // Block every key-down (no typing / shortcuts). But if this key's DOWN
            // leaked to the foreground before we locked (e.g. the Ctrl of a Ctrl+L
            // lock hotkey), let its key-UP through so the system sees the release -
            // otherwise that modifier stays stuck (its real up would be swallowed here).
            if (!isDown && _leakedDown.Remove(vk))
                return false;
            return true;
        }

        if (!SuppressLockHotkey && _lockHotkey != null && _lockHotkey.Feed(_pressed, nv, isDown))
        {
            Engage("hotkey");
            return true; // swallow the completing key so it doesn't leak to the app
        }

        // Unlocked pass-through: remember which physical keys the app has seen go
        // down, so we can release them if we lock mid-combo (see _leakedDown).
        if (isDown) _leakedDown.Add(vk); else _leakedDown.Remove(vk);
        return false;
    }

    public void Toggle()
    {
        if (IsLocked) Disengage("toggle");
        else Engage("toggle");
    }

    public void Engage(string source)
    {
        if (IsLocked) return;
        IsLocked = true;
        Log.Info($"LOCK engaged (source={source})");
        Input.ClearModifiers();        // fast; kills stuck-modifier / zoom-on-scroll bug
        _pressed.Clear();
        _unlockChord?.Reset();
        _passphrase?.Reset();
        RaiseLocked(true);
    }

    public void Disengage(string source)
    {
        if (!IsLocked) return;
        IsLocked = false;
        Log.Info($"UNLOCK ({source})");
        Input.ClearModifiers();
        _pressed.Clear();
        _leakedDown.Clear();
        RaiseLocked(false);
    }

    private void RaiseLocked(bool locked)
    {
        try { LockedChanged?.Invoke(locked); }
        catch (Exception ex) { Log.Error("LockedChanged handler", ex); }
    }
}
