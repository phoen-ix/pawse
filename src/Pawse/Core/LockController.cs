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
///
/// <para>Which keys are held is decided differently in the two states. While UNLOCKED every
/// key passes through, so the OS agrees with us and <see cref="PruneReleased"/> can heal our
/// set from <c>GetAsyncKeyState</c>. While LOCKED that state is useless - it is updated
/// downstream of WH_KEYBOARD_LL, so a key-down we swallow never registers there - and this
/// class's own down/up record is the only truth. The one thing that can make that record
/// drift is a desktop switch eating key-ups, which is what <see cref="ForgetHeldKeys"/> is
/// for.</para>
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

    /// <summary>Normalized VKs that were physically held when the lock engaged (e.g. the lock
    /// hotkey's own keys). The OS autorepeats a held key, and to the hook those repeats are
    /// ordinary key-downs: feeding them to the matchers would let a key the user never pressed
    /// again type the passphrase or complete the unlock chord by itself. Repeats of these keys
    /// are therefore ignored until the key's real key-UP (or <see cref="ForgetHeldKeys"/>)
    /// drops it from the set. It stays in <see cref="_pressed"/> throughout - it really is
    /// held, and the unlock chord is entitled to count it once the chord is re-formed.</summary>
    private readonly HashSet<int> _staleSinceEngage = new();

    /// <summary>"Is this normalized VK physically down?" - the OS by default, swappable in
    /// tests. Only meaningful for keys the system actually processed (see OnKeyboard).</summary>
    private readonly Func<int, bool> _isPhysicallyDown;

    /// <summary>Injects modifier key-UPs; <see cref="Input.ClearModifiers"/> by default.</summary>
    private readonly Action _clearModifiers;

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

    public LockController(Config config) : this(config, null, null) { }

    /// <summary>Test seam: the two calls into the OS this class makes are injectable so the
    /// state machine can be driven without a keyboard (see Pawse.Tests).</summary>
    internal LockController(Config config, Func<int, bool>? isPhysicallyDown, Action? clearModifiers)
    {
        Config = config;
        _isPhysicallyDown = isPhysicallyDown ?? PhysicallyDown;
        _clearModifiers = clearModifiers ?? Input.ClearModifiers;
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
            // Re-bound mid-lock (Settings → Apply): a brand-new matcher is armed, so if the
            // new chord happens to be held right now, the next autorepeat would unlock.
            if (_isLocked) _unlockChord?.Prime(_pressed);
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
                _pressed.Add(nv);
            }
            else
            {
                _pressed.Remove(nv);
                _staleSinceEngage.Remove(nv);
            }

            // Key-ups delivered on another desktop (Win+L's Winlogon, a UAC prompt) never
            // reach a LL hook, leaving phantom entries in _pressed - and a phantom plus
            // subset matching would let a single real keypress complete the lock hotkey.
            // Reconcile against the OS's async key state; the sets are tiny, so this is a
            // handful of user32 calls.
            //
            // ONLY while unlocked. That state is updated downstream of WH_KEYBOARD_LL ("the
            // callback function is called before the asynchronous state of the key is
            // updated"), so it only ever knows about events the system actually processed -
            // and while locked we swallow every key-down, so the OS never records it.
            // Pruning there would evict the very keys being held for the unlock chord: the
            // Ctrl of a Ctrl+L would be gone by the time L arrives, and no multi-key chord
            // could ever complete. While locked, our own down/up record is the only truth;
            // phantoms are healed by ForgetHeldKeys when the input desktop comes back.
            if (!_isLocked) PruneReleased(exceptNv: nv);

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
            _clearModifiers();             // fast; kills stuck-modifier / zoom-on-scroll bug
            _staleSinceEngage.Clear();
            _staleSinceEngage.UnionWith(_pressed); // their autorepeat must not feed the matchers
            // _pressed is NOT cleared: it is what the user is physically holding, and while
            // locked nothing else can tell us (see the pruning note in OnKeyboard). Priming
            // the chord instead of resetting it is what keeps a held Ctrl+L from unlocking
            // the moment it autorepeats - an already-satisfied chord must be broken first.
            _unlockChord?.Prime(_pressed);
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
            _clearModifiers();
            // Clearing is truthful here: ClearModifiers has just made the OS-level modifier
            // state "up", so the foreground app holds nothing either. Real keys still held
            // announce themselves again on their next event.
            _pressed.Clear();
            _leakedDown.Clear();
            _staleSinceEngage.Clear();
        }
        RaiseLocked(false);
    }

    /// <summary>
    /// Drop everything we believe is held. Called when key-ups were provably missed - input
    /// went to a desktop we don't own (Win+L's Winlogon, a UAC prompt, Ctrl+Alt+Del), where
    /// no LL hook of ours runs. Without this a key released over there stays "held" forever
    /// and, under exact matching, blocks the unlock chord for good.
    /// <para><see cref="_leakedDown"/> is deliberately kept: its entries only ever let a
    /// key-UP through to the foreground, and dropping them risks a stuck modifier.</para>
    /// </summary>
    public void ForgetHeldKeys()
    {
        lock (_gate)
        {
            if (_pressed.Count == 0 && _staleSinceEngage.Count == 0) return;
            _pressed.Clear();
            _staleSinceEngage.Clear();
            _unlockChord?.Reset();
            _lockHotkey?.Reset();
        }
    }

    private void PruneReleased(int exceptNv)
    {
        // The current event's own key is exempt: for the key being processed the
        // async state may not be updated yet (LL hooks run ahead of it).
        _pressed.RemoveWhere(nv => nv != exceptNv && !_isPhysicallyDown(nv));
        _staleSinceEngage.RemoveWhere(nv => nv != exceptNv && !_isPhysicallyDown(nv));
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
