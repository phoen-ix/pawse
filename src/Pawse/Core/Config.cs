using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pawse.Core;

/// <summary>
/// User configuration, persisted as <c>pawse.json</c> next to the exe. Every
/// unlock method has its own <c>Enabled</c> flag; the popup is optional; mouse
/// blocking is off by default. Deliberately nothing in here can put Pawse on the
/// network: the only request it ever makes is the update check the user starts by
/// hand from the tray (<see cref="UpdateCheck"/>), which no setting can automate.
/// </summary>
public sealed class Config
{
    public GeneralCfg General { get; set; } = new();
    public ChordCfg LockHotkey { get; set; } = new() { Enabled = true, Keys = new() { "Ctrl", "L" } };
    public UnlockCfg Unlock { get; set; } = new();
    public OverlayCfg Overlay { get; set; } = new();
    public SystemBlockCfg SystemBlock { get; set; } = new();
    public UpdateCfg Update { get; set; } = new();

    public sealed class GeneralCfg
    {
        public bool StartLocked { get; set; }
        public bool Autostart { get; set; }
        public bool BlockMouse { get; set; }

        /// <summary>Off by default: an on-screen / touch keyboard keeps working while the
        /// hardware keyboard is locked. The hook can only tell that a keystroke was
        /// simulated, not which program simulated it, so this covers all injected input.</summary>
        public bool BlockScreenKeyboard { get; set; }

        /// <summary>While locked, the tray paw unlocks only on a double-click (a lone
        /// click shows a hint), so a stray paw-click can't undo the lock. Off =
        /// classic single-click toggle. Locking is always a single click.</summary>
        public bool TrayDoubleClickUnlock { get; set; } = true;
    }

    /// <summary>
    /// The update check, which is the only thing in Pawse that can reach the network.
    /// Off by default: nothing happens until you press "Check now" in Settings. Turning
    /// <see cref="AutoCheck"/> on adds a once-a-day check while Pawse runs - it only ever
    /// tells you an update exists; installing still takes a deliberate yes.
    /// </summary>
    public sealed class UpdateCfg
    {
        public bool AutoCheck { get; set; }

        /// <summary>UTC of the last completed check, so the daily check doesn't fire again
        /// on every restart. Written by the app; there is nothing to hand-edit here.</summary>
        public DateTime? LastCheckUtc { get; set; }
    }

    public sealed class UnlockCfg
    {
        public ChordCfg Chord { get; set; } = new() { Enabled = true, Keys = new() { "Ctrl", "L" } };
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
        /// <summary>One floor for every consumer - the overlay's clamp and the settings
        /// slider - so a hand-edited value can't silently disagree between them.</summary>
        public const double MinOpacity = 0.3;

        public bool Enabled { get; set; } = true;
        public double Opacity { get; set; } = 0.92;
        public int Monitor { get; set; }
        public int VerticalPercent { get; set; } = 50;
    }

    /// <summary>
    /// OS-level key suppression applied only while locked and reverted on unlock. Default off.
    /// <c>WinLock</c> disables Win+L via the per-user DisableLockWorkstation policy (managed
    /// PCs need Pawse elevated). <c>LaunchMediaKeys</c> blocks browser/calculator/media keys
    /// via the Windows Keyboard Filter (Enterprise/Education/IoT + admin only).
    /// </summary>
    public sealed class SystemBlockCfg
    {
        public bool WinLock { get; set; }         // DisableLockWorkstation toggle
        public bool LaunchMediaKeys { get; set; } // browser/calc/media via Keyboard Filter
    }

    // ---- persistence ---------------------------------------------------------

    // Reading is lenient because pawse.json is hand-editable (tray → "Open config file"):
    // a trailing comma, a // comment or unusual key casing must not cost the user their settings.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    public static string PathOnDisk() => Log.ResolvePath("pawse.json");

    public static Config Load()
    {
        var path = PathOnDisk();
        if (File.Exists(path))
        {
            try
            {
                var cfg = FromJson(File.ReadAllText(path));
                if (cfg != null)
                {
                    Log.Info($"config loaded from {path}");
                    return cfg;
                }
                Log.Error($"config load failed ({path}): no object in file; using defaults");
            }
            catch (Exception ex)
            {
                Log.Error($"config load failed ({path}); using defaults", ex);
            }
            // Keep the unreadable file - a hand-edit typo must not silently wipe the
            // user's settings when the defaults below are saved over the live path.
            try
            {
                File.Copy(path, path + ".bad", overwrite: true);
                Log.Warn($"kept the unreadable config as {path}.bad");
            }
            catch { /* best effort */ }
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
            // Write-to-temp + move so a crash or full disk mid-write can't truncate the
            // live file (a truncated pawse.json would load as defaults on the next start).
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson());
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"config save failed ({path})", ex);
        }
    }

    /// <summary>Deserialize + normalize. Null only if the JSON is the literal <c>null</c>.</summary>
    internal static Config? FromJson(string json)
    {
        var cfg = JsonSerializer.Deserialize<Config>(json, JsonOpts);
        cfg?.NormalizeAfterLoad();
        return cfg;
    }

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>
    /// System.Text.Json assigns a JSON <c>null</c> into non-nullable properties, so a
    /// hand-edited <c>"Unlock": null</c> would NRE later (worst case inside
    /// <see cref="HasUsableUnlock"/> during startup). Reseed any nulled section with
    /// its defaults instead.
    /// </summary>
    private void NormalizeAfterLoad()
    {
        General ??= new();
        LockHotkey ??= new() { Enabled = true, Keys = new() { "Ctrl", "L" } };
        LockHotkey.Keys ??= new();
        Unlock ??= new();
        Unlock.Chord ??= new() { Enabled = true, Keys = new() { "Ctrl", "L" } };
        Unlock.Chord.Keys ??= new();
        Unlock.Passphrase ??= new();
        Unlock.Passphrase.Text ??= "";
        Unlock.MouseHold ??= new();
        Unlock.Timer ??= new();
        Overlay ??= new();
        SystemBlock ??= new();
        Update ??= new();
    }

    /// <summary>
    /// True if at least one unlock method is genuinely usable given the whole config, so
    /// locking can't strand the user. Mirrors what actually works while locked: a chord must
    /// parse to >=1 key; a passphrase must be fully typeable (only a-z/0-9/space register
    /// through the hook); mouse-hold needs the overlay shown AND the mouse not blocked (else
    /// the hold button can't be clicked); the timer needs a positive delay.
    /// </summary>
    public bool HasUsableUnlock()
    {
        if (Unlock.Chord.Enabled && Keys.ParseChord(Unlock.Chord.Keys).Count > 0) return true;
        if (Unlock.Passphrase.Enabled && Keys.IsTypeablePassphrase(Unlock.Passphrase.Text)) return true;
        if (Unlock.MouseHold.Enabled && Overlay.Enabled && !General.BlockMouse) return true;
        if (Unlock.Timer.Enabled && Unlock.Timer.Seconds > 0) return true;
        return false;
    }

    /// <summary>
    /// Lockout guard shared by startup and Settings-save: when no unlock method is
    /// genuinely usable, enable the chord - reseeding it to Ctrl+L if it doesn't parse -
    /// so a lock can always be undone. Returns true if anything was changed;
    /// <paramref name="reseeded"/> reports whether the keys had to be replaced.
    /// </summary>
    public bool EnsureUsableUnlockFallback(out bool reseeded)
    {
        reseeded = false;
        if (HasUsableUnlock()) return false;
        Unlock.Chord.Enabled = true;
        if (Keys.ParseChord(Unlock.Chord.Keys).Count == 0)
        {
            Unlock.Chord.Keys = new() { "Ctrl", "L" };
            reseeded = true;
        }
        return true;
    }

    public string Summary() =>
        $"gui=tray lock_on_start={General.StartLocked} block_mouse={General.BlockMouse} " +
        $"block_screen_keyboard={General.BlockScreenKeyboard} " +
        $"overlay.enabled={Overlay.Enabled} unlock=[chord={Unlock.Chord.Enabled} " +
        $"passphrase={Unlock.Passphrase.Enabled} mouse_hold={Unlock.MouseHold.Enabled} " +
        $"timer={Unlock.Timer.Enabled}] lock_hotkey={LockHotkey.Enabled} " +
        $"auto_update_check={Update.AutoCheck} " +
        $"sysblock=[win_l={SystemBlock.WinLock} launch_media={SystemBlock.LaunchMediaKeys}]";
}
