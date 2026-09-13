using System.Windows;
using Pawse.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Pawse.UI;

/// <summary>
/// Records a keyboard chord by reading Pawse's own global hook instead of WPF key events.
///
/// <para>Why not WPF: <see cref="ChordBox"/> records through <c>PreviewKeyDown</c>, which
/// needs the Settings window to be foreground, to hold keyboard focus, and to have a working
/// text-input path. Every one of those can fail - an elevated process whose activation is
/// refused, a key reported as <c>Key.ImeProcessed</c> - and when they do, capture fails
/// silently and the box just sits there. The hook has none of those dependencies: it is
/// installed at startup, lives on its own thread, and sees every key on the desktop no matter
/// what has focus.</para>
///
/// <para>Same "peak-held set" rule as <see cref="ChordBox"/>: every key is recorded on its
/// key-DOWN and the chord is committed once they are ALL released, so release order does not
/// matter and the user sees exactly what was taken before the window closes.</para>
/// </summary>
public partial class ShortcutRecorderWindow : Window
{
    private readonly LockController _controller;
    private readonly List<string> _captured = new();
    private readonly HashSet<int> _down = new();
    private bool _finished;

    /// <summary>The recorded chord, or null when cancelled. Only meaningful once
    /// <see cref="Window.ShowDialog"/> has returned true.</summary>
    public List<string>? Chord { get; private set; }

    public ShortcutRecorderWindow(LockController controller)
    {
        InitializeComponent();
        _controller = controller;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Arm only once the window is really on screen, so the keys that opened it (a click,
        // or Space/Enter on the button) cannot land in the recording.
        _controller.CaptureSink = OnHookKey;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Belt and braces - Finish() clears it too, but a sink left armed would swallow the
        // keyboard for the rest of the session.
        _controller.CaptureSink = null;
        base.OnClosed(e);
    }

    /// <summary>Called on the HOOK thread for every key while this window is open.</summary>
    private void OnHookKey(int vk, bool isDown) =>
        Dispatcher.BeginInvoke(new Action(() => HandleKey(vk, isDown)));

    private void HandleKey(int vk, bool isDown)
    {
        if (_finished) return;

        int nv = Keys.Normalize(vk);
        if (isDown)
        {
            _down.Add(nv);
            string name = Keys.VkToName(nv);
            if (Keys.NameToVk(name) == null)
            {
                // The 0xNN fallback: arrows, Home/End, the keypad, OEM punctuation. The config
                // cannot round-trip those, and silently dropping one used to leave a chord of
                // nothing but its modifiers. Say so and abandon the recording.
                Reject(name);
                return;
            }
            if (!_captured.Contains(name)) _captured.Add(name);
            LblChord.Text = Keys.ChordToText(_captured);
            return;
        }

        _down.Remove(nv);
        // Commit when every key is back up - the behaviour the user asked for: "closes when he
        // releases all keys again and shows what is actually used".
        if (_down.Count == 0 && _captured.Count > 0) Finish(commit: true);
    }

    /// <summary>Esc arrives through the hook like everything else, so this is only the
    /// belt-and-braces path for a key that somehow reached WPF instead.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        e.Handled = true;
        if (e.Key == System.Windows.Input.Key.Escape) Finish(commit: false);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(commit: false);

    private void Reject(string name)
    {
        _finished = true;
        _controller.CaptureSink = null;
        LblChord.Text = name;
        LblHint.Text = $"{name} can't be part of a shortcut - use letters, digits, F-keys, "
                     + "Space, Tab, Enter, Esc or Backspace with the modifiers. "
                     + "The previous shortcut was kept.";
        LblTitle.Text = "That key can't be used";
        BtnCancel.Content = "Close";
    }

    private void Finish(bool commit)
    {
        if (_finished) return;
        _finished = true;
        // Before DialogResult: setting that closes the window, and the sink must never outlive
        // it. Esc on a chord that captured nothing still counts as a cancel.
        _controller.CaptureSink = null;
        if (commit && _captured.Count > 0) Chord = new List<string>(_captured);
        DialogResult = commit && Chord is not null;
    }
}
