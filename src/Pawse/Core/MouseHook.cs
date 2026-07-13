using System.Runtime.InteropServices;

namespace Pawse.Core;

/// <summary>
/// Optional global low-level mouse hook. Off by default. When enabled and locked
/// it swallows all mouse input. Note: with mouse-blocking ON, the overlay's
/// hold-to-unlock button cannot receive clicks, so use a keyboard unlock method.
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
        IntPtr hMod = NativeMethods.GetModuleHandleW(null);
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Error($"mouse hook install FAILED (err={Marshal.GetLastWin32Error()})");
            return false;
        }
        Log.Info("mouse hook installed");
        return true;
    }

    private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _controller.IsLocked && _controller.Config.General.BlockMouse)
        {
            try
            {
                var s = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (s.dwExtraInfo != NativeMethods.PAWSE_MAGIC)
                    return 1;
            }
            catch (Exception ex)
            {
                Log.Error("mouse hook proc", ex);
            }
        }
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
