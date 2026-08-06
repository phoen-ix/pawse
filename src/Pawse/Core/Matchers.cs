namespace Pawse.Core;

/// <summary>
/// Fires once when the held keys satisfy <see cref="_target"/>. Edge-triggered on
/// the key-down that completes it, then re-arms only once the condition is broken -
/// so holding the chord doesn't fire repeatedly.
///
/// <para>Two matching modes: subset (the chord may be part of a larger held set -
/// right for the lock hotkey, where locking should be easy) and exact (the held
/// set must equal the chord and nothing else - right for the unlock chord, where
/// a cat sprawled over half the keyboard holding Ctrl and L among ten other keys
/// must NOT unlock).</para>
/// </summary>
public sealed class ChordMatcher
{
    private readonly HashSet<int> _target;
    private readonly bool _requireExact;
    private bool _satisfied;

    public ChordMatcher(HashSet<int> target, bool requireExact = false)
    {
        _target = target;
        _requireExact = requireExact;
    }

    /// <param name="pressed">Currently-held normalized VKs.</param>
    /// <param name="justVk">The normalized VK of the event being processed.</param>
    /// <param name="isDown">True for key-down.</param>
    public bool Feed(HashSet<int> pressed, int justVk, bool isDown)
    {
        bool now = Matches(pressed);
        bool fire = now && !_satisfied && isDown && _target.Contains(justVk);
        _satisfied = now;
        return fire;
    }

    /// <summary>Latch the matcher against what is held right now, without firing. Used when
    /// the lock engages on this very chord: it is already satisfied, so it must be broken
    /// before it can fire again - otherwise the OS autorepeat of the held key unlocks
    /// instantly. Re-arming happens on its own, on the first event that breaks the chord.</summary>
    public void Prime(HashSet<int> pressed) => _satisfied = Matches(pressed);

    public void Reset() => _satisfied = false;

    private bool Matches(HashSet<int> pressed) =>
        _target.Count > 0 && (_requireExact ? _target.SetEquals(pressed) : _target.IsSubsetOf(pressed));
}

/// <summary>
/// Fires when the passphrase is typed. This is pure state - the characters come
/// from the keyboard hook, not a text box (the hook swallows all keys while
/// locked, so no control ever gets focus).
///
/// <para>With <c>resetOnWrong</c>, a wrong key falls back to the longest prefix of
/// the passphrase that is still a suffix of what was typed (KMP failure links) -
/// so "aaab" completes "aab", which a naive restart-from-scratch would miss.
/// Without it, wrong keys are simply ignored and progress is kept.</para>
/// </summary>
public sealed class PassphraseMatcher
{
    private readonly string _text;
    private readonly bool _resetOnWrong;
    private readonly int[] _fail;
    private int _i;

    public PassphraseMatcher(string text, bool resetOnWrong)
    {
        _text = (text ?? "").ToLowerInvariant();
        _resetOnWrong = resetOnWrong;

        // Standard KMP failure function: _fail[i] = length of the longest proper
        // prefix of _text[..i] that is also its suffix.
        _fail = new int[_text.Length];
        int k = 0;
        for (int i = 1; i < _text.Length; i++)
        {
            while (k > 0 && _text[i] != _text[k]) k = _fail[k - 1];
            if (_text[i] == _text[k]) k++;
            _fail[i] = k;
        }
    }

    public bool Feed(char c)
    {
        if (_text.Length == 0) return false;
        c = char.ToLowerInvariant(c);

        if (_resetOnWrong)
        {
            while (_i > 0 && c != _text[_i]) _i = _fail[_i - 1];
            if (c == _text[_i]) _i++;
        }
        else if (c == _text[_i])
        {
            _i++;
        }
        // else: keep progress and ignore the wrong key (the user opted out of reset,
        // so fat-fingering a key mid-phrase doesn't make them start over).

        if (_i >= _text.Length)
        {
            _i = 0;
            return true;
        }
        return false;
    }

    public void Reset() => _i = 0;
}
