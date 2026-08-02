using System.Threading;

namespace Pawse.Core;

/// <summary>
/// Owns both low-level hooks on a dedicated message-pumping thread.
///
/// <para>Installed on the WPF UI thread, hook callbacks wait for whatever the
/// dispatcher is doing - the overlay's first Show(), a config save, a settings
/// window - and any wait past LowLevelHooksTimeout makes Windows deliver the
/// keystroke to the foreground app UNSWALLOWED and, after a few offenses,
/// silently remove the hook: the lock keeps saying "locked" while blocking
/// nothing. A thread that does nothing but pump for the hooks removes that
/// whole failure class; per-event work stays microseconds.</para>
///
/// <para>Hook removal by the OS is silent and there is no API to query it, so the
/// thread also re-registers both hooks every few seconds (WM_TIMER) - the
/// standard self-heal idiom. Re-registering an alive hook is cheap; the
/// unhook→rehook gap is microseconds.</para>
/// </summary>
public sealed class HookThread
{
    private const uint RehookMs = 5000;

    private readonly LockController _controller;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private KeyboardHook? _kb;
    private MouseHook? _mouse;
    private bool _installOk;
    private bool _rehookFailureLogged;

    public HookThread(LockController controller) => _controller = controller;

    /// <summary>Start the thread and install both hooks on it. Returns the
    /// keyboard hook's install result - without it locking must stay disabled.</summary>
    public bool Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Pawse hooks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));
        return _installOk;
    }

    /// <summary>Unhook and stop the thread. Safe to call once at shutdown.</summary>
    public void Stop()
    {
        if (_thread == null) return;
        NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
            Log.Warn("hook thread did not stop in time (exiting anyway - the OS removes hooks with the process)");
        _thread = null;
    }

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _kb = new KeyboardHook(_controller);
        _installOk = _kb.Install();
        if (_installOk)
        {
            _mouse = new MouseHook(_controller);
            _mouse.Install();
            NativeMethods.SetTimer(IntPtr.Zero, 0, RehookMs, IntPtr.Zero);
        }
        _ready.Set();
        if (!_installOk) return;

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == NativeMethods.WM_TIMER) Rehook();
            else NativeMethods.DispatchMessageW(ref msg);
        }

        _mouse?.Dispose();
        _kb.Dispose();
    }

    private void Rehook()
    {
        // Silent on success - this runs every few seconds for the process lifetime.
        bool ok = _kb!.Reinstall() & (_mouse?.Reinstall() ?? true);
        if (!ok)
        {
            if (!_rehookFailureLogged)
            {
                _rehookFailureLogged = true;
                Log.Error("periodic hook re-registration FAILED - will keep retrying");
            }
        }
        else if (_rehookFailureLogged)
        {
            _rehookFailureLogged = false;
            Log.Info("periodic hook re-registration recovered");
        }
    }
}
