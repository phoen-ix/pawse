using System.Runtime.InteropServices;

namespace Pawse.Core;

/// <summary>
/// Optional global low-level mouse hook - only INSTALLED while
/// <see cref="Config.GeneralCfg.BlockMouse"/> is on (see <see cref="HookThread.SyncMouse"/>);
/// an installed WH_MOUSE_LL routes every mouse event system-wide through the hook
/// thread, a tax not paid for an off-by-default feature. When installed and locked
/// it swallows all mouse input. Note: with mouse-blocking ON, the overlay's
/// hold-to-unlock button cannot receive clicks, so use a keyboard unlock method.
/// Blocks only the legacy mouse stream: native touch/pen input reaches
/// pointer-aware apps via WM_POINTER, which no LL mouse hook sees - a documented
/// limitation (README, "What it can and can't block").
/// </summary>
public sealed class MouseHook : IDisposable
{
    private readonly LockController _controller;
    private readonly NativeMethods.HookProc _proc;
    private IntPtr _hook;

    public MouseHook(LockController controller)
    {
        _controller = controller;
        _proc = Proc;
    }

    public bool Install()
    {
        _hook = Hook();
        if (_hook == IntPtr.Zero)
        {
            Log.Error($"mouse hook install FAILED (err={Marshal.GetLastWin32Error()})");
            return false;
        }
        Log.Info("mouse hook installed");
        return true;
    }

    /// <summary>See <see cref="KeyboardHook.Reinstall"/>: new hook first, old handle second.</summary>
    public bool Reinstall()
    {
        IntPtr fresh = Hook();
        if (fresh == IntPtr.Zero) return false;   // keep whatever we had
        IntPtr old = _hook;
        _hook = fresh;
        if (old != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(old);
        return true;
    }

    private IntPtr Hook() =>
        NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);

    private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // No PAWSE_MAGIC exception here: Pawse never injects mouse input, so honoring
        // the (public, constant) tag would only hand any local process a click-through
        // hole in the lock. While locked with BlockMouse, everything is swallowed.
        if (nCode >= 0 && _controller.IsLocked && _controller.Config.General.BlockMouse)
            return 1;
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Log.Info("mouse hook removed");
        }
    }
}
