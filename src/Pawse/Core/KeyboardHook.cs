using System.Runtime.InteropServices;

namespace Pawse.Core;

/// <summary>
/// Global low-level keyboard hook. MUST be installed from a thread that pumps
/// messages - the dedicated <see cref="HookThread"/>, so callbacks are serviced
/// even while the UI thread is busy (a delayed callback passes the key through
/// unswallowed and eventually gets the hook removed by the OS). The callback
/// therefore runs OFF the UI thread and must not touch tray/overlay directly.
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

    public bool Install(bool quiet = false)
    {
        IntPtr hMod = NativeMethods.GetModuleHandleW(null);
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero)
        {
            if (!quiet) Log.Error($"keyboard hook install FAILED (err={Marshal.GetLastWin32Error()})");
            return false;
        }
        if (!quiet) Log.Info("keyboard hook installed");
        return true;
    }

    /// <summary>Unhook + hook again, from the owning thread. The OS removes LL hooks
    /// it deems slow without telling anyone; this makes removal self-healing.</summary>
    public bool Reinstall()
    {
        if (_hook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        return Install(quiet: true);
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
                    // Field reads instead of PtrToStructure: that call marshals (and
                    // allocates) the whole struct on every keystroke system-wide, and
                    // only three fields are needed. KBDLLHOOKSTRUCT starts with four
                    // uints, so vkCode/flags/dwExtraInfo sit at 0/8/16 on x86 and x64
                    // alike (dwExtraInfo is pointer-aligned after 16 bytes either way).
                    int vk = Marshal.ReadInt32(lParam);
                    uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                    nuint extra = (nuint)(nint)Marshal.ReadIntPtr(lParam, 16);
                    bool ours = extra == NativeMethods.PAWSE_MAGIC;
                    bool injected = (flags & NativeMethods.LLKHF_INJECTED) != 0;
                    if (_controller.OnKeyboard(vk, isDown, ours, injected))
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
