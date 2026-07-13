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
        else WorkstationLock.Restore();

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
                // previous toggle is still settling.
                _kf.Set(disableIds, enabled: false);
                _kf.Set(enableIds, enabled: true);
            }
        }

        if (background) System.Threading.Tasks.Task.Run(Work);
        else Work();
    }

    private void NotifyOnce(string key, string message)
    {
        lock (_notified) { if (!_notified.Add(key)) return; }
        Log.Warn("notify: " + message);
        _notify?.Invoke("Pawse", message);
    }
}
