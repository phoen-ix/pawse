using System.IO;
using Microsoft.Win32;

namespace Pawse.Core;

/// <summary>Which hive an install's Add/Remove entry sits in. Per-machine cannot be updated
/// without a UAC prompt, so an unattended update declines it rather than surprising anyone.</summary>
public enum InstallScope
{
    None,
    PerUser,
    PerMachine,
}

/// <summary>How this copy of Pawse got onto the machine - which decides what it may write, and
/// where. See <see cref="Deployment.Mode"/>.</summary>
public enum DeploymentMode
{
    /// <summary>Put there by the installer: its Apps &amp; features entry names this very folder.
    /// May use the registry (Start with Windows, Block Win+L, the Keyboard Filter markers), and
    /// the uninstaller removes all of it again.</summary>
    Installed,

    /// <summary>Anything else - a zip unpacked somewhere, or an installed exe copied elsewhere.
    /// Writes nothing outside its own folder: settings and log live next to the exe, and nothing
    /// is written to the registry, so deleting the folder removes Pawse. The one exception is
    /// .NET's own unpack cache in %TEMP%\.net, which the runtime writes before Pawse runs.</summary>
    Portable,

    /// <summary>The Microsoft Store package (the STORE build): data in the package's own
    /// folder, start-at-sign-in through its StartupTask, removed by Windows on uninstall.</summary>
    Store,
}

/// <summary>
/// Installed, portable or Store - answered once per process, before anything is read or
/// written, because it decides where the config and log go (<see cref="Log"/>) and whether the
/// registry may be touched at all.
/// <para>"Installed" is the installer's own Add/Remove entry, <see cref="UninstallKey"/>, whose
/// InstallLocation names the folder this exe runs from. A copy of the exe anywhere else is
/// portable, even on a PC where Pawse is also installed - and so is the installed exe itself if
/// that entry was removed by hand.</para>
/// </summary>
public static class Deployment
{
    /// <summary>Also hard-coded in packaging/pawse.nsi as UNINST_KEY - change both or
    /// neither, or an installed Pawse starts reading as portable.</summary>
    internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pawse";

    private static DeploymentMode? _mode;

    /// <summary>This copy's mode. Cached: an install is not something that changes under a
    /// running process (an in-place upgrade closes it first).</summary>
    public static DeploymentMode Mode => _mode ??= Detect();

    public static bool IsPortable => Mode == DeploymentMode.Portable;

    private static DeploymentMode Detect()
    {
#if STORE
        return DeploymentMode.Store;
#else
        return ModeOf(Scope());
#endif
    }

    /// <summary>Test seam for the mode decision of the non-Store builds.</summary>
    internal static DeploymentMode ModeOf(InstallScope scope) =>
        scope == InstallScope.None ? DeploymentMode.Portable : DeploymentMode.Installed;

    /// <summary>Which hive registers THIS folder as installed, if any. Read fresh on every call
    /// (the updater asks it as well); <see cref="Mode"/> is the cached answer.</summary>
    public static InstallScope Scope() =>
        ScopeOf(Log.ExeDir(), ReadValue(Registry.CurrentUser, "InstallLocation"),
                ReadValue(Registry.LocalMachine, "InstallLocation"));

    /// <summary>Test seam for <see cref="Scope()"/>. HKCU first, the same order the
    /// installer's own previous-install probe uses.</summary>
    internal static InstallScope ScopeOf(string exeDir, string? hkcuLocation, string? hklmLocation)
    {
        if (!string.IsNullOrWhiteSpace(hkcuLocation) && SameFolder(hkcuLocation, exeDir))
            return InstallScope.PerUser;
        if (!string.IsNullOrWhiteSpace(hklmLocation) && SameFolder(hklmLocation, exeDir))
            return InstallScope.PerMachine;
        return InstallScope.None;
    }

    /// <summary>Same comparison for two file paths (a Run value's target against this exe).</summary>
    internal static bool SameFile(string a, string b) => SameFolder(a, b);

    internal static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>A value under the Add/Remove entry, or null. The installer is a 32-bit NSIS exe,
    /// so a per-machine entry lands in HKLM's WOW6432Node view, which this x64 process does not
    /// see by default - HKLM is read in both views.</summary>
    internal static string? ReadValue(RegistryKey root, string name)
    {
        var value = ReadValueIn(root, name);
        if (value is null && root.Name == Registry.LocalMachine.Name)
        {
            try
            {
                using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                value = ReadValueIn(hklm32, name);
            }
            catch { /* no 32-bit view to read - not installed there */ }
        }
        return value;
    }

    private static string? ReadValueIn(RegistryKey root, string name)
    {
        try
        {
            using var key = root.OpenSubKey(UninstallKey);
            if (key?.GetValue(name) is string value && value.Length > 0) return value;
        }
        catch { /* an unreadable key just means "not installed here" */ }
        return null;
    }
}
