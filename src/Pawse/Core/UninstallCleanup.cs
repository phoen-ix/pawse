using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace Pawse.Core;

/// <summary>
/// <c>Pawse.exe --uninstall-cleanup [--all-users]</c> - what the uninstaller
/// (packaging/pawse.nsi) runs before it deletes the program files, so that uninstalling leaves
/// nothing of Pawse behind. It reuses the app's own revert code rather than a copy of it: the
/// Win+L policy value and the keys created for it (<see cref="WorkstationLock"/>), and the
/// Keyboard Filter rules (<see cref="SystemBlock.RevertOwedFilter"/>). Then it deletes what
/// Pawse keeps per account:
/// <list type="bullet">
/// <item><c>Software\Pawse</c> and the Run value, if it points into the install folder;</item>
/// <item>%APPDATA%\Pawse (settings and log of a machine-wide install);</item>
/// <item>%TEMP%\.net\Pawse (the .NET host's unpack cache of the single-file build) and any
/// %TEMP%\Pawse-update-* download.</item>
/// </list>
/// <para>Without <c>--all-users</c> that is the current account. With it - a machine-wide
/// uninstall, which runs elevated - every account on the PC: a signed-in one through its hive
/// under HKEY_USERS, a signed-out one by loading its NTUSER.DAT for the moment it takes.</para>
/// <para>Runs before <see cref="Log.Init"/> on purpose: resolving the log's folder could
/// recreate the very %APPDATA%\Pawse this deletes. Nothing is written to a log.</para>
/// <para>Not compiled into the Store build (see Pawse.csproj): an MSIX has no uninstaller.</para>
/// </summary>
internal static class UninstallCleanup
{
    public const string Arg = "--uninstall-cleanup";
    public const string AllUsersArg = "--all-users";

    // Exit codes - pawse.nsi reads these, change both or neither.
    public const int ExitDone = 0;
    public const int ExitIncomplete = 1;
    /// <summary>Pawse's Keyboard Filter rules are still on and need administrator rights to turn
    /// off. Only a per-user uninstall (not elevated) can hit this; its marker is kept.</summary>
    public const int ExitKeyboardFilterNeedsAdmin = 3;

    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public static int Run(bool allUsers)
    {
        string installDir = Log.ExeDir();
        var kf = new KeyboardFilterGuard();
        bool incomplete = false, needsAdmin = false;
        string? me = WindowsIdentity.GetCurrent().User?.Value;

        // This account first, with its real folders (they may be redirected) and its own hive.
        var mine = CleanAccount(Registry.CurrentUser, installDir, kf,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.GetTempPath());
        needsAdmin |= mine == SystemBlock.FilterRevert.NeedsAdmin;
        incomplete |= mine == SystemBlock.FilterRevert.Failed;

        if (allUsers)
        {
            foreach (var (sid, profileDir) in OtherProfiles(me))
            {
                try
                {
                    using var hive = UserHive.Open(sid, profileDir);
                    var result = hive is null
                        ? SystemBlock.FilterRevert.NothingOwed
                        : CleanAccount(hive.Key, installDir, kf, null, null);
                    incomplete |= result == SystemBlock.FilterRevert.Failed;
                    CleanFiles(Path.Combine(profileDir, "AppData", "Roaming"),
                               Path.Combine(profileDir, "AppData", "Local", "Temp"));
                }
                catch { incomplete = true; }
            }
        }

        return needsAdmin ? ExitKeyboardFilterNeedsAdmin : incomplete ? ExitIncomplete : ExitDone;
    }

    /// <summary>One account's registry - and its folders, when given. Test seam: a throwaway key
    /// stands in for the hive, temp folders for the profile.</summary>
    internal static SystemBlock.FilterRevert CleanAccount(RegistryKey userRoot, string installDir,
                                                          KeyboardFilterGuard kf,
                                                          string? roamingAppData, string? localTemp)
    {
        WorkstationLock.Restore(userRoot);
        var filter = SystemBlock.RevertOwedFilter(userRoot, kf);

        try
        {
            using var run = userRoot.OpenSubKey(Autostart.RunKey, writable: true);
            if (run?.GetValue(Autostart.ValueName) is string entry && PointsInto(entry, installDir))
                run.DeleteValue(Autostart.ValueName, throwOnMissingValue: false);
        }
        catch { /* unreadable - nothing of ours reachable there */ }

        // Everything else of Pawse's in this hive - unless a Keyboard Filter revert is still owed:
        // its marker is then the only thing an elevated run could finish the job from.
        if (filter is not (SystemBlock.FilterRevert.NeedsAdmin or SystemBlock.FilterRevert.Failed))
        {
            try { userRoot.DeleteSubKeyTree(WorkstationLock.OwnerKey, throwOnMissingSubKey: false); }
            catch { /* already gone, or not ours to open */ }
        }

        if (roamingAppData is not null && localTemp is not null) CleanFiles(roamingAppData, localTemp);
        return filter;
    }

    /// <summary>The per-account folders. %TEMP%\.net\Pawse of the account running this is in use
    /// by this very process (the host unpacked into it to start it), so the uninstaller deletes
    /// that one again once the process has exited.</summary>
    internal static void CleanFiles(string roamingAppData, string localTemp)
    {
        TryDeleteDir(Path.Combine(roamingAppData, "Pawse"));
        TryDeleteDir(Path.Combine(localTemp, ".net", "Pawse"));
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(localTemp, "Pawse-update-*"))
                TryDeleteDir(dir);
        }
        catch { /* no temp folder - nothing downloaded there */ }
    }

    /// <summary>A Run value is ours when the exe it starts is in the install folder.</summary>
    internal static bool PointsInto(string runValue, string installDir)
    {
        var v = runValue.Trim();
        if (v.StartsWith('"'))
        {
            int end = v.IndexOf('"', 1);
            if (end > 1) v = v[1..end];
        }
        try { return Deployment.SameFolder(Path.GetDirectoryName(v) ?? "", installDir); }
        catch { return false; }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* in use or not ours to delete - leave it */ }
    }

    /// <summary>Real user profiles (S-1-5-21-…) other than the account running this.</summary>
    private static IEnumerable<(string Sid, string Dir)> OtherProfiles(string? exceptSid)
    {
        var profiles = new List<(string, string)>();
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(ProfileListKey);
            foreach (var sid in list?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid == exceptSid) continue;
                using var entry = list!.OpenSubKey(sid);
                if (entry?.GetValue("ProfileImagePath") is string dir && Directory.Exists(dir))
                    profiles.Add((sid, dir));
            }
        }
        catch { /* no profile list readable - only this account gets cleaned */ }
        return profiles;
    }

    /// <summary>Another account's registry hive: the loaded one under HKEY_USERS while it is
    /// signed in, otherwise its NTUSER.DAT, mounted for as long as this lives.</summary>
    private sealed class UserHive : IDisposable
    {
        public RegistryKey Key { get; }
        private readonly string? _mountedAs;

        private UserHive(RegistryKey key, string? mountedAs) { Key = key; _mountedAs = mountedAs; }

        public static UserHive? Open(string sid, string profileDir)
        {
            var loaded = Registry.Users.OpenSubKey(sid, writable: true);
            if (loaded is not null) return new UserHive(loaded, null);

            var dat = Path.Combine(profileDir, "NTUSER.DAT");
            if (!File.Exists(dat) || !EnablePrivileges()) return null;
            var name = "Pawse-uninstall-" + sid;
            if (NativeMethods.RegLoadKeyW(NativeMethods.HKEY_USERS, name, dat) != 0) return null;
            var key = Registry.Users.OpenSubKey(name, writable: true);
            if (key is null)
            {
                NativeMethods.RegUnLoadKeyW(NativeMethods.HKEY_USERS, name);
                return null;
            }
            return new UserHive(key, name);
        }

        public void Dispose()
        {
            Key.Dispose();
            if (_mountedAs is not null) NativeMethods.RegUnLoadKeyW(NativeMethods.HKEY_USERS, _mountedAs);
        }

        private static bool _privileged;

        /// <summary>RegLoadKey needs SeBackupPrivilege and SeRestorePrivilege enabled. An
        /// administrator's elevated token holds both, disabled until asked for.</summary>
        private static bool EnablePrivileges()
        {
            if (_privileged) return true;
            try
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.AdjustPrivileges | TokenAccessLevels.Query);
                foreach (var name in new[] { "SeBackupPrivilege", "SeRestorePrivilege" })
                {
                    if (!NativeMethods.LookupPrivilegeValueW(null, name, out var luid)) return false;
                    var state = new NativeMethods.TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = NativeMethods.SE_PRIVILEGE_ENABLED,
                    };
                    if (!NativeMethods.AdjustTokenPrivileges(identity.Token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
                        return false;
                }
                return _privileged = true;
            }
            catch { return false; }
        }
    }
}
