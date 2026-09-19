namespace Pawse.Core;

/// <summary>
/// Orchestrates the OS-level key blocks that toggle with the lock:
/// <see cref="WorkstationLock"/> (Win+L, every edition, no admin) and
/// <see cref="KeyboardFilterGuard"/> (browser/calculator/media keys,
/// Enterprise/Education/IoT + admin). Both are opt-in
/// (<see cref="Config.SystemBlockCfg"/>) and default off.
///
/// <para><see cref="Apply"/> is the single entry point: it drives the full OS state from
/// the desired locked flag, so lock, unlock, the startup crash-sweep, exit, and settings
/// changes all funnel through it. The Win+L registry toggle runs inline (fast); the
/// Keyboard-Filter WMI work is serialized off-thread so a slow round-trip never stalls the
/// caller (and never runs on the hook path).</para>
/// </summary>
public sealed class SystemBlock
{
    private readonly Config _cfg;
    private readonly KeyboardFilterGuard _kf = new();
    private readonly Action<string, string>? _notify;
    private readonly object _kfGate = new();
    private long _generation; // bumped per Apply so stale background work is skipped
    private readonly HashSet<string> _notified = new(); // one tray note per distinct issue

    public SystemBlock(Config cfg, Action<string, string>? notify = null)
    {
        _cfg = cfg;
        _notify = notify;
    }

    private Config.SystemBlockCfg S => _cfg.SystemBlock;

    /// <summary>Both blocks change system state - a registry policy value, machine-wide Keyboard
    /// Filter rules - so they are for installed copies only. A portable copy writes nothing
    /// outside its folder: it never applies them, whatever its pawse.json says, but it still
    /// runs the revert sweep, which only ever removes what an older version left behind.</summary>
    internal static bool Allowed => !Deployment.IsPortable;

    /// <summary>
    /// Reconcile the OS to the desired state. When <paramref name="locked"/> is true the
    /// enabled guards are applied; when false everything Pawse manages is reverted. Pass
    /// <paramref name="background"/> = false on exit so the revert completes before the
    /// process ends; true everywhere else.
    /// </summary>
    public void Apply(bool locked, bool background = true, bool notify = false)
    {
        // Each call supersedes the last: still-queued background KF work bails once it
        // sees a newer generation.
        long gen = Interlocked.Increment(ref _generation);

        // Win+L - registry, fast, inline.
        if (locked && S.WinLock && Allowed)
        {
            if (!WorkstationLock.Suppress() && notify)
                NotifyOnce("winl-admin",
                    "Blocking Win+L was denied by Windows on this PC - run Pawse as administrator " +
                    "(tray menu → Restart as administrator).");
        }
        else if (!WorkstationLock.Restore() && notify)
        {
            // A silent failure here leaves the USER unable to lock their own PC -
            // that must never be just a log line.
            NotifyOnce("winl-restore",
                "Windows denied re-enabling Win+L (it was blocked by an earlier Pawse run). " +
                "Restart Pawse as administrator (tray menu) to restore it - until then Win+L stays off.");
        }

        // Keyboard Filter - WMI, possibly slow, off-thread.
        ApplyFilter(enable: locked && S.LaunchMediaKeys && Allowed, gen, background, notify);
    }

    /// <summary>Enable Pawse's Keyboard Filter rules, or revert them. There is one group of rules
    /// (<see cref="KeyboardFilterGuard.LaunchMediaIds"/>), so this is an on/off switch.</summary>
    private void ApplyFilter(bool enable, long gen, bool background, bool notify)
    {
        void Work()
        {
            lock (_kfGate)
            {
                // A newer Apply has superseded us - don't fight it.
                if (gen != Interlocked.Read(ref _generation)) return;
                bool owed = HasWekfMarker(Microsoft.Win32.Registry.CurrentUser);
                // Pure revert with no marker = Pawse never enabled anything: don't
                // touch rules another tool may have set (and skip the WMI probe).
                if (!enable && !owed) return;
                if (!_kf.IsAvailable())
                {
                    if (notify && enable)
                        NotifyOnce("kf-unavailable", Elevation.IsElevated()
                            ? "Blocking browser / calculator / media keys needs the Windows Keyboard Filter " +
                              "feature (Enterprise, Education or IoT) - it isn't available on this PC."
                            : "Blocking browser / calculator / media keys needs Pawse running as administrator " +
                              "(tray menu → Restart as administrator), on Windows Enterprise, Education or IoT.");
                    return;
                }
                // WEKF rules are persistent machine-wide state, so a crash-while-locked leaves
                // them on and only the marker lets the next start know Pawse owes a revert (a
                // non-elevated restart can't even read WEKF to find out). The marker is written
                // BEFORE enabling and cleared only after a full revert - "full" taken literally:
                // a disable pass where any Put failed keeps the marker, so the revert stays owed
                // and the next sweep retries instead of orphaning the rules machine-wide.
                //
                // Alongside the marker, the rules that were ALREADY on when Pawse first enabled
                // its set: those are IT's or another tool's, and reverting must leave them on -
                // the same ownership rule WorkstationLock applies to the Win+L policy value.
                if (enable)
                {
                    if (!owed)
                    {
                        var already = _kf.ReadEnabled(KeyboardFilterGuard.LaunchMediaIds);
                        if (already is null)
                        {
                            Log.Warn("keyboard-filter: could not read the current rules, so not enabling any");
                            return;
                        }
                        SetWekfMarker(Microsoft.Win32.Registry.CurrentUser, already);
                    }
                    _kf.Set(KeyboardFilterGuard.LaunchMediaIds, enabled: true);
                }
                else
                {
                    RevertOwnRules(Microsoft.Win32.Registry.CurrentUser, _kf);
                }
            }
        }

        if (background) Task.Run(Work);
        else Work();
    }

    // Same key WorkstationLock uses for its ownership marker (HKCU\Software\Pawse).
    private const string OwnerKey = WorkstationLock.OwnerKey;
    private const string WekfMarkerName = "WekfLeftOn";
    private const string WekfPreEnabledName = "WekfPreEnabled";

    /// <summary>What <see cref="RevertOwedFilter"/> managed.</summary>
    internal enum FilterRevert { NothingOwed, Reverted, NeedsAdmin, Failed }

    /// <summary>
    /// The uninstall cleanup's half of the sweep: if the marker in <paramref name="userRoot"/>
    /// says Pawse left its Keyboard Filter rules on, turn them off (keeping the ones that were
    /// on before Pawse) and clear the marker. The rules are machine-wide and need
    /// administrator rights; without them this answers <see cref="FilterRevert.NeedsAdmin"/>
    /// and leaves the marker, so an elevated run can still finish the job.
    /// </summary>
    internal static FilterRevert RevertOwedFilter(Microsoft.Win32.RegistryKey userRoot, KeyboardFilterGuard kf)
    {
        if (!HasWekfMarker(userRoot)) return FilterRevert.NothingOwed;
        if (!Elevation.IsElevated()) return FilterRevert.NeedsAdmin;
        if (!kf.IsAvailable())
        {
            // Elevated and still no Keyboard Filter: the feature is gone from this PC, and with
            // it every rule - nothing left to revert, so the marker is all that remains.
            ClearWekfMarker(userRoot);
            return FilterRevert.Reverted;
        }
        return RevertOwnRules(userRoot, kf) ? FilterRevert.Reverted : FilterRevert.Failed;
    }

    /// <summary>Disable Pawse's rules except those that were on before it, and clear the marker
    /// once every one of them went through.</summary>
    private static bool RevertOwnRules(Microsoft.Win32.RegistryKey userRoot, KeyboardFilterGuard kf)
    {
        var keep = new HashSet<string>(ReadWekfPreEnabled(userRoot), StringComparer.OrdinalIgnoreCase);
        var toDisable = KeyboardFilterGuard.LaunchMediaIds.Where(id => !keep.Contains(id)).ToArray();
        if (keep.Count > 0)
            Log.Info($"keyboard-filter: leaving on what was on before Pawse: [{string.Join(", ", keep)}]");
        if (!kf.Set(toDisable, enabled: false)) return false;
        ClearWekfMarker(userRoot);
        return true;
    }

    /// <summary>Record that Pawse owes a revert, and which managed rules were already on.</summary>
    private static void SetWekfMarker(Microsoft.Win32.RegistryKey userRoot, IReadOnlyCollection<string> preEnabled)
    {
        try
        {
            using var key = userRoot.CreateSubKey(OwnerKey);
            key?.SetValue(WekfPreEnabledName, preEnabled.ToArray(), Microsoft.Win32.RegistryValueKind.MultiString);
            key?.SetValue(WekfMarkerName, 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex) { Log.Warn("wekf marker: " + ex.Message); }
    }

    /// <summary>Clear the marker - opening the key, never creating it - and drop
    /// <c>Software\Pawse</c> when nothing else is left in it.</summary>
    private static void ClearWekfMarker(Microsoft.Win32.RegistryKey userRoot)
    {
        try
        {
            using (var key = userRoot.OpenSubKey(OwnerKey, writable: true))
            {
                key?.DeleteValue(WekfMarkerName, throwOnMissingValue: false);
                key?.DeleteValue(WekfPreEnabledName, throwOnMissingValue: false);
            }
            WorkstationLock.DropOwnerKeyIfEmpty(userRoot);
        }
        catch (Exception ex) { Log.Warn("wekf marker: " + ex.Message); }
    }

    /// <summary>The rules that were on before Pawse's. Empty for a marker an older build wrote,
    /// which then reverts everything - the only honest reading of a marker with no snapshot.</summary>
    private static string[] ReadWekfPreEnabled(Microsoft.Win32.RegistryKey userRoot)
    {
        try
        {
            using var key = userRoot.OpenSubKey(OwnerKey);
            return key?.GetValue(WekfPreEnabledName) as string[] ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    private static bool HasWekfMarker(Microsoft.Win32.RegistryKey userRoot)
    {
        try
        {
            using var key = userRoot.OpenSubKey(OwnerKey);
            return key?.GetValue(WekfMarkerName) is int i && i == 1;
        }
        catch { return false; }
    }

    /// <summary>
    /// Call once at startup, after the sweep. An elevated run that died while locked
    /// leaves the Keyboard Filter rules enabled machine-wide; a non-elevated restart's
    /// sweep silently can't touch them (<see cref="KeyboardFilterGuard.IsAvailable"/> is
    /// false without admin), so media/volume keys would stay dead with nothing pointing
    /// at Pawse. If the marker says we owe a revert and we can't perform it, say so.
    /// (When elevated, the sweep itself reverts and clears the marker.)
    /// </summary>
    public void WarnIfUnsweepableLeftovers()
    {
        if (!HasWekfMarker(Microsoft.Win32.Registry.CurrentUser) || Elevation.IsElevated()) return;
        NotifyOnce("kf-leftover",
            "A previous Pawse run (as administrator) left browser/media keys blocked and " +
            "this run can't undo that without admin. Restart Pawse as administrator " +
            "(tray menu) to restore them.");
    }

    private void NotifyOnce(string key, string message)
    {
        lock (_notified) { if (!_notified.Add(key)) return; }
        Log.Warn("notify: " + message);
        _notify?.Invoke("Pawse", message);
    }
}
