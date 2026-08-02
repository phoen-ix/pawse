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
    /// Re-point an existing Run entry at the current exe. A portable app's folder
    /// gets moved or renamed, and the stale entry would silently do nothing at the
    /// next sign-in. Only repairs - a value the user removed is never resurrected.
    /// </summary>
    public static void Repair()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            var exe = Environment.ProcessPath;
            if (key == null || string.IsNullOrEmpty(exe)) return;
            if (key.GetValue(ValueName) is string current
                && !string.IsNullOrWhiteSpace(current)
                && current != $"\"{exe}\"")
            {
                key.SetValue(ValueName, $"\"{exe}\"");
                Log.Info($"autostart entry re-pointed at {exe} (was {current})");
            }
        }
        catch (Exception ex) { Log.Error("autostart repair", ex); }
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
