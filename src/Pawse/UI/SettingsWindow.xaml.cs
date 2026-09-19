using System.Globalization;
using System.Windows;
using Pawse.Core;

namespace Pawse.UI;

/// <summary>
/// Editing form bound to the live <see cref="Config"/>. Values are only written
/// back on Save (Cancel leaves the config untouched), after which <see cref="Applied"/>
/// tells App to persist + apply the change.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Config _cfg;

    /// <summary>One checkbox per attached display, its Tag holding the zero-based index.</summary>
    private readonly List<System.Windows.Controls.CheckBox> _displayBoxes = new();

    /// <summary>The display set as it was on load. The checkbox list can only offer displays
    /// attached right now, so saving just what is ticked would silently drop every configured
    /// display that happens to be unplugged - an undocked laptop would forget its monitors.
    /// OnSave unions the ticked set with the detached part of this.</summary>
    private List<int> _configuredDisplays = new();

    /// <summary>The Run key as it stood on load. Start-at-sign-in is the one setting that lives
    /// in the registry rather than in pawse.json - the Run value IS the setting - and it is
    /// written back only when the user changed the checkbox: SetEnabled(true) points the value at
    /// THIS exe, so a portable copy saving an unrelated setting must not re-point the installed
    /// copy's autostart at itself.</summary>
    private bool _autostartAtLoad;

    /// <summary>What Save decided about start-at-sign-in: null when the checkbox was left as it
    /// was, otherwise the new state for App to write to the Run key.</summary>
    public bool? AutostartChange { get; private set; }

    /// <summary>Raised after Save has written control values back into the config.</summary>
    public event Action? Applied;

    /// <summary>Raised by the locked banner's Unlock button. App owns the lock state; this
    /// window only asks. See <see cref="SetLocked"/> for how the answer comes back.</summary>
    public event Action? UnlockRequested;

    /// <summary>Live lock state; also drives <see cref="ChordBox.IsRecordingBlocked"/>.</summary>
    private readonly Func<bool> _isLocked;

    /// <summary>Opens the shortcut recorder over this window and returns what it captured, or
    /// null when cancelled. App owns it because the recorder needs the LockController (it
    /// reads the global hook), and this window is deliberately kept to Config + callbacks.</summary>
    private readonly Func<Window, List<string>?> _recordChord;

    // The About page's update controls live in SettingsWindow.Updates.cs; the Store build
    // compiles SettingsWindow.Store.cs instead (see Pawse.csproj), which has no updater and
    // only says where updates come from. InitUpdateSection is implemented by whichever of the
    // two is in the build; a partial method with no body compiles away with its calls.
    partial void InitUpdateSection();
    partial void LoadUpdates();
    partial void SaveUpdates();

    public SettingsWindow(Config cfg, Func<bool> isLocked, Func<Window, List<string>?> recordChord)
    {
        InitializeComponent();
        _cfg = cfg;
        _isLocked = isLocked;
        _recordChord = recordChord;
        // The version reads from the title bar now, and again on the About page - the
        // footer is just Cancel/Save.
        Title = "Pawse settings - v" + App.Version;
        LblVersion.Text = App.Version == App.DevVersion
            ? $"Pawse {App.Version} - development build"
            : $"Pawse {App.Version}";
        // The default size does not fit a 1366x768 laptop at 125% scaling, and
        // CanMinimize leaves no way to resize out of it - so Save would sit below the
        // screen edge. Shrink instead; the per-page scrollers take up the slack.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        SldOpacity.Minimum = Config.OverlayCfg.MinOpacity;
        ApplyDeploymentMode();
        InitUpdateSection();
        LoadMonitors();
        LoadFromConfig();

        // Recording is refused while Pawse is locked (the global hook swallows keys
        // before the capture box sees them, and stray keys would feed the unlock matcher).
        TxtChord.IsRecordingBlocked = isLocked;
        TxtLockHotkey.IsRecordingBlocked = isLocked;
        TxtChord.ChordChanged += (_, _) => UpdateWarnings();
        TxtLockHotkey.ChordChanged += (_, _) => UpdateWarnings();
        TxtChord.RecordBlocked += (_, _) => ShowBlocked(LblChordWarn);
        TxtLockHotkey.RecordBlocked += (_, _) => ShowBlocked(LblLockHotkeyWarn);
        TxtChord.KeyRejected += (_, key) => ShowRejected(LblChordWarn, key);
        TxtLockHotkey.KeyRejected += (_, key) => ShowRejected(LblLockHotkeyWarn, key);
        TxtPassphrase.TextChanged += (_, _) => UpdateWarnings();
        ChkPassphrase.Checked += (_, _) => UpdateWarnings();
        ChkPassphrase.Unchecked += (_, _) => UpdateWarnings();
        UpdateWarnings();

        // Digits only, typed or pasted, so the field cannot hold something ParseInt will
        // silently throw away on save.
        foreach (var box in new[] { TxtHoldMs, TxtTimerSeconds })
        {
            box.PreviewTextInput += OnDigitsOnly;
            System.Windows.DataObject.AddPastingHandler(box, OnPasteDigitsOnly);
            box.TextChanged += (_, _) => UpdateNumberWarnings();
        }
        UpdateNumberWarnings();

        SetLocked(isLocked());
    }

    /// <summary>A portable copy writes nothing outside its folder (<see cref="DeploymentMode.Portable"/>),
    /// so the three options that would write to the registry stay visible but greyed out, with
    /// the reason beside them. Their saved values are kept, not cleared: the same pawse.json can
    /// end up next to an installed copy, which honours them.</summary>
    private void ApplyDeploymentMode()
    {
        if (!Deployment.IsPortable) return;
        ChkAutostart.IsEnabled = false;
        ChkWinLock.IsEnabled = false;
        ChkLaunchMedia.IsEnabled = false;
        LblAutostartPortable.Visibility = Visibility.Visible;
        LblSystemKeysPortable.Visibility = Visibility.Visible;
        LblPortable.Visibility = Visibility.Visible;
    }

    /// <summary>One checkbox per display attached right now, plus the two-entry mode list.
    /// Displays that are configured but currently unplugged cannot be shown - see
    /// <see cref="_configuredDisplays"/> for how they survive a save anyway.</summary>
    private void LoadMonitors()
    {
        CmbDisplayMode.Items.Clear();
        CmbDisplayMode.Items.Add("All displays");
        CmbDisplayMode.Items.Add("Selected displays");

        PnlDisplays.Children.Clear();
        _displayBoxes.Clear();
        var screens = System.Windows.Forms.Screen.AllScreens;
        int count = Math.Max(1, screens.Length);
        for (int i = 0; i < count; i++)
        {
            string label = $"Display {i + 1}";
            if (i < screens.Length)
            {
                var b = screens[i].Bounds;
                label += $" - {b.Width}×{b.Height}{(screens[i].Primary ? " (primary)" : "")}";
            }
            var box = new System.Windows.Controls.CheckBox { Content = label, Tag = i };
            _displayBoxes.Add(box);
            PnlDisplays.Children.Add(box);
        }
    }

    private void OnDisplayModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => PnlDisplays.IsEnabled = CmbDisplayMode.SelectedIndex == 1;

    /// <summary>The displays ticked right now.</summary>
    private List<int> TickedDisplays() =>
        _displayBoxes.Where(b => b.IsChecked == true).Select(b => (int)b.Tag!).ToList();

    private void LoadFromConfig()
    {
        ChkStartLocked.IsChecked = _cfg.General.StartLocked;
        ChkAutostart.IsChecked = _autostartAtLoad = Autostart.IsEnabled();
        ChkBlockMouse.IsChecked = _cfg.General.BlockMouse;
        ChkBlockScreenKeyboard.IsChecked = _cfg.General.BlockScreenKeyboard;
        ChkTrayDoubleClick.IsChecked = _cfg.General.TrayDoubleClickUnlock;
        ChkLogging.IsChecked = _cfg.General.Logging;

        ChkWinLock.IsChecked = _cfg.SystemBlock.WinLock;
        ChkLaunchMedia.IsChecked = _cfg.SystemBlock.LaunchMediaKeys;

        ChkLockHotkey.IsChecked = _cfg.LockHotkey.Enabled;
        TxtLockHotkey.Chord = _cfg.LockHotkey.Keys;

        ChkChord.IsChecked = _cfg.Unlock.Chord.Enabled;
        TxtChord.Chord = _cfg.Unlock.Chord.Keys;

        ChkPassphrase.IsChecked = _cfg.Unlock.Passphrase.Enabled;
        TxtPassphrase.Text = _cfg.Unlock.Passphrase.Text;
        ChkResetWrong.IsChecked = _cfg.Unlock.Passphrase.ResetOnWrongKey;

        ChkMouseHold.IsChecked = _cfg.Unlock.MouseHold.Enabled;
        TxtHoldMs.Text = _cfg.Unlock.MouseHold.HoldMs.ToString(CultureInfo.InvariantCulture);

        ChkTimer.IsChecked = _cfg.Unlock.Timer.Enabled;
        TxtTimerSeconds.Text = _cfg.Unlock.Timer.Seconds.ToString(CultureInfo.InvariantCulture);

        LoadUpdates();

        ChkOverlay.IsChecked = _cfg.Overlay.Enabled;
        _configuredDisplays = new List<int>(_cfg.Overlay.Displays);
        CmbDisplayMode.SelectedIndex = _cfg.Overlay.AllDisplays ? 0 : 1;
        PnlDisplays.IsEnabled = !_cfg.Overlay.AllDisplays;
        foreach (var box in _displayBoxes)
            box.IsChecked = _configuredDisplays.Contains((int)box.Tag!);
        SldOpacity.Value = Math.Clamp(_cfg.Overlay.Opacity, Config.OverlayCfg.MinOpacity, 1.0);
        SldVertical.Value = Math.Clamp(_cfg.Overlay.VerticalPercent, 0, 100);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _cfg.General.StartLocked = ChkStartLocked.IsChecked == true;
        bool autostart = ChkAutostart.IsChecked == true;
        AutostartChange = autostart == _autostartAtLoad ? null : autostart;
        _cfg.General.BlockMouse = ChkBlockMouse.IsChecked == true;
        _cfg.General.BlockScreenKeyboard = ChkBlockScreenKeyboard.IsChecked == true;
        _cfg.General.TrayDoubleClickUnlock = ChkTrayDoubleClick.IsChecked == true;
        _cfg.General.Logging = ChkLogging.IsChecked == true;

        _cfg.SystemBlock.WinLock = ChkWinLock.IsChecked == true;
        _cfg.SystemBlock.LaunchMediaKeys = ChkLaunchMedia.IsChecked == true;

        _cfg.LockHotkey.Enabled = ChkLockHotkey.IsChecked == true;
        _cfg.LockHotkey.Keys = new List<string>(TxtLockHotkey.Chord);

        _cfg.Unlock.Chord.Enabled = ChkChord.IsChecked == true;
        _cfg.Unlock.Chord.Keys = new List<string>(TxtChord.Chord);

        _cfg.Unlock.Passphrase.Enabled = ChkPassphrase.IsChecked == true;
        _cfg.Unlock.Passphrase.Text = (TxtPassphrase.Text ?? "").Trim();
        _cfg.Unlock.Passphrase.ResetOnWrongKey = ChkResetWrong.IsChecked == true;

        _cfg.Unlock.MouseHold.Enabled = ChkMouseHold.IsChecked == true;
        _cfg.Unlock.MouseHold.HoldMs = ParseInt(TxtHoldMs.Text, _cfg.Unlock.MouseHold.HoldMs,
            Config.MouseHoldCfg.MinHoldMs, Config.MouseHoldCfg.MaxHoldMs);

        _cfg.Unlock.Timer.Enabled = ChkTimer.IsChecked == true;
        _cfg.Unlock.Timer.Seconds = ParseInt(TxtTimerSeconds.Text, _cfg.Unlock.Timer.Seconds, 1, Config.TimerCfg.MaxSeconds);

        SaveUpdates();

        _cfg.Overlay.Enabled = ChkOverlay.IsChecked == true;
        _cfg.Overlay.AllDisplays = CmbDisplayMode.SelectedIndex == 0;
        // Ticked now, plus whatever was configured for displays that are not attached: the list
        // can only offer what is plugged in, and dropping the rest would make an unrelated save
        // from an undocked laptop permanently forget its monitors.
        int attached = _displayBoxes.Count;
        _cfg.Overlay.Displays = TickedDisplays()
            .Concat(_configuredDisplays.Where(i => i >= attached))
            .Distinct().OrderBy(i => i).ToList();
        _cfg.Overlay.Opacity = SldOpacity.Value;
        _cfg.Overlay.VerticalPercent = (int)Math.Round(SldVertical.Value);

        // Guard against locking yourself out: require at least one *genuinely usable* unlock
        // method for the whole config - a parseable chord, a fully-typeable passphrase,
        // mouse-hold only when the overlay is shown AND the mouse isn't blocked, or a timer
        // with a positive delay (see Config.HasUsableUnlock).
        // The popup's own two guard rails, before the unlock check below - turning the popup
        // off here can change whether mouse-hold still counts as a usable unlock method.
        FixUpDisplaySelection();

        if (_cfg.EnsureUsableUnlockFallback(out bool reseeded))
        {
            MessageBox.Show(this,
                "At least one working unlock method is required, so the keyboard chord was enabled"
                    + (reseeded ? " and set to Ctrl+L." : "."),
                "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        Applied?.Invoke();
        Close();
    }

    /// <summary>
    /// Correct two selections that cannot mean what they say, and explain each. Both only apply
    /// to "Selected displays": in "All displays" the set is worked out fresh every time.
    /// </summary>
    private void FixUpDisplaySelection()
    {
        // Nothing to correct about where a popup goes when there is no popup. Without this,
        // switching the popup off while every display happened to be ticked answered with a
        // message box about display selection, which is not what the user just asked about.
        // Re-enabling it runs the corrections then, which is when they mean something.
        if (!_cfg.Overlay.Enabled || _cfg.Overlay.AllDisplays) return;
        int attached = _displayBoxes.Count;

        // Nothing ticked - "show the popup, nowhere" is not a state worth keeping.
        if (_cfg.Overlay.Displays.Count == 0)
        {
            _cfg.Overlay.Enabled = false;
            MessageBox.Show(this,
                "No display is selected for the lock popup, so it was switched off.\n\n"
                    + "Pick at least one display to show it again.",
                "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Every attached display ticked means "all of them" - store it as that, so a monitor
        // plugged in later is covered instead of being silently left out.
        bool everyOne = attached > 0 && Enumerable.Range(0, attached).All(_cfg.Overlay.Displays.Contains);
        if (everyOne)
        {
            _cfg.Overlay.AllDisplays = true;
            MessageBox.Show(this,
                "Every display was selected, so the lock popup is set to \"All displays\".\n\n"
                    + "It will follow any monitor you plug in or unplug from now on.",
                "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Both About-page links. A WPF Hyperlink does nothing by itself; this hands the
    /// URL to the default browser. https only - the shell would run whatever NavigateUri said,
    /// and these two are the only things that should ever reach it.</summary>
    private void OnOpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme == Uri.UriSchemeHttps) ShellOpen.Open(e.Uri.AbsoluteUri, "link");
        e.Handled = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnClearChord(object sender, RoutedEventArgs e)
    {
        TxtChord.Chord = new List<string>();
        UpdateWarnings();
    }

    private void OnClearLockHotkey(object sender, RoutedEventArgs e)
    {
        TxtLockHotkey.Chord = new List<string>();
        UpdateWarnings();
    }

    private void OnSetLockHotkey(object sender, RoutedEventArgs e)
        => RecordInto(TxtLockHotkey, LblLockHotkeyWarn);

    private void OnSetChord(object sender, RoutedEventArgs e)
        => RecordInto(TxtChord, LblChordWarn);

    /// <summary>Run the recorder and take what it captured. The recorder reads Pawse's global
    /// hook rather than WPF key events, so it works regardless of what this window's keyboard
    /// focus or text-input path is doing - which is the whole reason it exists.</summary>
    private void RecordInto(ChordBox box, System.Windows.Controls.TextBlock warn)
    {
        // Same refusal as clicking the box: while locked the hook swallows everything for the
        // lock, and stray keys would feed the live unlock matchers.
        if (_isLocked())
        {
            ShowBlocked(warn);
            return;
        }
        var chord = _recordChord(this);
        if (chord is null || chord.Count == 0) return;   // cancelled, or a rejected key
        box.Chord = chord;
        UpdateWarnings();
    }

    private static void OnDigitsOnly(object sender, System.Windows.Input.TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private static void OnPasteDigitsOnly(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) as string;
        if (string.IsNullOrEmpty(text) || !text.All(char.IsAsciiDigit)) e.CancelCommand();
    }

    /// <summary>Say the range while the user is typing. ParseInt still clamps on save - this
    /// only stops a silently corrected value being the first the user hears of it.</summary>
    private void UpdateNumberWarnings()
    {
        SetRangeWarn(LblHoldWarn, TxtHoldMs.Text,
                     Config.MouseHoldCfg.MinHoldMs, Config.MouseHoldCfg.MaxHoldMs, "milliseconds");
        // The floor of 1 matches OnSave's ParseInt call, which hard-codes it rather than
        // taking a Config constant the way the ceiling does.
        SetRangeWarn(LblTimerWarn, TxtTimerSeconds.Text, 1, Config.TimerCfg.MaxSeconds, "seconds");
    }

    private static void SetRangeWarn(System.Windows.Controls.TextBlock label, string? text,
                                     int min, int max, string unit)
    {
        bool ok = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                  && v >= min && v <= max;
        if (ok)
        {
            label.Visibility = Visibility.Collapsed;
            return;
        }
        label.Text = $"Enter a whole number from {min} to {max} {unit} - "
                   + "anything else is corrected when you save.";
        label.Visibility = Visibility.Visible;
    }

    private void UpdateWarnings()
    {
        SetWarn(LblChordWarn, TxtChord.IsModifiersOnly);
        SetWarn(LblLockHotkeyWarn, TxtLockHotkey.IsModifiersOnly);

        // Only a-z, 0-9 and space can register through the hook while locked, so any
        // other character makes the passphrase impossible to type. Warn while editing -
        // silently saving a passphrase that can never fire teaches the user it works.
        string phrase = (TxtPassphrase.Text ?? "").Trim();
        bool on = ChkPassphrase.IsChecked == true;
        if (on && phrase.Length == 0)
        {
            LblPassphraseWarn.Text = "The passphrase is empty - this unlock method won't do anything.";
            LblPassphraseWarn.Visibility = Visibility.Visible;
        }
        else if (on && !Keys.IsTypeablePassphrase(phrase))
        {
            LblPassphraseWarn.Text = "Only letters, digits and spaces can be typed while locked - " +
                                     "this passphrase could never unlock. Remove the other characters.";
            LblPassphraseWarn.Visibility = Visibility.Visible;
        }
        else
        {
            LblPassphraseWarn.Visibility = Visibility.Collapsed;
        }
    }

    private static void SetWarn(System.Windows.Controls.TextBlock label, bool modifiersOnly)
    {
        if (modifiersOnly)
        {
            label.Text = "This is modifiers-only - it triggers the instant you hold those keys. Add a normal key.";
            label.Visibility = Visibility.Visible;
        }
        else
        {
            label.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Follow the lock. Called at construction and again from App on every
    /// <c>LockedChanged</c>, so a window left open across a lock or unlock stays honest.
    /// <para>While locked the global hook swallows every key before WPF sees it, so the three
    /// plain text fields are disabled rather than left looking editable and doing nothing -
    /// the two <see cref="ChordBox"/>es already refuse capture on their own
    /// (<see cref="ChordBox.IsRecordingBlocked"/>) and keep saying why.</para></summary>
    public void SetLocked(bool locked)
    {
        PnlLockedBanner.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        TxtPassphrase.IsEnabled = !locked;
        TxtHoldMs.IsEnabled = !locked;
        TxtTimerSeconds.IsEnabled = !locked;
        // An unlock clears a stale "Unlock Pawse first to record a shortcut." that a click on
        // a chord box left behind; UpdateWarnings re-derives both labels from the real values.
        if (!locked) UpdateWarnings();
    }

    private void OnBannerUnlock(object sender, RoutedEventArgs e) => UnlockRequested?.Invoke();

    private static void ShowBlocked(System.Windows.Controls.TextBlock label)
    {
        label.Text = "Unlock Pawse first to record a shortcut.";
        label.Visibility = Visibility.Visible;
    }

    private static void ShowRejected(System.Windows.Controls.TextBlock label, string key)
    {
        label.Text = $"{key} can't be part of a shortcut - use letters, digits, F-keys, Space, Tab, " +
                     "Enter, Esc or Backspace with the modifiers. The previous shortcut was kept.";
        label.Visibility = Visibility.Visible;
    }

    private static int ParseInt(string? text, int fallback, int min, int max)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return Math.Clamp(v, min, max);
        return fallback;
    }
}
