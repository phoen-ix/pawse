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
/// While the feature is on Pawse owns this value - <see cref="Restore"/> deletes
/// it. If Pawse is killed while locked the value persists, so App sweeps it on
/// startup (see <see cref="SystemBlock"/>).</para>
/// </summary>
public static class WorkstationLock
{
    private const string PolicyKey =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "DisableLockWorkstation";

    /// <summary>
    /// Disable Win+L / the Lock command. Returns false if the write was denied -
    /// on some (managed) machines the Policies key is ACL-locked and needs admin.
    /// </summary>
    public static bool Suppress()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PolicyKey);
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

    /// <summary>Re-enable Win+L / the Lock command (deletes the policy value).</summary>
    public static void Restore()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PolicyKey, writable: true);
            if (key?.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("win+l: DisableLockWorkstation removed (lock restored)");
            }
        }
        catch (Exception ex) { Log.Error("win+l restore", ex); }
    }

    /// <summary>True when the policy value is currently set to 1.</summary>
    public static bool IsSuppressed()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PolicyKey);
            return key?.GetValue(ValueName) is int i && i == 1;
        }
        catch (Exception ex) { Log.Error("win+l read", ex); return false; }
    }
}
