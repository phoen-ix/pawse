using System.Windows.Input;
// This project enables both WPF and WinForms, so these names are ambiguous with
// their System.Windows.Forms twins (cf. the aliases in GlobalUsings.cs). Pin the
// WPF meanings for this file.
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;

namespace Pawse.UI;

/// <summary>
/// A read-only text box that records a keyboard chord by <em>capture</em>: click it,
/// press your combination, and it stores the keys as the config's canonical name list
/// (e.g. <c>["Ctrl","Shift","U"]</c>). All VK↔name work reuses <see cref="Keys"/>.
///
/// <para>Capture is a "peak-held set": every key is recorded on its key-DOWN and the
/// chord is committed on the first key-UP, so release order is irrelevant and a missed
/// key-up (e.g. the Win key stealing focus to the Start menu) cannot drop keys.</para>
///
/// <para><see cref="IsRecordingBlocked"/> lets the host forbid capture - while Pawse is
/// locked the global hook swallows keys before WPF ever sees them (and stray keys would
/// feed the live unlock matchers), so recording is refused with a hint instead.</para>
/// </summary>
public sealed class ChordBox : TextBox
{
    private const string PlaceholderEmpty = "(not set)";
    private const string PlaceholderCapturing = "Press keys…";

    private List<string> _chord = new();          // committed, canonical names
    private readonly List<string> _captured = new(); // built during the current capture
    private List<string> _snapshot = new();        // value at capture start (for Esc/empty)
    private bool _capturing;
    private string? _rejected;                     // a key this capture could not represent

    /// <summary>Raised when the committed chord changes (commit or clear); not on cancel.</summary>
    public event EventHandler? ChordChanged;

    /// <summary>Raised when a capture attempt was refused (recording blocked).</summary>
    public event EventHandler? RecordBlocked;

    /// <summary>Raised when a pressed key cannot be part of a chord (arrows, Home/End, the
    /// keypad, OEM keys) - the capture is then abandoned rather than committed without it,
    /// which used to leave a modifiers-only chord that fired the instant they were held.</summary>
    public event EventHandler<string>? KeyRejected;

    /// <summary>When it returns true, capture is refused (e.g. Pawse is locked). Checked live.</summary>
    public Func<bool>? IsRecordingBlocked { get; set; }

    public ChordBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;                 // no copy/paste menu on a capture field
        Cursor = Cursors.Arrow;             // reads as a button, not an editable field
        Text = PlaceholderEmpty;
    }

    /// <summary>The committed chord as canonical key names. Getter returns a copy so the
    /// control never shares a mutable list with the config.</summary>
    public IReadOnlyList<string> Chord
    {
        get => new List<string>(_chord);
        set => SetChord(value);
    }

    /// <summary>True when the chord is non-empty and every key is a modifier - such a chord
    /// fires the instant those keys are held, which is almost never what the user wants.</summary>
    public bool IsModifiersOnly =>
        _chord.Count > 0 && _chord.All(static n => n is "Ctrl" or "Shift" or "Alt" or "Win");

    private void SetChord(IEnumerable<string> value)
    {
        // Canonicalize aliases ("Control"->"Ctrl", "Super"->"Win") and drop unknowns so the
        // stored list and display always round-trip through the runtime matcher.
        _chord = Keys.ParseChordText(Keys.ChordToText(value));
        ShowChord();
    }

    private void ShowChord() =>
        Text = _chord.Count == 0 ? PlaceholderEmpty : Keys.ChordToText(_chord);

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Take focus (which starts capture) without dropping a caret into the read-only box.
        e.Handled = true;
        Focus();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        BeginCapture();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!_capturing) return;
        if (_captured.Count > 0 && _rejected is null) CommitCapture();
        else CancelCapture();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing) return;            // not capturing: leave normal behaviour (Tab nav, etc.)
        e.Handled = true;                   // capturing: swallow so keys don't navigate/Save/open Alt-menu
        if (e.IsRepeat) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt/F10 arrive as Key.System

        if (key == Key.Escape)
        {
            CancelCapture();
            MoveFocusAway();
            return;
        }

        // Tab / Shift+Tab as the first real key = navigate, don't trap the user. Shift arrives
        // first and is captured as a key in its own right, so "nothing captured yet" has to read
        // "nothing but Shift" - or Shift+Tab out of the box committed the chord Shift+Tab.
        if (key == Key.Tab && _captured.All(n => n == "Shift")
            && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            e.Handled = false;
            CancelCapture();
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;                                  // Key.None / IME / dead keys
        string name = Keys.VkToName(Keys.Normalize(vk));
        if (Keys.NameToVk(name) == null)
        {
            // 0xNN fallback (arrows, Home/End, numpad, OEM keys) - not representable in the
            // config. Say so and poison this capture: dropping the key silently left the chord
            // as just its modifiers.
            _rejected = key.ToString();
            Text = $"({_rejected} can't be part of a shortcut)";
            KeyRejected?.Invoke(this, _rejected);
            return;
        }

        if (!_captured.Contains(name))
        {
            _captured.Add(name);
            Text = Keys.ChordToText(_captured);               // live preview as the combo builds
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        if (_rejected is not null)
        {
            CancelCapture();                                  // keep the old chord, not a maimed one
            MoveFocusAway();
            return;
        }
        if (_captured.Count == 0) return;                     // nothing captured yet - keep waiting
        CommitCapture();
        MoveFocusAway();
    }

    private void BeginCapture()
    {
        if (_capturing) return;
        if (IsRecordingBlocked?.Invoke() == true)
        {
            RecordBlocked?.Invoke(this, EventArgs.Empty);
            // Defer the focus move out of the GotKeyboardFocus handler to avoid re-entrancy.
            Dispatcher.BeginInvoke(new Action(() =>
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))));
            return;
        }
        _snapshot = new List<string>(_chord);
        _captured.Clear();
        _rejected = null;
        _capturing = true;
        Text = PlaceholderCapturing;
    }

    private void CommitCapture()
    {
        _capturing = false;
        _chord = new List<string>(_captured);
        ShowChord();
        ChordChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelCapture()
    {
        _capturing = false;
        _chord = _snapshot;
        ShowChord();
    }

    private void MoveFocusAway()
    {
        // Clear _capturing first so the resulting LostKeyboardFocus no-ops (no double-commit).
        _capturing = false;
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}
