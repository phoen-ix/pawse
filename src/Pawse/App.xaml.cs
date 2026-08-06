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
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _autoUnlock;
    private DispatcherTimer? _autoUpdate;
    private bool _canLock;   // false if the keyboard hook failed to install (locking disabled)
    private bool _updateCheckBusy;

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
            // If we were relaunched elevated (--replace), wait briefly for the old
            // instance to exit and release the mutex before giving up.
            bool acquired = false;
            if (e.Args.Contains(Elevation.ReplaceArg))
            {
                try { acquired = _singleton.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }
            }
            if (!acquired)
            {
                MessageBox.Show("Pawse is already running - look for the paw in the system tray.",
                    "Pawse", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        Log.Init(Version);
        InstallExceptionHandlers();

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
            CreateOverlay(config);

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

    private void CreateOverlay(Config config)
    {
        _overlay = new OverlayWindow();
        _overlay.Configure(config);
        _overlay.UnlockByHold += () => _controller!.Disengage("hold");
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
                    _overlay?.ShowLocked();
                    StartAutoUnlock();
                }
                else
                {
                    StopAutoUnlock();
                    _overlay?.HideLocked();
                }
            }
            catch (Exception ex) { Log.Error("apply lock state", ex); }
        }));
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
        _controller.RebuildMatchers();
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
        // If Block Win+L was just enabled but this PC needs admin to apply it, offer to
        // relaunch elevated (same prompt as startup). On Yes we hand off and stop here.
        if (RelaunchElevatedIfWinLockNeedsIt(config)) return;
        // Apply/revert the OS-level guards to match the new settings + current state.
        _systemBlock?.Apply(_controller.IsLocked, background: true, notify: true);

        if (config.Overlay.Enabled)
        {
            if (_overlay == null) CreateOverlay(config);
            else _overlay.Configure(config);
            if (_controller.IsLocked) _overlay!.ShowLocked();
        }
        else
        {
            _overlay?.HideLocked();
        }

        Log.Info("config applied: " + config.Summary());
    }

    /// <summary>
    /// Settings → Updates → "Check now", and the opt-in daily check. Pawse reaches the
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

        Log.Info($"update check ({(interactive ? "requested" : "daily")}): asking {UpdateCheck.FeedUrl} (this copy is {current})");
        var result = await UpdateCheck.FetchAsync();
        StampCheckedNow();

        if (result.Info is not { } info)
        {
            Log.Warn($"update check failed: {result.Error}");
            if (!interactive) return;   // a daily check that can't reach the net says nothing
            _settingsWindow?.ShowUpdateStatus(result.Error ?? "The check failed.");
            OfferDownloadsPage((result.Error ?? "The update check failed.") +
                               "\n\nOpen the downloads page instead?");
            return;
        }

        if (!UpdateCheck.IsNewer(current, info.Version))
        {
            Log.Info($"update check: {current} is current (the feed offers {info.Version})");
            if (interactive) _settingsWindow?.ShowUpdateStatus($"Pawse {current} is the latest version.");
            return;
        }

        if (!interactive)
        {
            // Opting into the daily check opts into being told, not into a dialog appearing
            // over whatever you were doing - let alone an install.
            Log.Info($"update check: {info.Version} available (daily check - notifying only)");
            _tray?.Notify("Pawse", $"Pawse {info.Version} is available. Settings → Updates to install it.");
            return;
        }

        var kind = UpdateCheck.DetectInstall();
        var asset = kind switch
        {
            InstallKind.InstalledFull => info.Full,
            InstallKind.InstalledMin => info.Min,
            _ => null,
        };
        Log.Info($"update check: {info.Version} is available (this copy is {kind})");

        _settingsWindow?.ShowUpdateStatus($"Pawse {info.Version} is available.");

        if (asset is null)
        {
            OfferDownloadsPage(
                $"Pawse {info.Version} is available (you have {current}).\n\n" +
                (kind == InstallKind.Portable
                    ? "This is a portable copy, so it can't replace itself."
                    : "The update doesn't list an installer for this build.") +
                "\n\nOpen the downloads page?", info.NotesUrl);
            return;
        }

        string lockedNote = _controller?.IsLocked == true
            ? "\n\nPawse is locked: installing closes it, which releases the keyboard."
            : "";
        if (MessageBox.Show(
                $"Pawse {info.Version} is available (you have {current}).\n\nDownload and install it now?" + lockedNote,
                "Pawse", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            Log.Info("update: declined by the user");
            return;
        }

        _tray?.Notify("Pawse", $"Downloading Pawse {info.Version}…");
        string variant = kind == InstallKind.InstalledFull ? "full" : "min";
        var installer = await UpdateCheck.DownloadVerifiedAsync(asset, $"Pawse-Setup-{info.Version}-{variant}.exe");
        if (installer == null)
        {
            OfferDownloadsPage("The download failed or didn't match its checksum, so it was discarded.\n\n" +
                               "Open the downloads page instead?", info.NotesUrl);
            return;
        }

        // Hand over: the installer asks this instance to quit through the same channel the
        // installer and uninstaller always use (QuitSignal), so there is nothing left to do
        // here - including undoing the lock, which OnExit does on the way out.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer) { UseShellExecute = true });
            Log.Info($"update: handed over to {installer}");
        }
        catch (Exception ex)
        {
            Log.Error("update: starting the installer", ex);
            OfferDownloadsPage("The installer could not be started.\n\nOpen the downloads page instead?", info.NotesUrl);
        }
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
        if (_controller?.Config.Update.AutoCheck != true) return;
        _autoUpdate = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _autoUpdate.Tick += (_, _) =>
        {
            _autoUpdate!.Interval = TimeSpan.FromHours(1);
            if (_controller?.Config.Update.AutoCheck != true) { StopAutoUpdateCheck(); return; }
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
        StopAutoUpdateCheck();
        try { _controller?.Disengage("shutdown"); } catch { /* ignore */ }
        // Disengage's UI-thread dispatch may not run before we exit, so revert the
        // OS-level guards synchronously here (delete the policy value, disable WEKF).
        try { _systemBlock?.Apply(locked: false, background: false); } catch { /* ignore */ }
        try { _hooks?.Stop(); } catch { /* ignore */ }
        try { if (_overlay != null) { _overlay.AllowClose = true; _overlay.Close(); } } catch { /* ignore */ }
        _tray?.Dispose();
        _singleton?.Dispose();
        Log.Info("shutdown complete");
        Log.Shutdown();
    }
}
