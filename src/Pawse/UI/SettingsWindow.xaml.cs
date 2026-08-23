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

    /// <summary>Raised after Save has written control values back into the config.</summary>
    public event Action? Applied;

    /// <summary>Raised by "Check now". App owns the check itself (and the download that may
    /// follow); this window only shows what came of it - see <see cref="ShowUpdateStatus"/>.</summary>
    public event Action? CheckUpdatesRequested;

    /// <summary>Raised by the "Downloads page" button that appears after a failed check. App
    /// owns opening it, the same as it owns the check itself.</summary>
    public event Action? DownloadsPageRequested;

    public SettingsWindow(Config cfg, Func<bool> isLocked)
    {
        InitializeComponent();
        _cfg = cfg;
        // The version reads from the title bar now, and again on the About page - the
        // footer is just Cancel/Save.
        Title = "Pawse settings - v" + App.Version;
        LblVersion.Text = App.Version == UpdateCheck.DevVersion
            ? $"Pawse {App.Version} - development build"
            : $"Pawse {App.Version}";
        // The default size does not fit a 1366x768 laptop at 125% scaling, and
        // CanMinimize leaves no way to resize out of it - so Save would sit below the
        // screen edge. Shrink instead; the per-page scrollers take up the slack.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
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

        RbUpdManual.IsChecked = _cfg.Update.ModeValue == Config.UpdateMode.Manual;
        RbUpdNotify.IsChecked = _cfg.Update.ModeValue == Config.UpdateMode.Notify;
        RbUpdAuto.IsChecked = _cfg.Update.ModeValue == Config.UpdateMode.Automatic;
        ShowUpdateCaveat();
        LblUpdateStatus.Text = _cfg.Update.LastCheckUtc is { } last
            ? $"Last checked {last.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "Never checked";

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

        _cfg.Update.ModeValue =
            RbUpdAuto.IsChecked == true ? Config.UpdateMode.Automatic :
            RbUpdNotify.IsChecked == true ? Config.UpdateMode.Notify :
            Config.UpdateMode.Manual;

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

    private void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdates.IsEnabled = false;
        BtnDownloadsPage.Visibility = Visibility.Collapsed;
        LblUpdateStatus.Text = "Checking…";
        CheckUpdatesRequested?.Invoke();
    }

    private void OnOpenDownloadsPage(object sender, RoutedEventArgs e) => DownloadsPageRequested?.Invoke();

    /// <summary>Progress while a check is still running: the text changes, the button stays
    /// disabled. <see cref="ShowUpdateStatus"/> is the terminal one and re-enables it.</summary>
    public void ShowUpdateProgress(string text) => LblUpdateStatus.Text = text;

    /// <summary>A check that reached nobody. The button becomes the retry - a first attempt
    /// fails far more often than a second - and the downloads page moves in beside it rather
    /// than interrupting with a dialog.</summary>
    public void ShowUpdateFailure(string text)
    {
        LblUpdateStatus.Text = text;
        BtnCheckUpdates.Content = "Try again";
        BtnCheckUpdates.IsEnabled = true;
        BtnDownloadsPage.Visibility = Visibility.Visible;
    }

    /// <summary>Report a finished check. Called by App on the UI thread; safe to call after
    /// the user has closed the window (App null-checks its reference, WPF ignores the rest).</summary>
    public void ShowUpdateStatus(string text)
    {
        LblUpdateStatus.Text = text;
        BtnCheckUpdates.Content = "Check now";
        BtnCheckUpdates.IsEnabled = true;
        BtnDownloadsPage.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Correct two selections that cannot mean what they say, and explain each. Both only apply
    /// to "Selected displays": in "All displays" the set is worked out fresh every time.
    /// </summary>
    private void FixUpDisplaySelection()
    {
        if (_cfg.Overlay.AllDisplays) return;
        int attached = _displayBoxes.Count;

        // Nothing ticked - "show the popup, nowhere" is not a state worth keeping.
        if (_cfg.Overlay.Displays.Count == 0)
        {
            if (_cfg.Overlay.Enabled)
            {
                _cfg.Overlay.Enabled = false;
                MessageBox.Show(this,
                    "No display is selected for the lock popup, so it was switched off.\n\n"
                        + "Pick at least one display to show it again.",
                    "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
            }
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

    /// <summary>Say up front when this copy cannot take an update on its own, so choosing
    /// "automatically" never quietly means "notify". Both reasons are properties of where
    /// Pawse is installed, so neither can change while the window is open.</summary>
    private void ShowUpdateCaveat()
    {
        string? reason = null;
        if (UpdateCheck.IsInstalled(UpdateCheck.DetectInstall())
            && UpdateCheck.DetectScope() == InstallScope.PerMachine)
            reason = "This copy is installed for everyone on this PC, so updates are offered "
                   + "rather than installed - installing needs administrator rights.";
        else if (!SelfReplace.CanWriteTo(Log.ExeDir()))
            reason = "Pawse can't write to its own folder, so it can only tell you about "
                   + "updates - installing one is up to you.";

        LblUpdateCaveat.Text = reason ?? "";
        LblUpdateCaveat.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Both About-page links. A WPF Hyperlink does nothing by itself; this hands the
    /// URL to the default browser. https only - the shell would run whatever NavigateUri said,
    /// and these two are the only things that should ever reach it.</summary>
    private void OnOpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            if (e.Uri.Scheme == Uri.UriSchemeHttps)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("open link", ex); }
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
