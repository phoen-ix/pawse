using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pawse.Core;

/// <summary>
/// User configuration, persisted as <c>pawse.json</c> next to the exe. Every
/// unlock method has its own <c>Enabled</c> flag; the popup is optional; mouse
/// blocking is off by default. Nothing here touches the network - the app is
/// fully local (no phone-home, manual updates).
/// </summary>
public sealed class Config
{
    public GeneralCfg General { get; set; } = new();
    public ChordCfg LockHotkey { get; set; } = new() { Enabled = true, Keys = new() { "Ctrl", "Alt", "L" } };
    public UnlockCfg Unlock { get; set; } = new();
    public OverlayCfg Overlay { get; set; } = new();
    public SystemBlockCfg SystemBlock { get; set; } = new();

    public sealed class GeneralCfg
    {
        public bool StartLocked { get; set; }
        public bool Autostart { get; set; }
        public bool BlockMouse { get; set; }
    }

    public sealed class UnlockCfg
    {
        public ChordCfg Chord { get; set; } = new() { Enabled = true, Keys = new() { "Ctrl", "Shift", "U" } };
        public PassphraseCfg Passphrase { get; set; } = new();
        public MouseHoldCfg MouseHold { get; set; } = new();
        public TimerCfg Timer { get; set; } = new();
    }

    public sealed class ChordCfg
    {
        public bool Enabled { get; set; }
        public List<string> Keys { get; set; } = new();
    }

    public sealed class PassphraseCfg
    {
        public bool Enabled { get; set; }
        public string Text { get; set; } = "unlock";
        public bool ResetOnWrongKey { get; set; } = true;
    }

    public sealed class MouseHoldCfg
    {
        public bool Enabled { get; set; } = true;
        public int HoldMs { get; set; } = 1200;
    }

    public sealed class TimerCfg
    {
        public bool Enabled { get; set; }
        public int Seconds { get; set; } = 300;
    }

    public sealed class OverlayCfg
    {
        public bool Enabled { get; set; } = true;
        public double Opacity { get; set; } = 0.92;
        public int Monitor { get; set; }
        public int VerticalPercent { get; set; } = 50;
    }

    /// <summary>
    /// OS-level key suppression applied only while locked and reverted on unlock.
    /// Default off. <c>WinLock</c> disables Win+L via the per-user DisableLockWorkstation
    /// policy; on managed/corporate PCs the policy key is ACL-locked and needs Pawse elevated.
    /// </summary>
    public sealed class SystemBlockCfg
    {
        public bool WinLock { get; set; } // DisableLockWorkstation toggle
    }

    // ---- persistence ---------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string PathOnDisk() => Log.ResolvePath("pawse.json");

    public static Config Load()
    {
        var path = PathOnDisk();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<Config>(json, JsonOpts);
                if (cfg != null)
                {
                    Log.Info($"config loaded from {path}");
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"config load failed ({path}); using defaults", ex);
        }

        var fresh = new Config();
        fresh.Save();
        Log.Info($"config: wrote defaults to {path}");
        return fresh;
    }

    public void Save()
    {
        var path = PathOnDisk();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Error($"config save failed ({path})", ex);
        }
    }

    public string Summary() =>
        $"gui=tray lock_on_start={General.StartLocked} block_mouse={General.BlockMouse} " +
        $"overlay.enabled={Overlay.Enabled} unlock=[chord={Unlock.Chord.Enabled} " +
        $"passphrase={Unlock.Passphrase.Enabled} mouse_hold={Unlock.MouseHold.Enabled} " +
        $"timer={Unlock.Timer.Enabled}] lock_hotkey={LockHotkey.Enabled} " +
        $"sysblock=[win_l={SystemBlock.WinLock}]";
}
