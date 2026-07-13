namespace Pawse.Core;

/// <summary>
/// Fires once when every key in <see cref="_target"/> is held simultaneously.
/// Edge-triggered on the key-down that completes the set, then re-arms only once
/// the set is broken - so holding the chord doesn't fire repeatedly.
/// </summary>
public sealed class ChordMatcher
{
    private readonly HashSet<int> _target;
    private bool _satisfied;

    public ChordMatcher(HashSet<int> target) => _target = target;

    public bool IsUsable => _target.Count > 0;

    /// <param name="pressed">Currently-held normalized VKs.</param>
    /// <param name="justVk">The normalized VK of the event being processed.</param>
    /// <param name="isDown">True for key-down.</param>
    public bool Feed(HashSet<int> pressed, int justVk, bool isDown)
    {
        bool now = _target.Count > 0 && _target.IsSubsetOf(pressed);
        bool fire = now && !_satisfied && isDown && _target.Contains(justVk);
        _satisfied = now;
        return fire;
    }

    public void Reset() => _satisfied = false;
}

/// <summary>
/// Fires when the passphrase is typed. A wrong key restarts progress (falling
/// back to a fresh match if the wrong key happens to be the first letter). This
/// is pure state - the characters come from the keyboard hook, not a text box
/// (the hook swallows all keys while locked, so no control ever gets focus).
/// </summary>
public sealed class PassphraseMatcher
{
    private readonly string _text;
    private readonly bool _resetOnWrong;
    private int _i;

    public PassphraseMatcher(string text, bool resetOnWrong)
    {
        _text = (text ?? "").ToLowerInvariant();
        _resetOnWrong = resetOnWrong;
    }

    public bool IsUsable => _text.Length > 0;

    public int Progress => _i;
    public int Length => _text.Length;

    public bool Feed(char c)
    {
        if (_text.Length == 0) return false;
        c = char.ToLowerInvariant(c);

        if (c == _text[_i])
        {
            _i++;
        }
        else
        {
            // Wrong key: restart (and account for the wrong key itself being a
            // fresh first letter). Without reset-on-wrong we still restart, which
            // is the intuitive "type it cleanly" behavior.
            _ = _resetOnWrong;
            _i = (c == _text[0]) ? 1 : 0;
        }

        if (_i >= _text.Length)
        {
            _i = 0;
            return true;
        }
        return false;
    }

    public void Reset() => _i = 0;
}
