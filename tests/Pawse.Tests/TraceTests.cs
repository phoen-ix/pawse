using System.IO;
using Microsoft.Win32;
using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

/// <summary>Where the config and log live, per mode (<see cref="Log.ChooseBaseDir"/>).</summary>
public class DataFolderTests
{
    private const string Exe = @"C:\Tools\Pawse";
    private const string AppData = @"C:\Users\me\AppData\Roaming\Pawse";

    private static string Choose(DeploymentMode mode, bool exeWritable, out bool adopt,
                                 bool exeConfig = false, bool appDataConfig = false)
        => Log.ChooseBaseDir(mode, Exe, AppData,
            path => (exeConfig && path == Path.Combine(Exe, "pawse.json"))
                    || (appDataConfig && path == Path.Combine(AppData, "pawse.json")),
            _ => exeWritable, out adopt);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_portable_copy_never_leaves_its_folder(bool writable)
    {
        Assert.Equal(Exe, Choose(DeploymentMode.Portable, writable, out bool adopt, appDataConfig: true));
        Assert.False(adopt);
    }

    [Fact]
    public void An_installed_copy_prefers_an_existing_appdata_config()
        => Assert.Equal(AppData, Choose(DeploymentMode.Installed, true, out _, exeConfig: true, appDataConfig: true));

    [Fact]
    public void An_installed_copy_keeps_a_writable_config_next_to_the_exe()
        => Assert.Equal(Exe, Choose(DeploymentMode.Installed, true, out _, exeConfig: true));

    [Fact]
    public void A_read_only_config_next_to_the_exe_is_adopted_into_appdata()
    {
        Assert.Equal(AppData, Choose(DeploymentMode.Installed, false, out bool adopt, exeConfig: true));
        Assert.True(adopt);
    }

    [Theory]
    [InlineData(true, Exe)]
    [InlineData(false, AppData)]
    public void A_first_run_installed_copy_goes_where_it_can_write(bool writable, string expected)
        => Assert.Equal(expected, Choose(DeploymentMode.Installed, writable, out _));
}

/// <summary>A throwaway HKCU subkey standing in for a user's hive, so the registry code runs
/// for real without touching the account running the tests.</summary>
public abstract class RegistryRootTest : IDisposable
{
    private readonly string _path = @"Software\PawseTests\" + Guid.NewGuid().ToString("N");
    protected RegistryKey Root { get; }

    protected RegistryRootTest() => Root = Registry.CurrentUser.CreateSubKey(_path);

    protected const string PoliciesKey = @"Software\Microsoft\Windows\CurrentVersion\Policies";
    protected const string PolicyKey = PoliciesKey + @"\System";

    protected bool Exists(string subKey)
    {
        using var key = Root.OpenSubKey(subKey);
        return key is not null;
    }

    protected object? Value(string subKey, string name)
    {
        using var key = Root.OpenSubKey(subKey);
        return key?.GetValue(name);
    }

    /// <summary>Anything a test class sets up besides the registry root.</summary>
    protected virtual void Cleanup() { }

    public void Dispose()
    {
        Cleanup();
        Root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}

public class WorkstationLockTraceTests : RegistryRootTest
{
    [Fact]
    public void Keys_it_had_to_create_go_again_on_restore()
    {
        Assert.True(WorkstationLock.Suppress(Root));
        Assert.Equal(1, Value(PolicyKey, "DisableLockWorkstation"));

        Assert.True(WorkstationLock.Restore(Root));
        Assert.False(Exists(PolicyKey));
        Assert.False(Exists(PoliciesKey));
        Assert.False(Exists(WorkstationLock.OwnerKey));
    }

    [Fact]
    public void A_key_that_was_already_there_stays_with_its_other_values()
    {
        using (var key = Root.CreateSubKey(PolicyKey)) key.SetValue("SomethingElse", 7);

        Assert.True(WorkstationLock.Suppress(Root));
        Assert.True(WorkstationLock.Restore(Root));

        Assert.True(Exists(PolicyKey));
        Assert.Equal(7, Value(PolicyKey, "SomethingElse"));
        Assert.Null(Value(PolicyKey, "DisableLockWorkstation"));
        Assert.False(Exists(WorkstationLock.OwnerKey));
    }

    [Fact]
    public void A_value_that_was_there_before_is_put_back()
    {
        using (var key = Root.CreateSubKey(PolicyKey)) key.SetValue("DisableLockWorkstation", 0, RegistryValueKind.DWord);

        Assert.True(WorkstationLock.Suppress(Root));
        Assert.True(WorkstationLock.Restore(Root));

        Assert.Equal(0, Value(PolicyKey, "DisableLockWorkstation"));
    }

    [Fact]
    public void The_elevation_probe_leaves_nothing_behind()
    {
        Assert.False(WorkstationLock.NeedsElevation(Root));
        Assert.False(Exists(PoliciesKey));
    }

    [Fact]
    public void Its_own_key_stays_while_another_marker_is_owed()
    {
        using (var own = Root.CreateSubKey(WorkstationLock.OwnerKey)) own.SetValue("WekfLeftOn", 1, RegistryValueKind.DWord);

        Assert.True(WorkstationLock.Suppress(Root));
        Assert.True(WorkstationLock.Restore(Root));

        Assert.Equal(1, Value(WorkstationLock.OwnerKey, "WekfLeftOn"));
        Assert.Null(Value(WorkstationLock.OwnerKey, "PrevDisableLockWorkstation"));
    }
}

public class UninstallCleanupTests : RegistryRootTest
{
    private const string InstallDir = @"C:\Users\me\AppData\Local\Programs\Pawse";
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "pawse-profile-" + Guid.NewGuid().ToString("N"));

    private string Roaming => Path.Combine(_profile, "Roaming");
    private string Temp => Path.Combine(_profile, "Temp");

    private void Touch(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private void SetRun(string value)
    {
        using var run = Root.CreateSubKey(Autostart.RunKey);
        run.SetValue(Autostart.ValueName, value);
    }

    [Theory]
    [InlineData("\"" + InstallDir + "\\Pawse.exe\"", true)]
    [InlineData(InstallDir + "\\Pawse.exe", true)]
    [InlineData("\"c:\\users\\me\\appdata\\local\\programs\\pawse\\Pawse.exe\"", true)]
    [InlineData("\"C:\\Tools\\Pawse\\Pawse.exe\"", false)]
    [InlineData("", false)]
    public void Only_a_run_entry_into_the_install_folder_is_ours(string value, bool ours)
        => Assert.Equal(ours, UninstallCleanup.PointsInto(value, InstallDir));

    [Fact]
    public void An_account_is_left_with_nothing_of_pawse()
    {
        // A crash while locked: value set, keys created, markers owed - plus autostart,
        // settings, the .NET unpack cache and an old download.
        Assert.True(WorkstationLock.Suppress(Root));
        SetRun($"\"{InstallDir}\\Pawse.exe\"");
        Touch(Roaming, "Pawse", "pawse.json");
        Touch(Temp, ".net", "Pawse", "abc123", "wpfgfx_cor3.dll");
        Touch(Temp, "Pawse-update-xyz", "Pawse-Setup-9.9.9-full.exe");

        var result = UninstallCleanup.CleanAccount(Root, InstallDir, new KeyboardFilterGuard(), Roaming, Temp);

        Assert.Equal(SystemBlock.FilterRevert.NothingOwed, result);
        Assert.False(Exists(PoliciesKey));
        Assert.False(Exists(WorkstationLock.OwnerKey));
        Assert.Null(Value(Autostart.RunKey, Autostart.ValueName));
        Assert.False(Directory.Exists(Path.Combine(Roaming, "Pawse")));
        Assert.False(Directory.Exists(Path.Combine(Temp, ".net", "Pawse")));
        Assert.Empty(Directory.EnumerateDirectories(Temp, "Pawse-update-*"));
    }

    [Fact]
    public void Another_copy_s_autostart_entry_is_left_alone()
    {
        SetRun("\"C:\\Tools\\Pawse\\Pawse.exe\"");
        UninstallCleanup.CleanAccount(Root, InstallDir, new KeyboardFilterGuard(), null, null);
        Assert.Equal("\"C:\\Tools\\Pawse\\Pawse.exe\"", Value(Autostart.RunKey, Autostart.ValueName));
    }

    protected override void Cleanup()
    {
        try { Directory.Delete(_profile, recursive: true); } catch { /* best effort */ }
    }
}
