using System.Runtime.InteropServices;

namespace Pawse.Core;

/// <summary>
/// Global low-level keyboard hook. MUST be installed from a thread that pumps
/// messages (we install it on the WPF UI thread, whose dispatcher is the pump),
/// so the callback runs on the UI thread and may touch the tray/overlay directly.
/// If the process dies the OS removes the hook automatically → fail-open.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly LockController _controller;
    private readonly NativeMethods.HookProc _proc; // keep a ref so it isn't GC'd
    private IntPtr _hook;

    public KeyboardHook(LockController controller)
    {
        _controller = controller;
        _proc = Proc;
    }

    public bool Install()
    {
        IntPtr hMod = NativeMethods.GetModuleHandleW(null);
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Error($"keyboard hook install FAILED (err={Marshal.GetLastWin32Error()})");
            return false;
        }
        Log.Info("keyboard hook installed");
        return true;
    }

    private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool isDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            bool isUp = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
            if (isDown || isUp)
            {
                try
                {
                    var s = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    bool ours = s.dwExtraInfo == NativeMethods.PAWSE_MAGIC;
                    if (_controller.OnKeyboard((int)s.vkCode, isDown, ours))
                        return 1;
                }
                catch (Exception ex)
                {
                    // Never let an exception escape the callback - that kills the hook.
                    Log.Error("keyboard hook proc", ex);
                }
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
            Log.Info("keyboard hook removed");
        }
    }
}
