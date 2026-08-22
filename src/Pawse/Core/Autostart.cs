using Microsoft.Win32;

namespace Pawse.Core;

/// <summary>
/// Start-with-Windows via the per-user Run key. Purely local - no scheduled
/// tasks, no services, no network.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Pawse";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch (Exception ex)
        {
            Log.Error("autostart read", ex);
            return false;
        }
    }

    /// <summary>
    /// Re-point an existing Run entry at the current exe - but only when the exe it
    /// points at is GONE. A portable app's folder gets moved or renamed, and the stale
    /// entry would silently do nothing at the next sign-in; that is the case being
    /// repaired. An entry whose target still exists is a deliberate choice, and merely
    /// launching another copy once (a portable one from Downloads, say) must not hijack
    /// the installed copy's autostart. A value the user removed is never resurrected.
    /// </summary>
    public static void Repair()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            var exe = Environment.ProcessPath;
            if (key == null || string.IsNullOrEmpty(exe)) return;
            if (key.GetValue(ValueName) is not string current
                || string.IsNullOrWhiteSpace(current)
                || current == $"\"{exe}\"") return;
            if (File.Exists(UnquotedPath(current))) return; // still valid - not ours to touch
            key.SetValue(ValueName, $"\"{exe}\"");
            Log.Info($"autostart entry re-pointed at {exe} (was {current})");
        }
        catch (Exception ex) { Log.Error("autostart repair", ex); }
    }

    /// <summary>The executable path out of a Run value: the quoted segment if the value
    /// starts with a quote, otherwise the value as-is (Pawse always writes it quoted).</summary>
    private static string UnquotedPath(string value)
    {
        var v = value.Trim();
        if (v.StartsWith('"'))
        {
            int end = v.IndexOf('"', 1);
            if (end > 1) return v[1..end];
        }
        return v;
    }

    public static void SetEnabled(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (on)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            Log.Info($"autostart set to {on}");
        }
        catch (Exception ex)
        {
            Log.Error("autostart write", ex);
        }
    }
}
