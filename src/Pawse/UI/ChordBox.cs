using System.Windows;
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

    /// <summary>Raised when the committed chord changes (commit or clear); not on cancel.</summary>
    public event EventHandler? ChordChanged;

    /// <summary>Raised when a capture attempt was refused (recording blocked).</summary>
    public event EventHandler? RecordBlocked;

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
        if (_captured.Count > 0) CommitCapture();
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

        // A modifier-less Tab/Shift+Tab as the first key = navigate, don't trap the user.
        if (key == Key.Tab && _captured.Count == 0 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = false;
            CancelCapture();
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;                                  // Key.None / IME / dead keys
        string name = Keys.VkToName(Keys.Normalize(vk));
        if (Keys.NameToVk(name) == null) return;              // 0xNN fallback (numpad/OEM) - not representable

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
        if (_captured.Count == 0) return;                     // only a rejected key so far - keep waiting
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
