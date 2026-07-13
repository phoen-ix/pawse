using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Pawse.Core;
using Pawse.UI;

namespace Pawse;

public partial class App : Application
{
    private Mutex? _singleton;
    private LockController? _controller;
    private SystemBlock? _systemBlock;
    private KeyboardHook? _kbHook;
    private MouseHook? _mouseHook;
    private TrayIcon? _tray;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _autoUnlock;
    private bool _canLock;   // false if the keyboard hook failed to install (locking disabled)

    private static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private void OnStartup(object sender, StartupEventArgs e)
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

        var config = Config.Load();
        bool unlockRepaired = EnsureUsableUnlock(config);
        Log.Info("config: " + config.Summary());

        // If Block Win+L is on but this (managed) PC needs admin to apply it and we're
        // not elevated, offer to relaunch as admin before building anything we'd tear down.
        if (RelaunchElevatedIfWinLockNeedsIt(config)) return;

        _controller = new LockController(config);

        _tray = new TrayIcon();
        _tray.ToggleRequested += () => { if (_canLock) _controller!.Toggle(); };
        _tray.SettingsRequested += OpenSettings;
        _tray.OpenConfigRequested += OpenConfigFile;
        _tray.RestartAsAdminRequested += RestartAsAdmin;
        _tray.QuitRequested += Shutdown;

        if (unlockRepaired)
            _tray.Notify("Pawse",
                "Your saved config had no working unlock method, so the default unlock chord " +
                "(Ctrl+L) was enabled to keep you from getting locked out.");

        // OS-level Win+L guard (opt-in). Sweep first so a value left behind by a
        // crash-while-locked is reverted before any StartLocked engage re-applies it.
        _systemBlock = new SystemBlock(config, _tray.Notify);
        _systemBlock.Apply(locked: false, background: true);

        if (config.Overlay.Enabled)
            CreateOverlay(config);

        _controller.LockedChanged += OnLockedChanged;

        _kbHook = new KeyboardHook(_controller);
        _canLock = _kbHook.Install();
        if (_canLock)
        {
            _mouseHook = new MouseHook(_controller);
            _mouseHook.Install();

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
        if (config.HasUsableUnlock()) return false;
        Log.Warn("config has no usable unlock method - enabling the default chord to prevent lockout");
        config.Unlock.Chord.Enabled = true;
        if (Keys.ParseChord(config.Unlock.Chord.Keys).Count == 0)
            config.Unlock.Chord.Keys = new() { "Ctrl", "L" };
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
        // We're on the UI thread already (hook callback), but defer the actual UI
        // work so the hook callback returns immediately - a slow callback is killed.
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
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(_controller!.Config, () => _controller!.IsLocked);
            _settingsWindow.Applied += ApplyConfigChange;
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _controller!.SuppressLockHotkey = false;
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
        Autostart.SetEnabled(config.General.Autostart);
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
        try { _controller?.Disengage("shutdown"); } catch { /* ignore */ }
        // Disengage's UI-thread dispatch may not run before we exit, so revert the
        // OS-level guards synchronously here (delete the policy value, disable WEKF).
        try { _systemBlock?.Apply(locked: false, background: false); } catch { /* ignore */ }
        _kbHook?.Dispose();
        _mouseHook?.Dispose();
        try { if (_overlay != null) { _overlay.AllowClose = true; _overlay.Close(); } } catch { /* ignore */ }
        _tray?.Dispose();
        _singleton?.Dispose();
    }
}
