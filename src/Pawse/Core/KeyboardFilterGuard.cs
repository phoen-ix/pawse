using System.Management;

namespace Pawse.Core;

/// <summary>
/// Blocks the browser / calculator / media / volume keys - which bypass
/// <c>WH_KEYBOARD_LL</c> by arriving as <c>WM_APPCOMMAND</c> - using the Windows
/// <b>Keyboard Filter</b> feature via its WMI class <c>WEKF_PredefinedKey</c>
/// (namespace <c>root\standardcimv2\embedded</c>).
///
/// <para>Only usable on Windows Enterprise / Education / IoT Enterprise with the
/// "Keyboard Filter" optional feature installed, and only when Pawse runs elevated
/// (the WMI writes require admin). <see cref="IsAvailable"/> reports this; when it
/// is false, <see cref="Set"/> is a safe no-op. Rules toggle live (no reboot), so
/// we enable them on lock and disable them on unlock.</para>
/// </summary>
public sealed class KeyboardFilterGuard
{
    private const string WmiScope = @"root\standardcimv2\embedded";
    private const string PredefinedClass = "WEKF_PredefinedKey";

    /// <summary>Browser / calculator (LaunchApp2) / media / volume keys.</summary>
    public static readonly string[] LaunchMediaIds =
    {
        "BrowserBack", "BrowserForward", "BrowserRefresh", "BrowserStop",
        "BrowserSearch", "BrowserFavorites", "BrowserHome",
        "LaunchMail", "LaunchMediaSelect", "LaunchApp1", "LaunchApp2",
        "MediaNext", "MediaPrev", "MediaStop", "MediaPlayPause",
        "VolumeMute", "VolumeDown", "VolumeUp",
    };

    private bool? _available;

    /// <summary>
    /// True only when Pawse is elevated AND the Keyboard Filter WMI class is
    /// present. Cached after the first probe. The elevation check short-circuits
    /// first, so the common (non-elevated) case never touches WMI.
    /// </summary>
    public bool IsAvailable()
    {
        _available ??= Probe();
        return _available.Value;
    }

    private static bool Probe()
    {
        if (!Elevation.IsElevated())
        {
            Log.Warn("keyboard-filter: Pawse is not elevated - media-key blocking unavailable");
            return false;
        }
        try
        {
            var scope = new ManagementScope(WmiScope);
            scope.Connect(); // throws if the namespace/feature is absent
            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery($"SELECT Id FROM {PredefinedClass}"));
            using var results = searcher.Get();
            _ = results.Count; // force the query - throws if the class is missing
            Log.Info("keyboard-filter: feature available");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("keyboard-filter: feature not present (needs Enterprise/Education/IoT + the Keyboard Filter feature) :: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Enable or disable the given predefined key combinations. No-op (safe) when
    /// the feature is unavailable. Intended to be called off the UI thread.
    /// </summary>
    public void Set(IReadOnlyCollection<string> ids, bool enabled)
    {
        if (ids.Count == 0 || !IsAvailable()) return;
        foreach (var id in ids)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    WmiScope, $"SELECT * FROM {PredefinedClass} WHERE Id='{id}'");
                using var col = searcher.Get();
                bool found = false;
                foreach (ManagementObject mo in col)
                {
                    using (mo)
                    {
                        mo["Enabled"] = enabled;
                        mo.Put();
                        found = true;
                    }
                }
                if (!found) Log.Warn($"keyboard-filter: predefined key '{id}' not found on this build");
            }
            catch (Exception ex) { Log.Error($"keyboard-filter set {id}={enabled}", ex); }
        }
        Log.Info($"keyboard-filter: {(enabled ? "enabled" : "disabled")} [{string.Join(", ", ids)}]");
    }
}
