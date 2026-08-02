using System.Runtime.InteropServices;

namespace Pawse.Core;

/// <summary>Synthesizes input. Only used to release modifier keys.</summary>
public static class Input
{
    // Release BOTH sides of every modifier. Injecting a key-up for a key that
    // isn't down is harmless, and this is what clears a "stuck Ctrl" after the
    // lock swallowed its key-up (the original zoom-on-scroll bug).
    private static readonly ushort[] Modifiers =
    {
        (ushort)Keys.VK_LCONTROL, (ushort)Keys.VK_RCONTROL,
        (ushort)Keys.VK_LSHIFT,  (ushort)Keys.VK_RSHIFT,
        (ushort)Keys.VK_LMENU,   (ushort)Keys.VK_RMENU,
        (ushort)Keys.VK_LWIN,    (ushort)Keys.VK_RWIN,
    };

    private static readonly HashSet<int> ModifierVks = new();
    static Input() { foreach (var m in Modifiers) ModifierVks.Add(m); }

    /// <summary>True for the exact (side-specific) VKs <see cref="ClearModifiers"/>
    /// injects - the only events the hook's PAWSE_MAGIC pass-through may honor.</summary>
    public static bool IsClearedModifier(int vk) => ModifierVks.Contains(vk);

    /// <summary>Inject key-up for all modifiers, tagged so our own hook ignores them.</summary>
    public static void ClearModifiers()
    {
        var inputs = new NativeMethods.INPUT[Modifiers.Length];
        for (int i = 0; i < Modifiers.Length; i++)
        {
            inputs[i] = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = Modifiers[i],
                        wScan = 0,
                        dwFlags = NativeMethods.KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = NativeMethods.PAWSE_MAGIC,
                    },
                },
            };
        }

        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
            Log.Warn($"ClearModifiers: SendInput sent {sent}/{inputs.Length} (err={Marshal.GetLastWin32Error()})");
    }
}
