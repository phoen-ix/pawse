namespace Pawse.Core;

/// <summary>
/// A cross-process "please quit" channel, so the installer and uninstaller can ask a
/// running Pawse to exit <em>cleanly</em> instead of force-killing it.
///
/// <para>Why it has to exist: Pawse is a tray-only WPF app with
/// <c>ShutdownMode="OnExplicitShutdown"</c> and no titled window, so there is nothing for
/// an outsider to send <c>WM_CLOSE</c> to - plain <c>taskkill</c> does nothing and
/// <c>taskkill /F</c> skips <c>App.OnExit</c>, the only code that reverts the Win+L policy
/// value (<see cref="WorkstationLock"/>) and the Keyboard Filter rules
/// (<see cref="KeyboardFilterGuard"/>). Killing Pawse can therefore leave Win+L blocked on
/// a machine that no longer has Pawse on it; asking it to quit cannot.</para>
///
/// <para>Mechanism: a named auto-reset event in the same per-session <c>Local\</c>
/// namespace as the single-instance mutex. It carries a default DACL, so only processes
/// running as the same user in the same session can signal it - precisely the processes
/// that could already <c>taskkill</c> us, so this hands nobody any new power.</para>
/// </summary>
public static class QuitSignal
{
    /// <summary>Also hard-coded in packaging/pawse.nsi - change both or neither.</summary>
    internal const string EventName = @"Local\Pawse-quit-2b8f9c";

    /// <summary>
    /// Start listening for an external quit request. <paramref name="onQuit"/> is invoked
    /// on a thread-pool thread, at most once, and only for a real signal. Dispose the
    /// returned handle to stop listening. Returns null if the channel could not be opened,
    /// which is not fatal: the app runs exactly as before and the installer falls back to
    /// asking the user whether to force the close.
    /// </summary>
    public static IDisposable? Listen(Action onQuit)
    {
        try
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            // Registering on an already-signalled event fires straight away, so a request
            // that arrives before we get here is still delivered rather than lost.
            var registration = ThreadPool.RegisterWaitForSingleObject(
                handle,
                // Guarded because this runs on a thread-pool thread, where an escaping
                // exception takes the whole process down - and the obvious one is real:
                // a request landing just as the user quits from the tray hits a dispatcher
                // that has already shut down. Losing the request beats crashing on it.
                (_, timedOut) =>
                {
                    if (timedOut) return;
                    try { onQuit(); }
                    catch (Exception ex) { Log.Error("quit channel callback", ex); }
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: true);
            Log.Info("quit channel listening");
            return new Registration(registration, handle);
        }
        catch (Exception ex)
        {
            Log.Error("quit channel listen", ex);
            return null;
        }
    }

    /// <summary>
    /// Ask a running Pawse to quit. False means nobody is listening - no instance running,
    /// or one built before this channel existed.
    /// </summary>
    public static bool Signal()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventName, out var handle)) return false;
            using (handle) return handle.Set();
        }
        catch (Exception ex)
        {
            Log.Error("quit channel signal", ex);
            return false;
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly RegisteredWaitHandle _registration;
        private readonly EventWaitHandle _handle;

        internal Registration(RegisteredWaitHandle registration, EventWaitHandle handle)
        {
            _registration = registration;
            _handle = handle;
        }

        public void Dispose()
        {
            // Unregister(null) does NOT wait for a callback that is already running - and
            // it must not: the usual caller is App.OnExit, which is reached *from* that
            // callback, so the waiting overload would deadlock against itself.
            try { _registration.Unregister(null); } catch { /* ignore */ }
            try { _handle.Dispose(); } catch { /* ignore */ }
        }
    }
}
