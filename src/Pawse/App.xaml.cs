using System.Threading;
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
    private DispatcherTimer? _autoUpdate;
    private bool _canLock;   // false if the keyboard hook failed to install (locking disabled)
    private bool _updateCheckBusy;

    /// <summary>An automatic install that arrived while the keyboard was locked. Installing
    /// closes Pawse, which hands the keyboard back - so it waits for the unlock instead.
    /// Deliberately not persisted: the next scheduled check would derive it again anyway.</summary>
    private UpdatePlan? _pendingUpdate;

    /// <summary>Cancels anything still in flight when Pawse quits - notably a part-finished
    /// 58 MB download, which would otherwise carry on and resume onto a dead dispatcher.</summary>
    private readonly System.Threading.CancellationTokenSource _shutdown = new();

    internal static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private void OnStartup(object sender, StartupEventArgs e)
    {
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
        _singleton = new Mutex(true, @"Local\Pawse-single-instance-2b8f9c", out bool created);
        if (!created)
        {
            // Relaunched by an outgoing instance (--replace): it is already on its way out and
            // told us so, no need to ask anyone anything.
            bool acquired = e.Args.Contains(Elevation.ReplaceArg) && WaitForMutex(TimeSpan.FromSeconds(5));
            if (!acquired && !TakeOverFromRunningInstance())
            {
                Shutdown();
                return;
            }
        }

        Log.Init(Version);
        InstallExceptionHandlers();

        // Clean up after a portable self-replace. Windows is still unmapping the exe we
        // replaced for a moment after that process let go of the mutex we just took, so
        // retry quietly off the UI thread and leave it for the next start if it never frees.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            for (int i = 0; i < 10 && !SelfReplace.SweepLeftovers(); i++)
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        });

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
        bool unlockRepaired = EnsureUsableUnlock(config);
        Log.Info("config: " + config.Summary());

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

        if (unlockRepaired)
            _tray.Notify("Pawse",
                "Your saved config had no working unlock method, so the default unlock chord " +
                "(Ctrl+L) was enabled to keep you from getting locked out.");

        // OS-level Win+L guard (opt-in). Sweep first so a value left behind by a
        // crash-while-locked is reverted before any StartLocked engage re-applies it.
        // The sweep only reverts Pawse's own leftovers (WorkstationLock's ownership
        // marker) - an admin-set DisableLockWorkstation is left alone.
        _systemBlock = new SystemBlock(config, _tray.Notify);
        _systemBlock.Apply(locked: false, background: true, notify: true);
        _systemBlock.WarnIfUnsweepableLeftovers();

        // A moved/renamed portable folder leaves the Run entry pointing at nothing;
        // re-point it at this exe (never resurrects an entry the user removed).
        Autostart.Repair();

        if (config.Overlay.Enabled)
            CreateOverlays(config);

        _controller.LockedChanged += OnLockedChanged;

        // Both hooks live on a dedicated pumping thread: callbacks stay serviced
        // (and keys stay swallowed) no matter how busy this UI thread is, and the
        // thread re-registers the hooks periodically in case the OS removed them.
        _hooks = new HookThread(_controller);
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

        StartAutoUpdateCheck();   // no-op unless the user turned it on

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
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("task exception", ex.Exception);
            ex.SetObserved();
        };
    }

    /// <summary>If the loaded config has no usable unlock method (hand-edited pawse.json, or
    /// all methods disabled/misconfigured), enable the default chord so a lock can always be
    /// undone. Returns true if it repaired anything.</summary>
    private static bool EnsureUsableUnlock(Config config)
    {
        if (!config.EnsureUsableUnlockFallback(out _)) return false;
        Log.Warn("config has no usable unlock method - enabling the default chord to prevent lockout");
        config.Save();
        return true;
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
                // The Win+L registry toggle runs inline inside Apply; the Keyboard-Filter
                // (WMI) work is dispatched off-thread inside Apply so this returns fast.
                _systemBlock?.Apply(locked, background: true, notify: true);
                if (locked)
                {
                    // Check the setting, don't just check that a window exists. Turning the
                    // popup off used to hide the window without destroying it, so the next
                    // lock showed it again - the setting was honoured only at startup, which
                    // made a restart look like the fix.
                    if (_controller!.Config.Overlay.Enabled) ShowOverlays();
                    StartAutoUnlock();
                }
                else
                {
                    StopAutoUnlock();
                    HideOverlays();
                    // The keyboard is the user's again, so an update held back while locked
                    // can go ahead now.
                    if (_pendingUpdate is { } deferred)
                    {
                        _pendingUpdate = null;
                        ApplyPendingUpdate(deferred);
                    }
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
                MessageBox.Show(
                    "The Pawse that's running has administrator rights, so this copy can't ask "
                        + "it to close.\n\nQuit it from the tray (right-click the paw, then Quit) "
                        + "and start this one again.",
                    "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
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
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(_controller!.Config, () => _controller!.IsLocked);
            _settingsWindow.Applied += ApplyConfigChange;
            _settingsWindow.CheckUpdatesRequested += () => CheckForUpdates(interactive: true);
            _settingsWindow.DownloadsPageRequested += OpenDownloadsPage;
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
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("open settings", ex);
            // Don't leave the lock hotkey wedged off or a half-open window latched.
            _settingsWindow = null;
            if (_controller != null) _controller.SuppressLockHotkey = false;
        }
    }

    private void ApplyConfigChange()
    {
        var config = _controller!.Config;
        config.Save();
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
        Autostart.SetEnabled(config.General.Autostart);
        StartAutoUpdateCheck();   // re-armed or stopped to match the new setting
        // Turning automatic installs back off cancels one that was waiting for the unlock:
        // the consent it was riding on has just been withdrawn.
        if (config.Update.ModeValue != Config.UpdateMode.Automatic && _pendingUpdate is not null)
        {
            Log.Info("update: dropping the deferred install - automatic updates were switched off");
            _pendingUpdate = null;
        }
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
    /// Settings → About → "Check now", and the opt-in daily check. Pawse reaches the
    /// network here and nowhere else. Re-entry is refused rather than queued.
    /// </summary>
    /// <param name="interactive">True when the user just pressed the button: it may put
    /// dialogs on screen and offer to install. False for the daily check, which is allowed
    /// to say "there's an update" through the tray balloon and nothing more.</param>
    private async void CheckForUpdates(bool interactive)
    {
        if (_updateCheckBusy)
        {
            if (interactive) _settingsWindow?.ShowUpdateStatus("A check is already running.");
            return;
        }
        _updateCheckBusy = true;
        try
        {
            await RunUpdateCheck(interactive);
        }
        catch (Exception ex)
        {
            // async void: no caller can catch this, and looking for an update must never be
            // the thing that takes the app down.
            Log.Error("update check", ex);
            if (interactive)
            {
                _settingsWindow?.ShowUpdateStatus("The check failed.");
                MessageBox.Show("Pawse could not check for updates.\n\n" + ex.Message,
                    "Pawse", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _updateCheckBusy = false;
        }
    }

    private async Task RunUpdateCheck(bool interactive)
    {
        string current = Version;
        if (current == UpdateCheck.DevVersion)
        {
            Log.Info("update check: development build, nothing to compare against");
            if (interactive)
                _settingsWindow?.ShowUpdateStatus($"Development build ({current}) - nothing to compare.");
            return;
        }

        var kind = UpdateCheck.DetectInstall();
        Log.Info($"update check ({(interactive ? "requested" : "scheduled")}): this copy is {current} ({kind})");

        // Only from the second attempt on: a check that works first time - almost all of them -
        // should look exactly as it always has.
        var progress = interactive
            ? new Progress<int>(attempt =>
            {
                if (attempt > 1)
                    _settingsWindow?.ShowUpdateProgress($"Checking… ({attempt} of {UpdateCheck.MaxCheckAttempts})");
            })
            : null;

        var plan = await UpdateCheck.CheckAsync(current, kind, _shutdown.Token, progress);
        StampCheckedNow();
        Log.Info("update check: " + plan);

        switch (plan.Verdict)
        {
            case UpdateVerdict.UpToDate:
                if (interactive) _settingsWindow?.ShowUpdateStatus($"Pawse {current} is the latest version.");
                break;

            case UpdateVerdict.Available:
                ReportAvailable(plan, current, interactive);
                break;

            case UpdateVerdict.Installable:
                if (interactive) await OfferInstall(plan, current);
                else await AutoInstall(plan);
                break;

            default:
                // A scheduled check that cannot reach the network says nothing at all.
                if (!interactive) break;
                // No dialog: the check has already tried five times, and the thing most likely
                // to help is a sixth on the user's own timing - once the firewall has been
                // answered, or the network is back. So the button becomes that, and the
                // downloads page sits next to it instead of interrupting.
                _settingsWindow?.ShowUpdateFailure(plan.Error ?? "The check failed.");
                break;
        }
    }

    /// <summary>Newer, but nothing this copy can verify or install by itself.</summary>
    private void ReportAvailable(UpdatePlan plan, string current, bool interactive)
    {
        if (!interactive)
        {
            _tray?.Notify("Pawse", $"Pawse {plan.Version} is available. Settings → About to install it.");
            return;
        }
        _settingsWindow?.ShowUpdateStatus($"Pawse {plan.Version} is available.");
        OfferDownloadsPage(
            $"Pawse {plan.Version} is available (you have {current}).\n\n" +
            "This release doesn't offer anything Pawse can verify for this copy, so it won't " +
            "download it.\n\nOpen the downloads page?", plan.NotesUrl);
    }

    /// <summary>The user pressed Check now and there is something installable.</summary>
    private async Task OfferInstall(UpdatePlan plan, string current)
    {
        _settingsWindow?.ShowUpdateStatus($"Pawse {plan.Version} is available.");

        // Only pawse.at's checksum crosses hosts. Say so when it doesn't, rather than
        // implying a verification that only proves the transfer wasn't corrupted.
        string sameHostNote = plan.Checksum == ChecksumSource.GitHubSums
            ? "\n\nIts checksum comes from the download's own host this time, so it confirms the " +
              "transfer but not much more."
            : "";
        string lockedNote = _controller?.IsLocked == true
            ? "\n\nPawse is locked: installing closes it, which releases the keyboard."
            : "";
        string portableNote = !UpdateCheck.IsInstalled(plan.Kind)
            ? "\n\nThis is a portable copy, so Pawse will replace its own exe and restart."
            : "";

        if (MessageBox.Show(
                $"Pawse {plan.Version} is available (you have {current}).\n\nDownload and install it now?"
                    + portableNote + sameHostNote + lockedNote,
                "Pawse", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            Log.Info("update: declined by the user");
            return;
        }
        await ApplyUpdate(plan, unattended: false);
    }

    /// <summary>A scheduled check found something installable. Everything that would surprise
    /// the user - a UAC prompt, a runtime download, the keyboard being handed back mid-lock -
    /// declines here instead, and says why in the log.</summary>
    private async Task AutoInstall(UpdatePlan plan)
    {
        if (AutoInstallRefusal(plan) is { } refusal)
        {
            Log.Info($"update: {plan.Version} available but not installed automatically - {refusal}");
            _tray?.Notify("Pawse", $"Pawse {plan.Version} is available. Settings → About to install it.");
            return;
        }
        if (_controller?.IsLocked == true)
        {
            // Installing closes Pawse, and closing Pawse releases the keyboard. Doing that
            // unattended because a timer fired is precisely what the lock exists to prevent,
            // so hold the plan and take it at the next unlock. Nothing has been downloaded
            // yet, so nothing goes stale while it waits.
            Log.Info($"update: {plan.Version} is ready but Pawse is locked - deferring until unlock");
            _pendingUpdate = plan;
            return;
        }
        await ApplyUpdate(plan, unattended: true);
    }

    /// <summary>Null when a check nobody is watching may install this on its own; otherwise
    /// the reason it may not.</summary>
    private string? AutoInstallRefusal(UpdatePlan plan)
    {
        var cfg = _controller!.Config.Update;
        if (cfg.ModeValue != Config.UpdateMode.Automatic)
            return "the setting is notify-only";
        if (!UpdateCheck.MayInstallUnattended(plan))
            return plan.FeedAllowsAuto
                ? "there is no checksum from pawse.at to cross-check the download against"
                : "the feed has paused automatic installs";
        if (!UpdateCheck.MayRetryAutoInstall(plan.Version!, cfg.LastAutoAttemptVersion,
                                             cfg.LastAutoAttemptUtc, DateTime.UtcNow))
            return $"{plan.Version} was already tried and did not take";
        // Per-user installs update themselves; per-machine needs administrator rights, and an
        // unattended check must never be the thing that raises a UAC prompt.
        if (UpdateCheck.IsInstalled(plan.Kind) && UpdateCheck.DetectScope() == InstallScope.PerMachine)
            return "this copy was installed for everyone on this PC, which needs administrator rights";
        if (!SelfReplace.CanWriteTo(Log.ExeDir()))
            return "the folder Pawse lives in is not writable";
        return null;
    }

    /// <summary>Download, verify, then hand over to the installer or replace the exe.</summary>
    private async Task ApplyUpdate(UpdatePlan plan, bool unattended)
    {
        _tray?.Notify("Pawse", $"Downloading Pawse {plan.Version}…");
        var file = await UpdateCheck.DownloadVerifiedAsync(plan.Asset!, plan.FileName!, _shutdown.Token);
        if (file is null)
        {
            Log.Error($"update: {plan.Version} failed to download or did not match its checksum");
            if (unattended)
                _tray?.Notify("Pawse", $"Pawse {plan.Version} could not be verified, so it was discarded.");
            else
                OfferDownloadsPage("The download failed or didn't match its checksum, so it was discarded.\n\n" +
                                   "Open the downloads page instead?", plan.NotesUrl);
            return;
        }

        // Stamp the attempt BEFORE handing over: a successful handover kills this process long
        // before anything after it could run.
        var cfg = _controller!.Config.Update;
        cfg.LastAutoAttemptVersion = plan.Version;
        cfg.LastAutoAttemptUtc = DateTime.UtcNow;
        _controller.Config.Save();

        if (UpdateCheck.IsInstalled(plan.Kind)) LaunchInstaller(plan, file, unattended);
        else ReplacePortable(plan, file, unattended);
    }

    /// <summary>Hand over to the downloaded installer. It asks this instance to quit over
    /// QuitSignal - the channel every install and uninstall uses - so there is nothing to tear
    /// down here; OnExit still runs and still reverts the Win+L and media-key blocks.</summary>
    private void LaunchInstaller(UpdatePlan plan, string installer, bool unattended)
    {
        // Only an update nobody asked to watch runs silently. When the user pressed Check now
        // they get the wizard: it is the visible, cancellable path, and it is what every
        // previous version did.
        //
        // Never silent for a per-machine install either - pawse.nsi's .onInit sets error level
        // 2 and quits when /S meets an AllUsers install without an admin token. AutoInstallRefusal
        // already declines those before we get here; this is belt-and-braces.
        bool silent = unattended && UpdateCheck.DetectScope() != InstallScope.PerMachine;
        // /RESTART because a silent install never reaches the finish page, so nothing would
        // bring the tray paw back. /NORUNTIME because EnsureDotnet's prompt defaults to Yes
        // under /S, and an update must not pull ~55 MB of runtime machine-wide on that default
        // - the wizard asks that question properly, so the interactive path doesn't need it.
        string args = silent ? "/S /RESTART /NORUNTIME" : "";
        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installer,
                Arguments = args,
                UseShellExecute = true,
            });
            Log.Info($"update: handed over to {installer} {(silent ? args : "(interactive)")}");
            if (silent) WatchSilentInstaller(process, plan);
        }
        catch (Exception ex)
        {
            Log.Error("update: starting the installer", ex);
            if (unattended)
                _tray?.Notify("Pawse", $"Pawse {plan.Version} could not be installed - Settings → About to try it yourself.");
            else
                OfferDownloadsPage("The installer could not be started.\n\nOpen the downloads page instead?",
                                   plan.NotesUrl);
        }
    }

    /// <summary>Portable copies have no installer: swap the exe and restart.</summary>
    private void ReplacePortable(UpdatePlan plan, string zip, bool unattended)
    {
        var outcome = SelfReplace.Run(zip, plan.Kind, plan.Version!);
        switch (outcome.Result)
        {
            case ReplaceResult.Handover:
                Log.Info($"update: replaced by Pawse {plan.Version}, shutting down");
                Shutdown();
                break;

            case ReplaceResult.Stranded:
                // The one failure the user has to act on, so it gets a dialog whether or not
                // anyone asked for this update - and Pawse deliberately keeps running, because
                // this process is the only working copy left.
                Log.Error("update: stranded after a failed self-replace");
                MessageBox.Show(outcome.Message, "Pawse", MessageBoxButton.OK, MessageBoxImage.Error);
                break;

            default:
                if (unattended) _tray?.Notify("Pawse", $"Pawse {plan.Version} could not be installed. {outcome.Message}");
                else OfferDownloadsPage(outcome.Message + "\n\nOpen the downloads page instead?", plan.NotesUrl);
                break;
        }
    }

    /// <summary>A silent installer that refuses the job just exits with a code - and we are
    /// still here to see it, because a successful one would have asked us to quit long before
    /// this fires. Turns a silent no-op into something the log and the tray mention.</summary>
    private void WatchSilentInstaller(System.Diagnostics.Process? process, UpdatePlan plan)
    {
        if (process is null) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (!process.HasExited || process.ExitCode == 0) return;
                Log.Error($"update: the silent installer exited with {process.ExitCode} and Pawse is still {Version}");
                _tray?.Notify("Pawse",
                    $"Pawse {plan.Version} could not be installed automatically. Settings → About to try it yourself.");
            }
            catch (Exception ex) { Log.Error("update: watching the installer", ex); }
            finally { process.Dispose(); }
        };
        timer.Start();
    }

    /// <summary>Take a deferred update now that the keyboard is free. async void because it is
    /// called from an event handler; it must never let an exception escape onto the dispatcher.</summary>
    private async void ApplyPendingUpdate(UpdatePlan plan)
    {
        try { await ApplyUpdate(plan, unattended: true); }
        catch (Exception ex) { Log.Error("update: applying the deferred update", ex); }
    }

    /// <summary>Remember that a check happened, so the daily one doesn't run again on every
    /// restart. Stamped whether or not the fetch succeeded: an offline machine should retry
    /// tomorrow, not once an hour.</summary>
    private void StampCheckedNow()
    {
        if (_controller is null) return;
        _controller.Config.Update.LastCheckUtc = DateTime.UtcNow;
        _controller.Config.Save();
    }

    /// <summary>
    /// Arms the opt-in daily check. The first look is a minute after startup - nothing about
    /// the lock should ever wait on the network - and it then ticks hourly, doing nothing
    /// until <see cref="UpdateCheck.IsCheckDue"/> says a day has passed.
    /// </summary>
    private void StartAutoUpdateCheck()
    {
        StopAutoUpdateCheck();
        if (_controller?.Config.Update.ModeValue is not (Config.UpdateMode.Notify or Config.UpdateMode.Automatic)) return;
        _autoUpdate = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _autoUpdate.Tick += (_, _) =>
        {
            _autoUpdate!.Interval = TimeSpan.FromHours(1);
            if (_controller?.Config.Update.ModeValue is not (Config.UpdateMode.Notify or Config.UpdateMode.Automatic))
            { StopAutoUpdateCheck(); return; }
            if (!UpdateCheck.IsCheckDue(_controller.Config.Update.LastCheckUtc, DateTime.UtcNow)) return;
            CheckForUpdates(interactive: false);
        };
        _autoUpdate.Start();
        Log.Info("daily update check armed");
    }

    private void StopAutoUpdateCheck()
    {
        _autoUpdate?.Stop();
        _autoUpdate = null;
    }

    /// <summary>The downloads page, straight up - no dialog. Used by the button that appears
    /// after a failed check, where the user has already been told what happened.</summary>
    private static void OpenDownloadsPage()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(UpdateCheck.ReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("open downloads page", ex); }
    }

    /// <summary>Every dead end in the update flow ends the same way: say what happened, and
    /// offer the page the user would have gone to anyway.</summary>
    private static void OfferDownloadsPage(string text, string? url = null)
    {
        if (MessageBox.Show(text, "Pawse", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url ?? UpdateCheck.ReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("open downloads page", ex); }
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
        if (Elevation.IsElevated()) return false;            // already admin - restart is pointless
        if (!WorkstationLock.NeedsElevation()) return false; // works un-elevated on this PC

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
        // Launch an elevated copy; if the user approves UAC, hand off by shutting
        // down so the new instance can take the single-instance mutex. If UAC is
        // declined, RelaunchAsAdmin returns false and we keep running unchanged.
        if (Elevation.RelaunchAsAdmin())
            Shutdown();
    }

    private void OpenConfigFile()
    {
        try
        {
            var path = Config.PathOnDisk();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("open config file", ex); }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        Log.Info("shutting down");
        // Stop listening first - we're already on our way out, and a second request
        // arriving mid-teardown would only re-enter Shutdown.
        try { _quitChannel?.Dispose(); } catch { /* ignore */ }
        // Abandon any download still running: it has nowhere to report back to now.
        try { _shutdown.Cancel(); } catch { /* ignore */ }
        StopAutoUpdateCheck();
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
