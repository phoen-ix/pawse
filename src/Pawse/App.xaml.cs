using System.IO;
using System.Windows;
using System.Windows.Threading;
using Pawse.Core;
using Pawse.UI;

namespace Pawse;

public partial class App : Application
{
    private Mutex? _singleton;
    private IDisposable? _quitChannel;
    private LockController? _controller;
    private SystemBlock? _systemBlock;
    private HookThread? _hooks;
    private TrayIcon? _tray;
    /// <summary>One popup per selected display. Empty whenever the popup is switched off -
    /// a hidden-but-alive window is what let a disabled popup reappear on the next lock.</summary>
    private readonly List<OverlayWindow> _overlays = new();
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _autoUnlock;
    private bool _canLock;   // false if the keyboard hook failed to install (locking disabled)
    private bool _saveFailureNotified;

    /// <summary>Shown when the running instance is elevated and this copy is not: it cannot be
    /// asked to quit (setting its event, or even opening its mutex, is refused).</summary>
    private const string ElevatedInstanceText =
        "The Pawse that's running has administrator rights, so this copy can't ask it to close.\n\n"
        + "Quit it from the tray (right-click the paw, then Quit) and start this one again.";

    internal static readonly string Version =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The version a local build reports (Pawse.csproj ships 0.0.0-dev; CI injects
    /// the real one).</summary>
    internal const string DevVersion = "0.0.0";

    // The self-updater lives in App.Updates.cs, which the Store build leaves out - the Store
    // updates Pawse there. A partial method with no body compiles to nothing, the calls to it
    // included, so these hooks cost that build nothing and cannot reach an updater it lacks.
    partial void SweepUpdateLeftovers();
    partial void SyncUpdateSchedule();
    partial void RunDeferredUpdate();
    partial void WireUpdates(SettingsWindow window);
    partial void StopUpdates();

    /// <summary>Store build only (App.Store.cs): one line on how this copy is packaged.</summary>
    partial void LogPackageContext();

    /// <summary>The uninstaller's headless mode, <c>--uninstall-cleanup</c> (App.Uninstall.cs;
    /// not in the Store build, which has no uninstaller). Sets <paramref name="handled"/> when
    /// the arguments asked for it - the process then does that and nothing else.</summary>
    partial void RunUninstallCleanup(string[] args, ref bool handled);

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Before everything else, logging included: the uninstaller asking for a cleanup gets
        // exactly that - no mutex, no tray, no hooks, and no log folder that it would have to
        // delete again.
        bool handled = false;
        RunUninstallCleanup(e.Args, ref handled);
        if (handled) return;

        // OnStartup runs on the dispatcher, so without this guard a throw below would be
        // swallowed by the DispatcherUnhandledException handler and - because ShutdownMode
        // is OnExplicitShutdown - leave a headless process with no tray icon, no hook and
        // no way to quit short of Task Manager. Startup failures must exit, not linger.
        try
        {
            StartupCore(e);
        }
        catch (Exception ex)
        {
            Log.Error("fatal startup failure - exiting", ex);
            try
            {
                MessageBox.Show("Pawse could not start and will close.\n\n" + ex.Message,
                    "Pawse", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* ignore */ }
            Shutdown(1);
        }
    }

    private void StartupCore(StartupEventArgs e)
    {
        // First, so nothing logs ahead of the banner. Nothing is written until Enable() has
        // read the setting; until then every line is only buffered.
        Log.Init(Version);

        // Before ANYTHING that can block - and long before the mutex wait below, which is the
        // instance we are replacing still holding it. A portable self-replace keeps the old
        // exe until this lands: getting here at all proves the apphost found a runtime and the
        // CLR came up, which is the one thing Process.Start cannot tell the outgoing instance.
        // Stay silent on a normal start; nothing is listening then. See StartupSignal for the
        // compatibility contract - a build that stops announcing gets rolled back as failed.
        if (e.Args.Contains(Elevation.ReplaceArg)) StartupSignal.Announce();

        bool created;
        try
        {
            _singleton = new Mutex(true, @"Local\Pawse-single-instance-2b8f9c", out created);
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but belongs to a token this one cannot open it with: a Pawse
            // running as administrator, seen from an un-elevated copy. .NET asks for full
            // control and has no reduced-rights fallback, so the case surfaces HERE - never in
            // TakeOverFromRunningInstance, which is only reached once the mutex was opened.
            Log.Info("startup: another instance is running elevated - this copy cannot ask it to quit");
            MessageBox.Show(ElevatedInstanceText, "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        if (!created)
        {
            // Relaunched by an outgoing instance (--replace): it is already on its way out and
            // told us so, no need to ask anyone anything. Ten seconds, like the other two waits:
            // its OnExit can legitimately take several (two 2 s thread joins plus an inline
            // Keyboard Filter revert) before the mutex is released.
            bool acquired = e.Args.Contains(Elevation.ReplaceArg) && WaitForMutex(TimeSpan.FromSeconds(10));
            if (!acquired && !TakeOverFromRunningInstance())
            {
                Shutdown();
                return;
            }
        }

        InstallExceptionHandlers();

        // Clean up after a self-update: the replaced exe and earlier downloads.
        SweepUpdateLeftovers();

        // Let the installer/uninstaller ask us to bow out cleanly instead of force-killing
        // us - only OnExit reverts the Win+L and media-key blocks. This deliberately skips
        // QuitWithConfirm's "you're locked" prompt: the user already agreed in the
        // installer, and a second dialog stuck behind it would just look like a hang.
        _quitChannel = QuitSignal.Listen(() =>
            Dispatcher.BeginInvoke((Action)(() =>
            {
                Log.Info("quit requested over the quit channel (installer/uninstaller)");
                Shutdown();
            })));

        var config = Config.Load();
        // Everything logged so far has only been buffered - commit it or drop it now that
        // the setting is known. Config.Load's own lines are in that buffer too.
        Log.Enable(config.General.Logging);
        string? unlockRepaired = EnsureUsableUnlock(config);
        Log.Info("config: " + config.Summary());
        LogPackageContext();

        // If Block Win+L is on but this (managed) PC needs admin to apply it and we're
        // not elevated, offer to relaunch as admin before building anything we'd tear down.
        if (RelaunchElevatedIfWinLockNeedsIt(config)) return;

        _controller = new LockController(config);

        _tray = new TrayIcon { DoubleClickUnlock = config.General.TrayDoubleClickUnlock };
        _tray.ToggleRequested += () => { if (_canLock) _controller!.Toggle(); };
        _tray.SettingsRequested += OpenSettings;
        _tray.OpenConfigRequested += OpenConfigFile;
        _tray.RestartAsAdminRequested += RestartAsAdmin;
        _tray.QuitRequested += QuitWithConfirm;

        if (unlockRepaired is not null)
            _tray.Notify("Pawse", unlockRepaired);

        // OS-level Win+L guard (opt-in). Sweep first so a value left behind by a
        // crash-while-locked is reverted before any StartLocked engage re-applies it.
        // The sweep only reverts Pawse's own leftovers (WorkstationLock's ownership
        // marker) - an admin-set DisableLockWorkstation is left alone.
        _systemBlock = new SystemBlock(config, _tray.Notify);
        _systemBlock.Apply(locked: false, background: true, notify: true);
        _systemBlock.WarnIfUnsweepableLeftovers();

        // Keep the Run entry honest: a portable copy takes back an entry an older version let it
        // write; an installed one re-points an entry whose exe is gone (never resurrects one the
        // user removed).
        if (Autostart.Repair() is { } autostartNotice) _tray.Notify("Pawse", autostartNotice);

        if (config.Overlay.Enabled)
            CreateOverlays(config);

        _controller.LockedChanged += OnLockedChanged;

        // Both hooks live on a dedicated pumping thread: callbacks stay serviced
        // (and keys stay swallowed) no matter how busy this UI thread is, and the
        // thread re-registers the hooks periodically in case the OS removed them.
        _hooks = new HookThread(_controller);
        // Raised on the hook thread; Notify marshals to the UI thread itself. Only the failure
        // edge is worth a balloon - the recovery is in the log.
        _hooks.KeyboardHookAlive += alive =>
        {
            if (alive) return;
            _tray?.Notify("Pawse",
                "Pawse could not refresh its keyboard hook. If keys get through while locked, " +
                "unlock and lock again - or restart Pawse.");
        };
        _canLock = _hooks.Start();
        if (_canLock)
        {
            if (config.General.StartLocked)
                _controller.Engage("start");
        }
        else
        {
            // No keyboard hook = keys aren't swallowed AND no in-app keyboard unlock can
            // fire. Refuse to "lock" rather than enter a state the user can't undo.
            _tray.Notify("Pawse",
                "Could not install the keyboard hook, so locking is disabled - a lock you " +
                "couldn't undo would be worse. Try restarting Pawse.");
        }

        SyncUpdateSchedule();   // the daily check - a no-op unless the user turned it on

        Log.Info("startup complete");
    }

    private void InstallExceptionHandlers()
    {
        // Keep the app alive on non-fatal UI exceptions; if it does die, the OS
        // removes our hooks automatically (fail-open - the keyboard is freed).
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("dispatcher exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Error("domain exception", ex.ExceptionObject as Exception ?? new Exception($"{ex.ExceptionObject}"));
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("task exception", ex.Exception);
            ex.SetObserved();
        };
    }

    /// <summary>If the loaded config has no usable unlock method (hand-edited pawse.json, or
    /// all methods disabled/misconfigured), enable the chord so a lock can always be undone.
    /// Returns the balloon text saying what changed, or null when nothing did. The text names
    /// the chord that actually works: the fallback only falls back to Ctrl+L when the saved
    /// keys do not parse - a merely disabled Ctrl+Shift+U is re-enabled as it is, and telling
    /// the user "Ctrl+L" then would be a lockout instruction.</summary>
    private static string? EnsureUsableUnlock(Config config)
    {
        if (!config.EnsureUsableUnlockFallback(out bool reseeded)) return null;
        string chord = Keys.ChordToText(config.Unlock.Chord.Keys);
        Log.Warn($"config has no usable unlock method - enabling the chord ({chord}) to prevent lockout");
        config.Save();
        return "Your saved config had no working unlock method, so the keyboard chord "
             + (reseeded ? $"was enabled and set to {chord}" : $"({chord}) was enabled")
             + " to keep you from getting locked out.";
    }

    /// <summary>Build one popup per display the config resolves to, reusing the existing set
    /// when it already matches - rebuilding on every save would flash the popup while locked.</summary>
    private void CreateOverlays(Config config)
    {
        int screens = Math.Max(1, System.Windows.Forms.Screen.AllScreens.Length);
        var targets = Config.OverlayCfg.ResolveDisplays(config.Overlay, screens);
        if (!config.Overlay.AllDisplays && config.Overlay.Displays.Count > 0
            && targets.Count == 1 && targets[0] == 0 && !config.Overlay.Displays.Contains(0))
        {
            Log.Warn("overlay: none of the chosen displays are attached - falling back to the primary");
        }

        if (_overlays.Count == targets.Count && _overlays.Select(o => o.TargetDisplay).SequenceEqual(targets))
        {
            foreach (var existing in _overlays) existing.Configure(config);
            return;
        }

        DestroyOverlays();
        foreach (int target in targets)
        {
            var overlay = new OverlayWindow { TargetDisplay = target };
            overlay.Configure(config);
            overlay.UnlockByHold += () => _controller!.Disengage("hold");
            _overlays.Add(overlay);
        }
        Log.Info($"overlay: {_overlays.Count} popup(s) on display(s) {string.Join(", ", targets.Select(t => t + 1))}");
    }

    private void ShowOverlays()
    {
        foreach (var overlay in _overlays) overlay.ShowLocked();
    }

    private void HideOverlays()
    {
        foreach (var overlay in _overlays) overlay.HideLocked();
    }

    private void DestroyOverlays()
    {
        foreach (var overlay in _overlays)
        {
            try { overlay.AllowClose = true; overlay.Close(); }
            catch (Exception ex) { Log.Error("overlay close", ex); }
        }
        _overlays.Clear();
    }

    private void OnLockedChanged(bool locked)
    {
        // Raised on whichever thread flipped the state - usually the hook thread.
        // BeginInvoke both marshals to the UI thread and returns immediately, so
        // the hook callback never waits on UI work.
        Dispatcher.BeginInvoke((Action)(() =>
        {
            try
            {
                _tray?.SetLocked(locked);
                // An open Settings window has to follow the lock: while locked the hook
                // swallows every key before WPF sees it, so its text fields would otherwise
                // sit there looking editable and doing nothing.
                _settingsWindow?.SetLocked(locked);
                // The Win+L registry toggle runs inline inside Apply; the Keyboard-Filter
                // (WMI) work is dispatched off-thread inside Apply so this returns fast.
                _systemBlock?.Apply(locked, background: true, notify: true);
                if (locked)
                {
                    // Check the setting, don't just check that a window exists. Turning the
                    // popup off used to hide the window without destroying it, so the next
                    // lock showed it again - the setting was honoured only at startup, which
                    // made a restart look like the fix.
                    if (_controller!.Config.Overlay.Enabled)
                    {
                        // Re-resolve the display set on every lock: "All displays" promises to
                        // follow a monitor plugged in or unplugged since startup, and this is the
                        // one cheap, certain place to keep that promise. CreateOverlays reuses the
                        // windows when the set is unchanged, so a repeat lock does not flash.
                        CreateOverlays(_controller.Config);
                        ShowOverlays();
                    }
                    StartAutoUnlock();
                }
                else
                {
                    StopAutoUnlock();
                    HideOverlays();
                    // The keyboard is the user's again, so an update held back while locked
                    // can go ahead now.
                    RunDeferredUpdate();
                }
            }
            catch (Exception ex) { Log.Error("apply lock state", ex); }
        }));
    }

    /// <summary>
    /// Another Pawse holds the single-instance mutex. Offer to close it and take over, so a
    /// copy you just downloaded - or unzipped somewhere else - can actually be run instead of
    /// only being told no. Returns true when the mutex is ours and startup may continue.
    /// </summary>
    private bool TakeOverFromRunningInstance()
    {
        // Default No: the safe answer is to leave a working Pawse alone.
        var answer = MessageBox.Show(
            "Pawse is already running - the paw is in the system tray.\n\n" +
            "Close that one and start this copy instead?\n\n" +
            "If it has the keyboard locked right now, closing it hands the keyboard back.",
            "Pawse", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            Log.Info("startup: another instance is running and the user kept it");
            return false;
        }

        // The quit channel, not taskkill: only the other instance's OnExit reverts the Win+L
        // policy value and the Keyboard Filter rules, so killing it could leave Win+L disabled
        // on a machine whose Pawse is gone.
        var request = QuitSignal.Signal();
        Log.Info($"startup: asked the running instance to quit - {request}");

        switch (request)
        {
            case QuitRequest.AccessDenied:
                MessageBox.Show(ElevatedInstanceText, "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;

            case QuitRequest.NoListener:
                // Either it exited between the mutex check and now, or it predates the quit
                // channel. The first case resolves itself, so try the mutex before giving up.
                if (WaitForMutex(TimeSpan.FromSeconds(2))) return true;
                MessageBox.Show(
                    "Pawse is running but didn't answer - it may be an older build that can't be "
                        + "asked to close.\n\nQuit it from the tray (right-click the paw, then "
                        + "Quit) and start this one again.",
                    "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;

            default:
                // Same budget the installer allows itself for this (pawse.nsi polls 20x500ms).
                // A clean shutdown is well under a second; blocking here is fine because no
                // window exists yet.
                if (WaitForMutex(TimeSpan.FromSeconds(10))) return true;
                MessageBox.Show(
                    "Pawse was asked to close but is still running.\n\nQuit it from the tray "
                        + "(right-click the paw, then Quit) and start this one again.",
                    "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
        }
    }

    /// <summary>Wait for the outgoing instance to release the single-instance mutex. An
    /// abandoned mutex counts as acquired: the previous owner died without releasing it, which
    /// leaves the name free either way.</summary>
    private bool WaitForMutex(TimeSpan timeout)
    {
        try { return _singleton!.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    private void StartAutoUnlock()
    {
        StopAutoUnlock();
        var t = _controller!.Config.Unlock.Timer;
        if (!t.Enabled || t.Seconds <= 0) return;
        _autoUnlock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(t.Seconds) };
        _autoUnlock.Tick += (_, _) =>
        {
            StopAutoUnlock();
            _controller!.Disengage("timer");
        };
        _autoUnlock.Start();
        Log.Info($"auto-unlock armed: {t.Seconds}s");
    }

    private void StopAutoUnlock()
    {
        _autoUnlock?.Stop();
        _autoUnlock = null;
    }

    private void OpenSettings()
    {
        try
        {
            if (_settingsWindow != null)
            {
                // Activate() alone does not surface a minimized window.
                if (_settingsWindow.WindowState == WindowState.Minimized)
                    _settingsWindow.WindowState = WindowState.Normal;
                ForceForeground(_settingsWindow);
                return;
            }
            _settingsWindow = new SettingsWindow(
                _controller!.Config,
                () => _controller!.IsLocked,
                RecordChord);
            _settingsWindow.Applied += ApplyConfigChange;
            WireUpdates(_settingsWindow);
            // The locked banner's Unlock button. While locked the keyboard is swallowed, so
            // the mouse is the only way out of Settings that does not mean hunting the tray.
            _settingsWindow.UnlockRequested += () => _controller!.Disengage("settings");
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _controller!.SuppressLockHotkey = false;
            };
            // Suppression exists to protect chord capture; a minimized Settings window
            // captures nothing, so give the lock hotkey back while it's minimized.
            _settingsWindow.StateChanged += (_, _) =>
            {
                if (_settingsWindow != null)
                    _controller!.SuppressLockHotkey = _settingsWindow.WindowState != WindowState.Minimized;
            };
            // Suppress the lock hotkey while Settings is open so re-binding it (which
            // means pressing the current one) can't lock the machine mid-capture.
            _controller!.SuppressLockHotkey = true;
            _settingsWindow.Show();
            ForceForeground(_settingsWindow);
        }
        catch (Exception ex)
        {
            Log.Error("open settings", ex);
            // Don't leave the lock hotkey wedged off or a half-open window latched.
            _settingsWindow = null;
            if (_controller != null) _controller.SuppressLockHotkey = false;
        }
    }

    /// <summary>Show the shortcut recorder over <paramref name="owner"/> and return what it
    /// captured, or null when cancelled. App owns this because the recorder needs the
    /// LockController - it records off the global hook rather than off WPF key events.</summary>
    private List<string>? RecordChord(Window owner)
    {
        var recorder = new ShortcutRecorderWindow(_controller!) { Owner = owner };
        return recorder.ShowDialog() == true ? recorder.Chord : null;
    }

    /// <summary>Bring a window to the front and make sure keyboard focus lands inside it.
    /// <para><see cref="Window.Activate"/> is <c>SetForegroundWindow</c> underneath, which
    /// Windows grants only to the process that owns the foreground - and after a click on the
    /// tray paw that is Explorer, not Pawse. A refused call fails silently: the window appears
    /// and takes mouse input (mouse messages go to the window under the cursor either way)
    /// while every keystroke goes to whatever is still active. That is what made Settings look
    /// half-broken - the passphrase box, both duration boxes and both shortcut boxes dead,
    /// while the checkboxes, sliders and Clear buttons worked.</para>
    /// <para>So check whether activation actually took, and if it did not, ask once more
    /// directly.</para>
    /// <para>This deliberately does NOT use the AttachThreadInput trick. Attaching merges the
    /// two threads' input queues - including the key-state table that TranslateMessage reads
    /// to decide whether a key-down produces a character - and detaching does not put it back.
    /// A foreground thread that believed Ctrl was held would hand that belief to Pawse's UI
    /// thread, where it would persist until Ctrl was pressed and released over the window:
    /// letters and digits would stop producing characters while Ctrl+Backspace still deleted.
    /// That is indistinguishable from the bug this function exists to fix, and it would only
    /// ever fire on the elevated/tray-click path that already reproduces it. Worse, attaching
    /// to a hung foreground thread blocks ours. A refused activation is a visible annoyance;
    /// this would be a silent, self-inflicted repeat.</para></summary>
    private static void ForceForeground(Window window)
    {
        window.Activate();
        if (!window.IsActive)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero && !NativeMethods.SetForegroundWindow(hwnd))
                    Log.Warn("settings window: SetForegroundWindow was refused");
                window.Activate();
            }
            catch (Exception ex) { Log.Error("settings window activate", ex); }
        }

        // Foreground is only half of it: a window with nothing focused inside has nowhere to
        // put a keystroke. Set it once, on a window that has none - re-opening Settings from
        // the tray must not yank focus back from wherever the user left it. The first tab stop
        // is the locked banner's Unlock button when that banner is up, otherwise the
        // TabControl's first header - never a ChordBox, which would start a capture nobody
        // asked for (ChordBox begins one on ANY keyboard-focus gain).
        if (System.Windows.Input.FocusManager.GetFocusedElement(window) is null)
        {
            window.Focus();
            window.MoveFocus(new System.Windows.Input.TraversalRequest(
                System.Windows.Input.FocusNavigationDirection.First));
        }

        // One line, so a repeat of "I can't type in Settings" is answerable from pawse.log.
        Log.Info($"settings window foreground: active={window.IsActive}");
    }

    private void ApplyConfigChange()
    {
        var config = _controller!.Config;
        if (!config.Save() && !_saveFailureNotified)
        {
            // Once per session: the settings apply now but will not survive a restart, and a
            // save that fails silently teaches the user that Pawse forgets things.
            _saveFailureNotified = true;
            // A portable copy keeps its settings next to the exe and nowhere else (Log.ChooseBaseDir),
            // so a read-only folder is something only moving Pawse fixes - say that.
            _tray?.Notify("Pawse", Deployment.IsPortable
                ? $"This portable copy can't save its settings in {Path.GetDirectoryName(Config.PathOnDisk())}. "
                  + "They apply now but will be lost when Pawse quits - move Pawse to a folder you can write to."
                : $"Your settings could not be saved to {Config.PathOnDisk()}. They apply now but will be lost when Pawse quits.");
        }
        Log.Enable(config.General.Logging);
        _controller.RebuildMatchers();
        // The mouse hook only exists while BlockMouse is on (HookThread.SyncMouse) -
        // tell the hook thread to reconcile now rather than on its next periodic tick.
        _hooks?.SyncMouseHook();
        // Re-arm the auto-unlock timer against the NEW settings if we're locked right
        // now: without this, a timer disabled mid-lock still fires at its old deadline
        // (a surprise unlock), and a timer enabled mid-lock never arms at all - which
        // with mouse-block + timer-only unlock is a genuine lockout. The countdown
        // restarts from the full new duration; the overlay text says as much.
        if (_controller.IsLocked)
        {
            StopAutoUnlock();
            StartAutoUnlock();
        }
        _tray!.DoubleClickUnlock = config.General.TrayDoubleClickUnlock;
        // Only when the checkbox changed - see SettingsWindow.AutostartChange for why.
        if (_settingsWindow?.AutostartChange is { } autostart && Autostart.SetEnabled(autostart) is { } refused)
            _tray?.Notify("Pawse", refused);
        SyncUpdateSchedule();   // re-armed or stopped to match the new setting
        // If Block Win+L was just enabled but this PC needs admin to apply it, offer to
        // relaunch elevated (same prompt as startup). On Yes we hand off and stop here.
        if (RelaunchElevatedIfWinLockNeedsIt(config)) return;
        // Apply/revert the OS-level guards to match the new settings + current state.
        _systemBlock?.Apply(_controller.IsLocked, background: true, notify: true);

        if (config.Overlay.Enabled)
        {
            CreateOverlays(config);
            if (_controller.IsLocked) ShowOverlays();
        }
        else
        {
            // Destroy, not just hide: a hidden-but-alive window is exactly what let a
            // disabled popup come back on the next lock.
            DestroyOverlays();
        }

        Log.Info("config applied: " + config.Summary());
    }

    /// <summary>
    /// When Block Win+L is enabled but this PC's policy key is ACL-locked (managed
    /// machine) and Pawse isn't elevated, the block silently fails on lock. Offer to
    /// relaunch as administrator up front. Returns true if we handed off to an elevated
    /// instance (caller should abort startup); false to keep starting un-elevated.
    /// </summary>
    private bool RelaunchElevatedIfWinLockNeedsIt(Config config)
    {
        if (!config.SystemBlock.WinLock) return false;
        if (!SystemBlock.Allowed) return false;               // portable: the block is never applied
        if (Elevation.IsElevated()) return false;            // already admin - restart is pointless
        if (!WorkstationLock.NeedsElevation()) return false; // works un-elevated on this PC

        if (!Elevation.CanElevateSelf())
        {
            // A standard user. "Run as administrator" would run Pawse as a different account,
            // whose policy hive winlogon never consults for this session - inert, while the log
            // would say it worked. Say so instead of asking for credentials that cannot help.
            Log.Warn("win+l: this PC needs elevation and this account is not an administrator - the block cannot work here");
            MessageBox.Show(
                "“Block Win+L” is turned on, but on this PC it needs administrator rights, and this " +
                "account isn't an administrator. Running Pawse under another account would not block " +
                "Win+L for you, so the setting can't work here.\n\n" +
                "Turn it off under Settings → Locking to stop this message.",
                "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var choice = MessageBox.Show(
            "“Block Win+L” is turned on, but on this PC it needs administrator rights to " +
            "work. Restart Pawse as administrator now?\n\n" +
            "You can also do this later from the tray menu, or turn the setting off in Settings.",
            "Pawse", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (choice == MessageBoxResult.Yes && Elevation.RelaunchAsAdmin())
        {
            Shutdown();
            return true; // handed off to the elevated instance - stop this startup
        }
        return false;   // declined / UAC cancelled → keep running un-elevated
    }

    private void QuitWithConfirm()
    {
        // Quitting removes the lock. While locked that deserves one deliberate click -
        // the overlay's hold-button friction shouldn't be undone by a stray hit on the
        // tray menu. (Reaching this menu needs a live mouse, so the dialog is usable.)
        if (_controller?.IsLocked == true)
        {
            var choice = MessageBox.Show(
                "Pawse is locked. Quit Pawse and release the keyboard?",
                "Pawse", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return;
        }
        Shutdown();
    }

    private void RestartAsAdmin()
    {
        // A standard user's UAC prompt runs the copy as the administrator whose password is
        // typed. Machine-wide state (the Keyboard Filter) works that way; per-user state (the
        // Win+L policy) lands in the wrong hive and does nothing. Let them decide, informed.
        if (!Elevation.CanElevateSelf()
            && MessageBox.Show(
                "This account isn't an administrator, so Pawse would run under the administrator " +
                "account you sign in with. Blocking browser / media keys works that way; blocking " +
                "Win+L does not - it would apply to that account, not to yours.\n\nContinue?",
                "Pawse", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        // Launch an elevated copy; if the user approves UAC, hand off by shutting
        // down so the new instance can take the single-instance mutex. If UAC is
        // declined, RelaunchAsAdmin returns false and we keep running unchanged.
        if (Elevation.RelaunchAsAdmin())
            Shutdown();
    }

    private void OpenConfigFile() => ShellOpen.Open(Config.PathOnDisk(), "config file");

    private void OnExit(object sender, ExitEventArgs e)
    {
        Log.Info("shutting down");
        // Stop listening first - we're already on our way out, and a second request
        // arriving mid-teardown would only re-enter Shutdown.
        try { _quitChannel?.Dispose(); } catch { /* ignore */ }
        // Abandon any download still running: it has nowhere to report back to now.
        StopUpdates();
        try { _controller?.Disengage("shutdown"); } catch { /* ignore */ }
        // Disengage's UI-thread dispatch may not run before we exit, so revert the
        // OS-level guards synchronously here (delete the policy value, disable WEKF).
        try { _systemBlock?.Apply(locked: false, background: false); } catch { /* ignore */ }
        try { _hooks?.Stop(); } catch { /* ignore */ }
        try { DestroyOverlays(); } catch { /* ignore */ }
        _tray?.Dispose();
        _singleton?.Dispose();
        Log.Info("shutdown complete");
        Log.Shutdown();
    }
}
