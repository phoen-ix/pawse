using Microsoft.Win32;

namespace Pawse.Core;

/// <summary>
/// Suppresses <c>Win+L</c> (and the Ctrl+Alt+Del "Lock" / Start-menu Lock command)
/// for the current user, only while the lock is engaged.
///
/// <para>The keyboard hook can't stop Win+L: swallowing the keystroke does not
/// prevent the workstation lock, because the lock is initiated by winlogon below
/// the hook. The one supported lever is the per-user policy value
/// <c>DisableLockWorkstation</c>, which Winlogon honours at lock time. We set it on
/// lock and delete it on unlock, so the behaviour is fully reverted.</para>
///
/// <para>Lives in HKCU ⇒ no admin, no reboot, effective on the next lock attempt.
/// Ownership is tracked via a marker under <c>HKCU\Software\Pawse</c> that records
/// the pre-Pawse state and is written <em>before</em> the policy value is touched:
/// <see cref="Restore"/> only ever reverts what Pawse itself set, so a value an
/// admin or the user put there deliberately is never deleted. The marker survives
/// a crash-while-locked, which is what lets the unconditional startup sweep (see
/// <see cref="SystemBlock"/>) repair exactly our own leftovers and nothing else.
/// The uninstaller reads the same marker (see packaging/pawse.nsi).</para>
/// </summary>
public static class WorkstationLock
{
    private const string PolicyKey =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "DisableLockWorkstation";

    // Pre-Pawse state of the policy value: 0|1 = it existed with that value, 2 = it
    // was absent. Present only while Pawse holds the policy value.
    private const string OwnerKey = @"Software\Pawse";
    private const string MarkerName = "PrevDisableLockWorkstation";
    private const int MarkerAbsent = 2;

    /// <summary>
    /// Disable Win+L / the Lock command. Returns false if the write was denied -
    /// on some (managed) machines the Policies key is ACL-locked and needs admin.
    /// </summary>
    public static bool Suppress()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PolicyKey);
            if (key?.GetValue(ValueName) is int cur && cur == 1)
                return true; // already blocked (admin policy, or our own re-apply) - nothing to record or write

            // Record the prior state FIRST: a crash after this point leaves the marker
            // in place, so Restore() (startup sweep / uninstaller) can still revert.
            using (var own = Registry.CurrentUser.CreateSubKey(OwnerKey))
                own?.SetValue(MarkerName,
                    key?.GetValue(ValueName) is int prev ? prev : MarkerAbsent,
                    RegistryValueKind.DWord);

            key?.SetValue(ValueName, 1, RegistryValueKind.DWord);
            Log.Info("win+l: DisableLockWorkstation=1 (lock suppressed)");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("win+l suppress: access denied (this machine needs Pawse elevated)", ex);
            return false;
        }
        catch (Exception ex) { Log.Error("win+l suppress", ex); return false; }
    }

    /// <summary>
    /// True when suppressing Win+L would need elevation: the policy key (or an ACL-locked
    /// parent) denies the current user write access - the managed-machine case. Otherwise
    /// false; <see cref="Suppress"/> can create/set the value without admin.
    ///
    /// <para>Mirrors <see cref="Suppress"/>'s exact access path (<c>CreateSubKey</c>) so the
    /// probe and the real writer agree - including when the leaf key is absent but an
    /// ACL-locked parent would deny creating it (which a bare <c>OpenSubKey</c> would miss).
    /// It writes no value, so Win+L itself is untouched; the empty key it may create is what
    /// <see cref="Suppress"/> would create on lock anyway.</para>
    /// </summary>
    public static bool NeedsElevation()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PolicyKey);
            return false; // created/opened for write → no elevation needed
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (System.Security.SecurityException) { return true; }
        catch (Exception ex) { Log.Warn("win+l elevation probe: " + ex.Message); return false; }
    }

    /// <summary>
    /// Put the policy value back to its pre-Pawse state - but only when the marker says
    /// Pawse set it. Without a marker this is a no-op, so the unconditional callers (the
    /// startup sweep, every unlock, exit) can never delete a value they don't own.
    /// Returns false when Pawse owes a revert but the write was denied (ACL-locked
    /// Policies key, no longer elevated) - the user's own Win+L is still disabled then,
    /// and the caller must say so out loud rather than bury it in the log.
    /// </summary>
    public static bool Restore()
    {
        try
        {
            int? prev = null;
            using (var own = Registry.CurrentUser.OpenSubKey(OwnerKey))
                if (own?.GetValue(MarkerName) is int m) prev = m;
            if (prev == null) return true; // not ours - never touch a value we didn't set

            using (var key = Registry.CurrentUser.OpenSubKey(PolicyKey, writable: true))
            {
                if (prev == MarkerAbsent) key?.DeleteValue(ValueName, throwOnMissingValue: false);
                else key?.SetValue(ValueName, prev.Value, RegistryValueKind.DWord);
            }
            using (var own = Registry.CurrentUser.OpenSubKey(OwnerKey, writable: true))
                own?.DeleteValue(MarkerName, throwOnMissingValue: false);
            Log.Info("win+l: restored pre-Pawse DisableLockWorkstation state (lock restored)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("win+l restore", ex);
            return false;
        }
    }
}
