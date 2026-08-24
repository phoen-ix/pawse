using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pawse.Core;

/// <summary>
/// User configuration, persisted as <c>pawse.json</c> next to the exe. Every
/// unlock method has its own <c>Enabled</c> flag; the popup is optional; mouse
/// blocking is off by default. The only thing here that can put Pawse on the
/// network is the update check (<see cref="UpdateCheck"/>), and
/// <see cref="UpdateCfg.Mode"/> decides how far it may go on its own - the default
/// is "not at all until you press Check now".
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

        /// <summary>Write pawse.log next to the exe. Off by default and opt-in on purpose:
        /// Pawse sees every keystroke, so a plaintext file recording what it did - including,
        /// while chasing a stuck-key problem, which keys were held when a lock engaged - is
        /// not something to leave switched on by default in an app whose whole claim is that
        /// nothing leaves the machine.</summary>
        public bool Logging { get; set; }
    }

    /// <summary>How far Pawse may go about updates on its own.</summary>
    public enum UpdateMode
    {
        /// <summary>Only when I ask. The default, and the only level at which nothing
        /// whatsoever leaves the machine unasked.</summary>
        Manual = 0,

        /// <summary>A once-a-day check that does no more than tell you.</summary>
        Notify = 1,

        /// <summary>Download, verify and install it. Still refuses anything that would need
        /// a UAC prompt, a runtime download, or a guess about which build is installed.</summary>
        Automatic = 2,
    }

    /// <summary>
    /// The update check, which is the only thing in Pawse that can reach the network.
    /// <see cref="Mode"/> is <see cref="UpdateMode.Manual"/> by default: nothing happens
    /// until you press "Check now" in Settings → About.
    /// </summary>
    public sealed class UpdateCfg
    {
        /// <summary>Read only to migrate configs written before <see cref="Mode"/> existed,
        /// where true meant the daily notify-only check. Never written back - the JsonIgnore
        /// overrides the file-wide <c>DefaultIgnoreCondition.Never</c> - so the key drops out
        /// of pawse.json the first time this build saves.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? AutoCheck { get; set; }

        /// <summary>"Manual" | "Notify" | "Automatic" - see <see cref="UpdateMode"/>. Stored
        /// as text so a hand-edited pawse.json reads like English, and typed as string rather
        /// than the enum on purpose: a JsonStringEnumConverter property THROWS on an
        /// unrecognised value, and <see cref="Load"/> answers a throw by keeping the file as
        /// .bad and starting from defaults - so one typo in the field people actually edit
        /// would cost them every other setting. <see cref="ParseMode"/> is lenient instead.</summary>
        public string Mode { get; set; } = nameof(UpdateMode.Manual);

        /// <summary>UTC of the last completed check, so the daily check doesn't fire again
        /// on every restart. Written by the app; there is nothing to hand-edit here.</summary>
        public DateTime? LastCheckUtc { get; set; }

        /// <summary>The version an unattended install was last attempted for, and when. A
        /// silent installer that refuses the job leaves the old version in place, and without
        /// this the next daily check would find the same update and try again - every day,
        /// forever. Written by the app.</summary>
        public string? LastAutoAttemptVersion { get; set; }
        public DateTime? LastAutoAttemptUtc { get; set; }

        /// <summary><see cref="Mode"/> parsed. Not serialized - the string is the stored form.</summary>
        [JsonIgnore]
        public UpdateMode ModeValue
        {
            get => ParseMode(Mode);
            set => Mode = value.ToString();
        }

        /// <summary>Anything unrecognised - a typo, a number, null - reads as
        /// <see cref="UpdateMode.Manual"/>, never as consent to go online. Enum.TryParse also
        /// accepts numbers, including ones no member sits at, so IsDefined has the last word.</summary>
        internal static UpdateMode ParseMode(string? text) =>
            Enum.TryParse<UpdateMode>((text ?? "").Trim(), ignoreCase: true, out var mode)
            && Enum.IsDefined(mode)
                ? mode
                : UpdateMode.Manual;
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

        /// <summary>Read only to migrate configs written before <see cref="Displays"/> existed,
        /// where the popup lived on one monitor. Never written back, so the key drops out of
        /// pawse.json the first time this build saves.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Monitor { get; set; }

        /// <summary>Show on every attached display, re-evaluated each time the popup is built -
        /// so plugging a monitor in is picked up without touching settings. Beats listing every
        /// index, which freezes the answer at whatever was attached that day.</summary>
        public bool AllDisplays { get; set; }

        /// <summary>Which displays to show on when <see cref="AllDisplays"/> is off. Zero-based,
        /// and deliberately allowed to name displays that are not attached right now: an
        /// undocked laptop must not permanently forget the monitors it was set up for.</summary>
        public List<int> Displays { get; set; } = new() { 0 };

        public int VerticalPercent { get; set; } = 50;

        /// <summary>
        /// Which displays the popup actually goes on, given how many are attached. Pure so the
        /// awkward combinations are testable without a screen.
        /// </summary>
        public static IReadOnlyList<int> ResolveDisplays(OverlayCfg cfg, int screenCount)
        {
            if (screenCount <= 0) return Array.Empty<int>();
            if (cfg.AllDisplays) return Enumerable.Range(0, screenCount).ToList();

            var chosen = (cfg.Displays ?? new())
                .Where(i => i >= 0 && i < screenCount)
                .Distinct()
                .OrderBy(i => i)
                .ToList();

            // Everything chosen is unplugged - someone picked display 2 only and then undocked.
            // Falling back to the primary beats showing nothing: the popup is the only thing on
            // screen saying how to unlock, so losing it is worse than putting it somewhere
            // unexpected.
            return chosen.Count > 0 ? chosen : new List<int> { 0 };
        }
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
    /// its defaults instead. The same applies one level down: a null ELEMENT inside a
    /// Keys array (<c>"Keys": ["Ctrl", null]</c>) also deserializes fine and would NRE
    /// in <see cref="Keys.NameToVk"/> - and because the JSON itself is valid, the
    /// .bad-file recovery in <see cref="Load"/> never triggers, so without the scrub
    /// below that one edit would crash-loop every startup.
    /// </summary>
    private void NormalizeAfterLoad()
    {
        General ??= new();
        LockHotkey ??= new() { Enabled = true, Keys = new() { "Ctrl", "L" } };
        LockHotkey.Keys ??= new();
        LockHotkey.Keys.RemoveAll(string.IsNullOrWhiteSpace);
        Unlock ??= new();
        Unlock.Chord ??= new() { Enabled = true, Keys = new() { "Ctrl", "L" } };
        Unlock.Chord.Keys ??= new();
        Unlock.Chord.Keys.RemoveAll(string.IsNullOrWhiteSpace);
        Unlock.Passphrase ??= new();
        Unlock.Passphrase.Text ??= "";
        Unlock.MouseHold ??= new();
        Unlock.Timer ??= new();
        Overlay ??= new();
        Overlay.Displays ??= new();
        // Configs written before multi-display carry "Monitor": N and no "Displays". Its mere
        // presence dates the file: no build has ever written both, and this clears it, so the
        // key cannot outlive one save. Testing the value rather than the count matters -
        // Displays has an initializer of [0], so an absent key leaves a NON-empty list and a
        // count test would silently drop the user's chosen monitor.
        if (Overlay.Monitor is { } monitor && !Overlay.AllDisplays)
            Overlay.Displays = new List<int> { Math.Max(0, monitor) };
        Overlay.Monitor = null;
        // A hand-edited "Displays": [-1, 2, 2] is valid JSON, so the .bad recovery never fires
        // and a bad value would come back every start. Scrub instead.
        Overlay.Displays = Overlay.Displays.Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        SystemBlock ??= new();
        Update ??= new();
        Update.Mode ??= nameof(UpdateMode.Manual);
        // Configs written before the three-level setting carry "AutoCheck": true|false and
        // no "Mode". Fold the old flag in once - true meant the daily notify-only check,
        // which is exactly Notify - then clear it so the next Save writes only the new key.
        // Guarded on Mode still being at its default: someone who has already chosen a level
        // keeps it, and an AutoCheck of false never turns anything on.
        if (Update.AutoCheck == true && Update.ModeValue == UpdateMode.Manual)
            Update.ModeValue = UpdateMode.Notify;
        Update.AutoCheck = null;
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
        $"gui=tray logging={General.Logging} lock_on_start={General.StartLocked} block_mouse={General.BlockMouse} " +
        $"block_screen_keyboard={General.BlockScreenKeyboard} " +
        $"overlay.enabled={Overlay.Enabled} " +
        $"overlay.displays={(Overlay.AllDisplays ? "all" : string.Join("+", Overlay.Displays))} " +
        $"unlock=[chord={Unlock.Chord.Enabled} " +
        $"passphrase={Unlock.Passphrase.Enabled} mouse_hold={Unlock.MouseHold.Enabled} " +
        $"timer={Unlock.Timer.Enabled}] lock_hotkey={LockHotkey.Enabled} " +
        $"update_mode={Update.ModeValue} " +
        $"sysblock=[win_l={SystemBlock.WinLock} launch_media={SystemBlock.LaunchMediaKeys}]";
}
