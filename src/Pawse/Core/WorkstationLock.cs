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
/// <see cref="Restore()"/> only ever reverts what Pawse itself set, so a value an
/// admin or the user put there deliberately is never deleted. The marker survives
/// a crash-while-locked, which is what lets the unconditional startup sweep (see
/// <see cref="SystemBlock"/>) repair exactly our own leftovers and nothing else.
/// The uninstaller reads the same marker (see packaging/pawse.nsi).</para>
///
/// <para>Nothing is left behind once the value is restored: the policy keys Pawse had to
/// create for it are recorded before they are created and removed again (while empty), and
/// <c>HKCU\Software\Pawse</c> goes as soon as it holds no marker. Every method takes the user
/// hive to work on - HKCU for the app, another account's hive for the uninstall cleanup.</para>
///
/// <para>Installed copies only: a portable copy writes nothing to the registry, so
/// <see cref="SystemBlock"/> never calls <see cref="Suppress()"/> there. It still runs the
/// sweep, which only ever reverts and deletes.</para>
/// </summary>
public static class WorkstationLock
{
    private const string PoliciesKey = @"Software\Microsoft\Windows\CurrentVersion\Policies";
    private const string PolicyKey = PoliciesKey + @"\System";
    private const string ValueName = "DisableLockWorkstation";

    /// <summary>Pawse's own key, for the markers below and SystemBlock's Keyboard Filter
    /// markers. Deleted once empty (<see cref="DropOwnerKeyIfEmpty"/>).</summary>
    internal const string OwnerKey = @"Software\Pawse";

    // Pre-Pawse state of the policy value: 0|1 = it existed with that value, 2 = it
    // was absent. Present only while Pawse holds the policy value.
    private const string MarkerName = "PrevDisableLockWorkstation";
    private const int MarkerAbsent = 2;

    // Which policy keys Suppress had to create, so Restore can remove them again: 1 = the
    // System key, 2 = Policies as well. Written BEFORE creating them, like the value marker.
    // Also read by the uninstaller (packaging/pawse.nsi) - change both or neither.
    private const string CreatedMarkerName = "PolicyKeysCreated";

    /// <summary>
    /// Disable Win+L / the Lock command. Returns false if the write was denied -
    /// on some (managed) machines the Policies key is ACL-locked and needs admin.
    /// </summary>
    public static bool Suppress() => Suppress(Registry.CurrentUser);

    internal static bool Suppress(RegistryKey userRoot)
    {
        int missing = 0;
        try
        {
            // The keys Pawse is about to create are recorded before they exist, so a crash
            // between here and the value below still lets Restore remove them.
            missing = MissingPolicyKeys(userRoot);
            if (missing > 0)
                using (var own = userRoot.CreateSubKey(OwnerKey))
                    own?.SetValue(CreatedMarkerName, missing, RegistryValueKind.DWord);

            // Opened for write before anything else is recorded: on an ACL-locked machine this
            // is what throws, and it must throw before a value marker claims Pawse set anything.
            using var key = userRoot.CreateSubKey(PolicyKey);
            object? existing = key?.GetValue(ValueName);
            if (existing is int cur && cur == 1)
                return true; // already blocked (admin policy, or our own re-apply) - nothing to record or write
            if (existing is not null && existing is not int)
            {
                // A string "1", a QWORD, whatever someone put there by hand: not a value Pawse
                // understands, so not one it may overwrite with a DWORD and later delete as
                // "absent" (the marker only knows 0, 1 and absent). Leave it exactly as found.
                Log.Warn($"win+l: DisableLockWorkstation exists as {key!.GetValueKind(ValueName)}, not a DWORD - leaving it alone, so Win+L is not blocked by Pawse");
                return true;
            }

            // Record the prior state FIRST: a crash after this point leaves the marker
            // in place, so Restore() (startup sweep / uninstaller) can still revert.
            using (var own = userRoot.CreateSubKey(OwnerKey))
                own?.SetValue(MarkerName, existing is int prev ? prev : MarkerAbsent, RegistryValueKind.DWord);

            key?.SetValue(ValueName, 1, RegistryValueKind.DWord);
            Log.Info("win+l: DisableLockWorkstation=1 (lock suppressed)");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("win+l suppress: access denied (this machine needs Pawse elevated)", ex);
            if (missing > 0) Restore(userRoot); // nothing was created - take the created-keys marker back
            return false;
        }
        catch (Exception ex) { Log.Error("win+l suppress", ex); return false; }
    }

    /// <summary>
    /// True when suppressing Win+L would need elevation: the policy key (or an ACL-locked
    /// parent) denies the current user write access - the managed-machine case. Otherwise
    /// false; <see cref="Suppress()"/> can create/set the value without admin.
    ///
    /// <para>Takes the same access path as <see cref="Suppress()"/> - including when the key is
    /// absent but an ACL-locked parent would deny creating it, which a bare read-only open
    /// would miss. It writes no value, so Win+L itself is untouched, and a key the probe had
    /// to create to find out is deleted again straight away: probing leaves nothing behind.</para>
    /// </summary>
    public static bool NeedsElevation() => NeedsElevation(Registry.CurrentUser);

    internal static bool NeedsElevation(RegistryKey userRoot)
    {
        try
        {
            int missing = MissingPolicyKeys(userRoot);
            if (missing == 0)
            {
                using var key = userRoot.OpenSubKey(PolicyKey, writable: true);
                return false; // opened for write → no elevation needed
            }
            using (userRoot.CreateSubKey(PolicyKey)) { }
            DeleteCreatedPolicyKeys(userRoot, missing);
            return false; // could create it → no elevation needed
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
    public static bool Restore() => Restore(Registry.CurrentUser);

    internal static bool Restore(RegistryKey userRoot)
    {
        try
        {
            int? prev = null;
            int created = 0;
            using (var own = userRoot.OpenSubKey(OwnerKey))
            {
                if (own?.GetValue(MarkerName) is int m) prev = m;
                if (own?.GetValue(CreatedMarkerName) is int c) created = c;
            }
            if (prev == null && created == 0) return true; // not ours - never touch a value we didn't set

            if (prev != null)
            {
                using var key = userRoot.OpenSubKey(PolicyKey, writable: true);
                if (prev == MarkerAbsent) key?.DeleteValue(ValueName, throwOnMissingValue: false);
                else key?.SetValue(ValueName, prev.Value, RegistryValueKind.DWord);
            }
            // Only what Pawse created, and only while empty - anything else put there since
            // belongs to someone else.
            if (created > 0) DeleteCreatedPolicyKeys(userRoot, created);

            using (var own = userRoot.OpenSubKey(OwnerKey, writable: true))
            {
                own?.DeleteValue(MarkerName, throwOnMissingValue: false);
                own?.DeleteValue(CreatedMarkerName, throwOnMissingValue: false);
            }
            DropOwnerKeyIfEmpty(userRoot);
            if (prev != null) Log.Info("win+l: restored pre-Pawse DisableLockWorkstation state (lock restored)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("win+l restore", ex);
            return false;
        }
    }

    /// <summary>Delete <c>Software\Pawse</c> once it holds nothing - no marker from here, none
    /// from SystemBlock's Keyboard Filter, no subkeys. A marker still owed keeps it.</summary>
    internal static void DropOwnerKeyIfEmpty(RegistryKey userRoot) => DeleteKeyIfEmpty(userRoot, OwnerKey);

    /// <summary>0 when the policy key exists; otherwise how many levels Suppress would have to
    /// create: 1 = the System key, 2 = Policies too.</summary>
    private static int MissingPolicyKeys(RegistryKey userRoot)
    {
        using (var system = userRoot.OpenSubKey(PolicyKey))
            if (system != null) return 0;
        using var policies = userRoot.OpenSubKey(PoliciesKey);
        return policies != null ? 1 : 2;
    }

    private static void DeleteCreatedPolicyKeys(RegistryKey userRoot, int created)
    {
        DeleteKeyIfEmpty(userRoot, PolicyKey);
        if (created >= 2) DeleteKeyIfEmpty(userRoot, PoliciesKey);
    }

    private static void DeleteKeyIfEmpty(RegistryKey root, string path)
    {
        try
        {
            using (var key = root.OpenSubKey(path))
                if (key == null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex) { Log.Warn($"registry cleanup of {path}: {ex.Message}"); }
    }
}
