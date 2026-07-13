using System.Diagnostics;
using System.Security.Principal;

namespace Pawse.Core;

/// <summary>
/// Admin-elevation helpers. The system-key guards (Win+L policy write on managed
/// machines, and the Keyboard Filter for Ctrl+Alt+Del / media keys) need Pawse to
/// run elevated; this lets the tray offer a one-click relaunch as administrator.
/// </summary>
public static class Elevation
{
    /// <summary>
    /// Command-line flag on the elevated relaunch, telling the new instance to
    /// wait briefly for this (departing) one to release the single-instance mutex.
    /// </summary>
    public const string ReplaceArg = "--replace";

    /// <summary>True when the current process runs with an administrator token.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Relaunch Pawse elevated via a UAC prompt. Returns true if the elevated
    /// process was started (the caller should then shut down so the new instance
    /// takes over); false if the user declined UAC or it failed (stay running).
    /// </summary>
    public static bool RelaunchAsAdmin()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) { Log.Error("relaunch: no ProcessPath"); return false; }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = ReplaceArg,
                UseShellExecute = true,   // required for the runas verb
                Verb = "runas",
                WorkingDirectory = Log.ExeDir(),
            });
            Log.Info("relaunching elevated");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("elevation declined by user (UAC cancelled)");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("relaunch as admin", ex);
            return false;
        }
    }
}
