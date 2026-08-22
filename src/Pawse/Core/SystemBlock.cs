using System.Linq;

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
    private static readonly string[] AllManagedIds = KeyboardFilterGuard.LaunchMediaIds;

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
        long gen = System.Threading.Interlocked.Increment(ref _generation);

        // Win+L - registry, fast, inline.
        if (locked && S.WinLock)
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
        var enable = locked ? EnabledFilterIds() : Array.Empty<string>();
        ApplyFilter(enable, gen, background, notify);
    }

    private string[] EnabledFilterIds() =>
        S.LaunchMediaKeys ? KeyboardFilterGuard.LaunchMediaIds : Array.Empty<string>();

    private void ApplyFilter(string[] enableIds, long gen, bool background, bool notify)
    {
        var enableSet = new HashSet<string>(enableIds, StringComparer.OrdinalIgnoreCase);
        var disableIds = AllManagedIds.Where(id => !enableSet.Contains(id)).ToArray();

        void Work()
        {
            lock (_kfGate)
            {
                // A newer Apply has superseded us - don't fight it.
                if (gen != System.Threading.Interlocked.Read(ref _generation)) return;
                // Pure revert with no marker = Pawse never enabled anything: don't
                // touch rules another tool may have set (and skip the WMI probe).
                if (enableIds.Length == 0 && !HasWekfMarker()) return;
                if (!_kf.IsAvailable())
                {
                    if (notify && enableIds.Length > 0)
                        NotifyOnce("kf-unavailable", Elevation.IsElevated()
                            ? "Blocking browser / calculator / media keys needs the Windows Keyboard Filter " +
                              "feature (Enterprise, Education or IoT) - it isn't available on this PC."
                            : "Blocking browser / calculator / media keys needs Pawse running as administrator " +
                              "(tray menu → Restart as administrator), on Windows Enterprise, Education or IoT.");
                    return;
                }
                // Disable-then-enable, in order, so the final state is exact even if a
                // previous toggle is still settling. The marker is written BEFORE
                // enabling and cleared only after a full revert: WEKF rules are
                // persistent machine-wide state, so a crash-while-locked leaves them
                // on, and only the marker lets the next start know Pawse owes a
                // revert (a non-elevated restart can't even read WEKF to find out).
                // "Full revert" is taken literally: a disable pass where any Put
                // failed keeps the marker, so the revert stays owed and the next
                // sweep retries instead of orphaning the rules machine-wide.
                if (enableIds.Length > 0) SetWekfMarker(true);
                bool reverted = _kf.Set(disableIds, enabled: false);
                _kf.Set(enableIds, enabled: true);
                if (enableIds.Length == 0 && reverted) SetWekfMarker(false);
            }
        }

        if (background) System.Threading.Tasks.Task.Run(Work);
        else Work();
    }

    // Same HKCU key WorkstationLock uses for its ownership marker.
    private const string OwnerKey = @"Software\Pawse";
    private const string WekfMarkerName = "WekfLeftOn";

    private static void SetWekfMarker(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(OwnerKey);
            if (on) key?.SetValue(WekfMarkerName, 1, Microsoft.Win32.RegistryValueKind.DWord);
            else key?.DeleteValue(WekfMarkerName, throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Warn("wekf marker: " + ex.Message); }
    }

    private static bool HasWekfMarker()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(OwnerKey);
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
        if (!HasWekfMarker() || Elevation.IsElevated()) return;
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
