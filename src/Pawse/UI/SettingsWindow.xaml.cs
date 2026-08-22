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

    /// <summary>What the monitor combo was set to on load (the stored index clamped to
    /// the displays attached right now) - OnSave persists only a departure from this.</summary>
    private int _loadedMonitorIndex;

    /// <summary>Raised after Save has written control values back into the config.</summary>
    public event Action? Applied;

    /// <summary>Raised by "Check now". App owns the check itself (and the download that may
    /// follow); this window only shows what came of it - see <see cref="ShowUpdateStatus"/>.</summary>
    public event Action? CheckUpdatesRequested;

    public SettingsWindow(Config cfg, Func<bool> isLocked)
    {
        InitializeComponent();
        _cfg = cfg;
        VersionLabel.Text = "v" + App.Version;
        SldOpacity.Minimum = Config.OverlayCfg.MinOpacity;
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
        TxtPassphrase.TextChanged += (_, _) => UpdateWarnings();
        ChkPassphrase.Checked += (_, _) => UpdateWarnings();
        ChkPassphrase.Unchecked += (_, _) => UpdateWarnings();
        UpdateWarnings();
    }

    private void LoadMonitors()
    {
        CmbMonitor.Items.Clear();
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            string primary = screens[i].Primary ? " (primary)" : "";
            CmbMonitor.Items.Add($"Display {i + 1} - {b.Width}×{b.Height}{primary}");
        }
        if (screens.Length == 0)
            CmbMonitor.Items.Add("Display 1");
    }

    private void LoadFromConfig()
    {
        ChkStartLocked.IsChecked = _cfg.General.StartLocked;
        ChkAutostart.IsChecked = Autostart.IsEnabled();
        ChkBlockMouse.IsChecked = _cfg.General.BlockMouse;
        ChkBlockScreenKeyboard.IsChecked = _cfg.General.BlockScreenKeyboard;
        ChkTrayDoubleClick.IsChecked = _cfg.General.TrayDoubleClickUnlock;

        ChkWinLock.IsChecked = _cfg.SystemBlock.WinLock;
        ChkLaunchMedia.IsChecked = _cfg.SystemBlock.LaunchMediaKeys;
        LblFilterStatus.Text =
            "Most of these are already blocked by the lock. This also engages the Windows Keyboard "
            + "Filter to catch any that bypass the hook - that part needs Enterprise/Education/IoT + admin.";

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

        ChkAutoUpdate.IsChecked = _cfg.Update.AutoCheck;
        LblUpdateStatus.Text = _cfg.Update.LastCheckUtc is { } last
            ? $"Last checked {last.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "Never checked";

        ChkOverlay.IsChecked = _cfg.Overlay.Enabled;
        int count = Math.Max(1, CmbMonitor.Items.Count);
        _loadedMonitorIndex = Math.Clamp(_cfg.Overlay.Monitor, 0, count - 1);
        CmbMonitor.SelectedIndex = _loadedMonitorIndex;
        SldOpacity.Value = Math.Clamp(_cfg.Overlay.Opacity, Config.OverlayCfg.MinOpacity, 1.0);
        SldVertical.Value = Math.Clamp(_cfg.Overlay.VerticalPercent, 0, 100);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _cfg.General.StartLocked = ChkStartLocked.IsChecked == true;
        _cfg.General.Autostart = ChkAutostart.IsChecked == true;
        _cfg.General.BlockMouse = ChkBlockMouse.IsChecked == true;
        _cfg.General.BlockScreenKeyboard = ChkBlockScreenKeyboard.IsChecked == true;
        _cfg.General.TrayDoubleClickUnlock = ChkTrayDoubleClick.IsChecked == true;

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
        _cfg.Unlock.MouseHold.HoldMs = ParseInt(TxtHoldMs.Text, _cfg.Unlock.MouseHold.HoldMs, 100, 10000);

        _cfg.Unlock.Timer.Enabled = ChkTimer.IsChecked == true;
        _cfg.Unlock.Timer.Seconds = ParseInt(TxtTimerSeconds.Text, _cfg.Unlock.Timer.Seconds, 1, 86400);

        _cfg.Update.AutoCheck = ChkAutoUpdate.IsChecked == true;

        _cfg.Overlay.Enabled = ChkOverlay.IsChecked == true;
        // Persist the display only when the user actually changed it. The combo can only
        // show the displays attached right now, so with the laptop undocked the stored
        // index is clamped for display - writing that clamp back on an unrelated save
        // would permanently forget which monitor the user picked.
        if (CmbMonitor.SelectedIndex != _loadedMonitorIndex)
            _cfg.Overlay.Monitor = Math.Max(0, CmbMonitor.SelectedIndex);
        _cfg.Overlay.Opacity = SldOpacity.Value;
        _cfg.Overlay.VerticalPercent = (int)Math.Round(SldVertical.Value);

        // Guard against locking yourself out: require at least one *genuinely usable* unlock
        // method for the whole config - a parseable chord, a fully-typeable passphrase,
        // mouse-hold only when the overlay is shown AND the mouse isn't blocked, or a timer
        // with a positive delay (see Config.HasUsableUnlock).
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

    private void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdates.IsEnabled = false;
        LblUpdateStatus.Text = "Checking…";
        CheckUpdatesRequested?.Invoke();
    }

    /// <summary>Report a finished check. Called by App on the UI thread; safe to call after
    /// the user has closed the window (App null-checks its reference, WPF ignores the rest).</summary>
    public void ShowUpdateStatus(string text)
    {
        LblUpdateStatus.Text = text;
        BtnCheckUpdates.IsEnabled = true;
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

    private static void ShowBlocked(System.Windows.Controls.TextBlock label)
    {
        label.Text = "Unlock Pawse first to record a shortcut.";
        label.Visibility = Visibility.Visible;
    }

    private static int ParseInt(string? text, int fallback, int min, int max)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return Math.Clamp(v, min, max);
        return fallback;
    }
}
