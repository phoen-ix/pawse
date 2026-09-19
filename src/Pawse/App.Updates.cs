using System.Windows;
using System.Windows.Threading;
using Pawse.Core;
using Pawse.UI;

namespace Pawse;

/// <summary>
/// The self-updater's half of App: Settings → About → "Check now", the opt-in daily check, and
/// the download and handover that can follow. The Store build does not compile this file (see
/// Pawse.csproj) - the Store updates Pawse there, and Store policy does not allow an app to
/// update itself. App.xaml.cs reaches everything here only through the partial-method hooks
/// implemented at the top, so leaving the file out removes the calls along with it.
/// </summary>
public partial class App
{
    private DispatcherTimer? _autoUpdate;
    private bool _updateCheckBusy;

    /// <summary>An automatic install that arrived while the keyboard was locked. Installing
    /// closes Pawse, which hands the keyboard back - so it waits for the unlock instead.
    /// Deliberately not persisted: the next scheduled check would derive it again anyway.</summary>
    private UpdatePlan? _pendingUpdate;

    /// <summary>Cancels anything still in flight when Pawse quits - notably a part-finished
    /// 61 MB download, which would otherwise carry on and resume onto a dead dispatcher.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Clean up after a portable self-replace. Windows is still unmapping the exe we
    /// replaced for a moment after that process let go of the mutex we just took, so retry
    /// quietly off the UI thread and leave it for the next start if it never frees. The same
    /// loop clears the %TEMP% folders earlier updates downloaded into - an installer we handed
    /// over to may still be exiting from one of them.</summary>
    partial void SweepUpdateLeftovers()
    {
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10 && !(SelfReplace.SweepLeftovers() & UpdateCheck.SweepDownloads()); i++)
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        });
    }

    /// <summary>Arm or stop the daily check to match the setting - at startup and after every
    /// Settings save.</summary>
    partial void SyncUpdateSchedule()
    {
        StartAutoUpdateCheck();
        // Turning automatic installs back off cancels one that was waiting for the unlock:
        // the consent it was riding on has just been withdrawn.
        if (_controller?.Config.Update.ModeValue != Config.UpdateMode.Automatic && _pendingUpdate is not null)
        {
            Log.Info("update: dropping the deferred install - automatic updates were switched off");
            _pendingUpdate = null;
        }
    }

    /// <summary>The keyboard was just handed back: take an install that waited for it.</summary>
    partial void RunDeferredUpdate()
    {
        if (_pendingUpdate is { } deferred)
        {
            _pendingUpdate = null;
            ApplyPendingUpdate(deferred);
        }
    }

    partial void WireUpdates(SettingsWindow window)
    {
        window.CheckUpdatesRequested += () => CheckForUpdates(interactive: true);
        window.DownloadsPageRequested += OpenDownloadsPage;
    }

    partial void StopUpdates()
    {
        try { _shutdown.Cancel(); } catch { /* ignore */ }
        StopAutoUpdateCheck();
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

        // A scheduled check supersedes whatever an earlier one deferred: if this one still finds
        // the release installable and the keyboard still locked, AutoInstall defers it again; if
        // the feed has since paused automatic installs, the release was pulled, or the verdict
        // changed, the old plan must not outlive the check that would have refused it.
        if (!interactive && _pendingUpdate is not null)
        {
            Log.Info($"update: dropping the deferred install of {_pendingUpdate.Version} - re-deciding from this check");
            _pendingUpdate = null;
        }

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
            _tray?.Notify("Pawse", AvailableNotice(plan.Version));
            return;
        }
        _settingsWindow?.ShowUpdateStatus($"Pawse {plan.Version} is available.");
        // Say why, when the reason is a host that did not answer rather than a release with
        // nothing to offer - "try again later" and "get it yourself" are different advice.
        string why = plan.Error is null
            ? "This release doesn't offer anything Pawse can verify for this copy, so it won't download it."
            : $"{plan.Error} Without a checksum Pawse won't download it - try again later, or get it yourself.";
        OfferDownloadsPage($"Pawse {plan.Version} is available (you have {current}).\n\n{why}\n\nOpen the downloads page?",
                           plan.NotesUrl);
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
            _tray?.Notify("Pawse", AvailableNotice(plan.Version));
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
        // The installer relaunches Pawse through explorer.exe, i.e. un-elevated: a copy the user
        // restarted as administrator (Win+L on a managed PC, the media-key block) would come back
        // without the rights it was restarted for. Interactively the user is there to restart it.
        if (UpdateCheck.IsInstalled(plan.Kind) && Elevation.IsElevated())
            return "Pawse is running as administrator, and the copy the installer relaunches would not be";
        // The same two obstacles the About page warns about up front (SettingsWindow.ShowUpdateCaveat).
        return UpdateCheck.LocalInstallObstacle(plan.Kind) switch
        {
            LocalObstacle.PerMachine => "this copy was installed for everyone on this PC, which needs administrator rights",
            LocalObstacle.FolderNotWritable => "the folder Pawse lives in is not writable",
            _ => null,
        };
    }

    private static string AvailableNotice(string? version) =>
        $"Pawse {version} is available. Settings → About to install it.";

    private static string NotInstalledNotice(string? version) =>
        $"Pawse {version} could not be installed automatically. Settings → About to try it yourself.";

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

        // Stamp an UNATTENDED attempt BEFORE handing over: a successful handover kills this
        // process long before anything after it could run. An interactive install is not
        // stamped - the user watching the wizard may cancel it, and that must not park the
        // automatic retry of the same version for a week (see MayRetryAutoInstall).
        if (unattended)
        {
            var cfg = _controller!.Config.Update;
            cfg.LastAutoAttemptVersion = plan.Version;
            cfg.LastAutoAttemptUtc = DateTime.UtcNow;
            _controller.Config.Save();
        }

        if (UpdateCheck.IsInstalled(plan.Kind)) LaunchInstaller(plan, file, unattended);
        else await ReplacePortable(plan, file, unattended);
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
        // under /S, and an update must not pull ~57 MB of runtime machine-wide on that default
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
                _tray?.Notify("Pawse", NotInstalledNotice(plan.Version));
            else
                OfferDownloadsPage("The installer could not be started.\n\nOpen the downloads page instead?",
                                   plan.NotesUrl);
        }
    }

    /// <summary>Portable copies have no installer: swap the exe and restart.
    /// <para>Off the dispatcher, because the swap now waits for the successor to prove it is
    /// running (SelfReplace.StartSuccessor) and that wait is measured in seconds - on the UI
    /// thread it would freeze the tray, and the settings window with it, for the whole of a
    /// failing update. The await resumes on the dispatcher, so everything below still touches
    /// the tray and dialogs from the right thread.</para></summary>
    private async Task ReplacePortable(UpdatePlan plan, string zip, bool unattended)
    {
        var outcome = await Task.Run(() => SelfReplace.Run(zip, plan.Kind, plan.Version!))
                                .ConfigureAwait(true);
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
                _tray?.Notify("Pawse", NotInstalledNotice(plan.Version));
            }
            catch (Exception ex) { Log.Error("update: watching the installer", ex); }
            finally { process.Dispose(); }
        };
        timer.Start();
    }

    /// <summary>Take a deferred update now that the keyboard is free. async void because it is
    /// called from an event handler; it must never let an exception escape onto the dispatcher.
    /// Holds the same busy flag as a check, so a Check-now cannot start a second download while
    /// this one is in flight, and asks <see cref="AutoInstallRefusal"/> again: the setting or the
    /// retry stamp may have changed while the plan waited.</summary>
    private async void ApplyPendingUpdate(UpdatePlan plan)
    {
        if (_updateCheckBusy)
        {
            // A check is running right now; a scheduled one re-derives (or drops) the deferral
            // itself, and an interactive one has the user's attention already.
            Log.Info("update: a check is in progress - the deferred install yields to it");
            return;
        }
        if (AutoInstallRefusal(plan) is { } refusal)
        {
            Log.Info($"update: {plan.Version} was deferred but is not installed automatically now - {refusal}");
            return;
        }
        _updateCheckBusy = true;
        try { await ApplyUpdate(plan, unattended: true); }
        catch (Exception ex) { Log.Error("update: applying the deferred update", ex); }
        finally { _updateCheckBusy = false; }
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
    private static void OpenDownloadsPage() => ShellOpen.Open(UpdateCheck.ReleasesUrl, "downloads page");

    /// <summary>Every dead end in the update flow ends the same way: say what happened, and
    /// offer the page the user would have gone to anyway.</summary>
    private static void OfferDownloadsPage(string text, string? url = null)
    {
        if (MessageBox.Show(text, "Pawse", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        ShellOpen.Open(url ?? UpdateCheck.ReleasesUrl, "downloads page");
    }
}
