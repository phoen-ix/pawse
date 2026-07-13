namespace Pawse.Core;

/// <summary>
/// Toggles the OS-level Win+L block (<see cref="WorkstationLock"/>) with the lock.
/// Opt-in via <see cref="Config.SystemBlockCfg"/>, default off.
///
/// <para><see cref="Apply"/> is the single entry point: it drives the OS state from
/// the desired locked flag, so lock, unlock, the startup crash-sweep, exit, and
/// settings changes all funnel through it. The registry toggle runs inline (fast).</para>
/// </summary>
public sealed class SystemBlock
{
    private readonly Config _cfg;
    private readonly Action<string, string>? _notify;
    private readonly HashSet<string> _notified = new(); // one tray note per distinct issue

    public SystemBlock(Config cfg, Action<string, string>? notify = null)
    {
        _cfg = cfg;
        _notify = notify;
    }

    private Config.SystemBlockCfg S => _cfg.SystemBlock;

    /// <summary>
    /// Reconcile the OS to the desired state. When <paramref name="locked"/> is true and
    /// Win+L blocking is enabled it is applied; otherwise it is reverted. <paramref name="background"/>
    /// is accepted for call-site compatibility (the Win+L registry write is fast and always inline).
    /// </summary>
    public void Apply(bool locked, bool background = true, bool notify = false)
    {
        if (locked && S.WinLock)
        {
            if (!WorkstationLock.Suppress() && notify)
                NotifyOnce("winl-admin",
                    "Blocking Win+L was denied by Windows on this PC - run Pawse as administrator " +
                    "(tray menu → Restart as administrator).");
        }
        else WorkstationLock.Restore();
    }

    private void NotifyOnce(string key, string message)
    {
        lock (_notified) { if (!_notified.Add(key)) return; }
        Log.Warn("notify: " + message);
        _notify?.Invoke("Pawse", message);
    }
}
