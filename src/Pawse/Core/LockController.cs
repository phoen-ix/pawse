namespace Pawse.Core;

/// <summary>
/// The lock state machine. Hook callbacks arrive on the dedicated hook thread
/// (see <see cref="HookThread"/>); Engage/Disengage/RebuildMatchers are also
/// called from the UI thread (tray, auto-unlock timer, hold button, settings),
/// so all mutable state is guarded by <see cref="_gate"/>. Everything inside the
/// lock is set arithmetic - microseconds - so the hook path never waits long.
///
/// <para><see cref="LockedChanged"/> is raised synchronously on whichever thread
/// flipped the state; App marshals the actual UI work onto the dispatcher.</para>
/// </summary>
public sealed class LockController
{
    private readonly object _gate = new();
    private readonly HashSet<int> _pressed = new();

    /// <summary>Raw (side-specific) VKs whose key-DOWN we passed to the foreground
    /// while unlocked and haven't seen released. If we lock mid-combo (e.g. the Ctrl
    /// of a Ctrl+L lock hotkey), these keys are still "held" by the app, so we let
    /// their key-UPs through while locked to avoid a stuck modifier.</summary>
    private readonly HashSet<int> _leakedDown = new();

    /// <summary>Normalized VKs that were physically held when the lock engaged (e.g.
    /// the lock hotkey's own keys). The OS autorepeats the newest held key, and those
    /// repeat DOWNs would re-enter <see cref="_pressed"/> and could complete an unlock
    /// chord that overlaps the hotkey. A key leaves this set only on its real key-UP
    /// (or when pruning sees it released), so held-across-the-lock keys never count
    /// toward the unlock chord until deliberately pressed again.</summary>
    private readonly HashSet<int> _staleSinceEngage = new();

    private ChordMatcher? _unlockChord;
    private ChordMatcher? _lockHotkey;
    private PassphraseMatcher? _passphrase;

    private volatile bool _isLocked;
    private volatile bool _suppressLockHotkey;

    public Config Config { get; }
    public bool IsLocked => _isLocked;

    /// <summary>When true, the lock hotkey is ignored - set while the Settings window is
    /// open so recording a new shortcut can't lock the machine mid-capture.</summary>
    public bool SuppressLockHotkey
    {
        get => _suppressLockHotkey;
        set => _suppressLockHotkey = value;
    }

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
        lock (_gate)
        {
            // The unlock chord requires the pressed set to EQUAL the chord: a cat
            // sprawled over a dozen keys that happen to include Ctrl and L must not
            // unlock. The lock hotkey stays subset-matched - locking is allowed to
            // be easy (Ctrl+L should lock even mid-Ctrl-combo).
            _unlockChord = Config.Unlock.Chord.Enabled
                ? new ChordMatcher(Keys.ParseChord(Config.Unlock.Chord.Keys), requireExact: true)
                : null;
            _lockHotkey = Config.LockHotkey.Enabled
                ? new ChordMatcher(Keys.ParseChord(Config.LockHotkey.Keys))
                : null;
            _passphrase = Config.Unlock.Passphrase.Enabled
                ? new PassphraseMatcher(Config.Unlock.Passphrase.Text, Config.Unlock.Passphrase.ResetOnWrongKey)
                : null;
        }
    }

    /// <summary>
    /// Process one physical key event. Returns <c>true</c> to swallow it.
    /// While locked EVERYTHING is swallowed; while unlocked keys pass through and
    /// we only watch for the lock hotkey.
    /// </summary>
    public bool OnKeyboard(int vk, bool isDown, bool ours)
    {
        // Our own injected modifier-clear: pass it through - but ONLY that. The tag
        // is a public constant any process could stamp on SendInput, so it buys
        // exactly what Pawse injects (modifier key-UPs) and nothing else; tagged
        // key-downs or non-modifier keys are treated like any other input.
        if (ours && !isDown && Input.IsClearedModifier(vk)) return false;

        lock (_gate)
        {
            int nv = Keys.Normalize(vk);
            bool staleRepeat = isDown && _staleSinceEngage.Contains(nv);
            if (isDown)
            {
                if (!staleRepeat) _pressed.Add(nv);
            }
            else
            {
                _pressed.Remove(nv);
                _staleSinceEngage.Remove(nv);
            }

            // Key-ups delivered on another desktop (Win+L's Winlogon, a UAC prompt)
            // never reach a LL hook, leaving phantom entries in _pressed - and a
            // phantom plus subset/exact matching would let a single real keypress
            // complete a chord. Reconcile against the OS's async key state before
            // matching; the sets are tiny, so this is a handful of user32 calls.
            PruneReleased(exceptNv: nv);

            if (_isLocked)
            {
                if (!staleRepeat)
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
                }
                // Block every key-down (no typing / shortcuts). But if this key's DOWN
                // leaked to the foreground before we locked (e.g. the Ctrl of a Ctrl+L
                // lock hotkey), let its key-UP through so the system sees the release -
                // otherwise that modifier stays stuck (its real up would be swallowed here).
                if (!isDown && _leakedDown.Remove(vk))
                    return false;
                return true;
            }

            if (!_suppressLockHotkey && _lockHotkey != null && _lockHotkey.Feed(_pressed, nv, isDown))
            {
                Engage("hotkey");
                return true; // swallow the completing key so it doesn't leak to the app
            }

            // Unlocked pass-through: remember which physical keys the app has seen go
            // down, so we can release them if we lock mid-combo (see _leakedDown).
            if (isDown) _leakedDown.Add(vk); else _leakedDown.Remove(vk);
            return false;
        }
    }

    public void Toggle()
    {
        if (_isLocked) Disengage("toggle");
        else Engage("toggle");
    }

    public void Engage(string source)
    {
        lock (_gate)
        {
            if (_isLocked) return;
            _isLocked = true;
            Log.Info($"LOCK engaged (source={source})");
            Input.ClearModifiers();        // fast; kills stuck-modifier / zoom-on-scroll bug
            _staleSinceEngage.Clear();
            _staleSinceEngage.UnionWith(_pressed); // held-at-engage keys must be re-pressed to count
            _pressed.Clear();
            _unlockChord?.Reset();
            _passphrase?.Reset();
        }
        RaiseLocked(true);
    }

    public void Disengage(string source)
    {
        lock (_gate)
        {
            if (!_isLocked) return;
            _isLocked = false;
            Log.Info($"UNLOCK ({source})");
            Input.ClearModifiers();
            _pressed.Clear();
            _leakedDown.Clear();
            _staleSinceEngage.Clear();
        }
        RaiseLocked(false);
    }

    private void PruneReleased(int exceptNv)
    {
        // The current event's own key is exempt: for the key being processed the
        // async state may not be updated yet (LL hooks run ahead of it).
        _pressed.RemoveWhere(nv => nv != exceptNv && !PhysicallyDown(nv));
        _staleSinceEngage.RemoveWhere(nv => nv != exceptNv && !PhysicallyDown(nv));
    }

    private static bool PhysicallyDown(int nv)
    {
        if (nv == Keys.VK_LWIN) // both Win keys normalize onto VK_LWIN
            return (NativeMethods.GetAsyncKeyState(Keys.VK_LWIN) & 0x8000) != 0
                || (NativeMethods.GetAsyncKeyState(Keys.VK_RWIN) & 0x8000) != 0;
        // Generic VK_SHIFT/VK_CONTROL/VK_MENU are valid GetAsyncKeyState queries
        // (they report "either side down"), matching how Normalize folds them.
        return (NativeMethods.GetAsyncKeyState(nv) & 0x8000) != 0;
    }

    private void RaiseLocked(bool locked)
    {
        try { LockedChanged?.Invoke(locked); }
        catch (Exception ex) { Log.Error("LockedChanged handler", ex); }
    }
}
