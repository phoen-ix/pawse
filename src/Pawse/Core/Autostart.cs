using System.IO;
using Microsoft.Win32;

namespace Pawse.Core;

/// <summary>
/// Start-with-Windows via the per-user Run key. Purely local - no scheduled
/// tasks, no services, no network. Installed copies only: a portable copy writes nothing to
/// the registry (<see cref="DeploymentMode.Portable"/>), and the uninstaller removes the value.
/// </summary>
public static class Autostart
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Pawse";

    /// <summary>What <see cref="SetEnabled"/> answers a portable copy. Settings greys the
    /// checkbox out with the same reason, so this is the fallback, not the usual path.</summary>
    public const string PortableRefusal =
        "Start with Windows needs the installed version of Pawse - a portable copy writes nothing to the registry.";

    public static bool IsEnabled()
    {
        if (Deployment.IsPortable) return false;
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
    /// Keep the Run entry honest at startup. Returns a notice for the tray, or null.
    /// <para>A portable copy: older versions let it start with Windows, which is now the
    /// installed version's job. An entry naming THIS exe is removed - deleting is the one
    /// registry change a portable copy makes, and only to take back what it wrote itself. An
    /// entry naming another exe belongs to another copy and is left alone.</para>
    /// <para>An installed copy: re-point an existing entry at the current exe - but only when
    /// the exe it points at is GONE, so the entry would silently do nothing at the next
    /// sign-in. An entry whose target still exists is a deliberate choice and is not
    /// hijacked, and a value the user removed is never resurrected.</para>
    /// </summary>
    public static string? Repair()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            var exe = Environment.ProcessPath;
            if (key == null || string.IsNullOrEmpty(exe)) return null;
            if (key.GetValue(ValueName) is not string current || string.IsNullOrWhiteSpace(current)) return null;

            if (Deployment.IsPortable)
            {
                if (!Deployment.SameFile(UnquotedPath(current), exe)) return null;
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info($"autostart: removed the entry of this portable copy ({current})");
                return "Start with Windows is now part of the installed version of Pawse, so this portable " +
                       "copy removed its autostart entry. Install Pawse to have it start when you sign in.";
            }

            if (current == $"\"{exe}\"") return null;
            if (File.Exists(UnquotedPath(current))) return null; // still valid - not ours to touch
            key.SetValue(ValueName, $"\"{exe}\"");
            Log.Info($"autostart entry re-pointed at {exe} (was {current})");
        }
        catch (Exception ex) { Log.Error("autostart repair", ex); }
        return null;
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

    /// <summary>Turn start-at-sign-in on or off. Returns null when done, otherwise what to tell
    /// the user - the Store build's StartupTask can be refused; the Run key never says no, so
    /// this one always returns null (a failure is only logged, as before).</summary>
    public static string? SetEnabled(bool on)
    {
        if (Deployment.IsPortable) return on ? PortableRefusal : null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return null;
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
        return null;
    }
}
