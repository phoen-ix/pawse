using Pawse.Core;
using Xunit;

namespace Pawse.Tests;

public class DeploymentScopeTests
{
    private const string Dir = @"C:\Program Files\Pawse";

    [Fact]
    public void A_per_user_entry_wins_over_a_per_machine_one()
        => Assert.Equal(InstallScope.PerUser, Deployment.ScopeOf(Dir, Dir, Dir));

    [Fact]
    public void Only_a_machine_entry_reads_as_per_machine()
        => Assert.Equal(InstallScope.PerMachine, Deployment.ScopeOf(Dir, null, Dir));

    [Fact]
    public void An_entry_for_another_folder_does_not_count()
        => Assert.Equal(InstallScope.None, Deployment.ScopeOf(Dir, @"D:\Elsewhere", null));

    [Fact]
    public void No_entry_at_all_is_no_scope()
        => Assert.Equal(InstallScope.None, Deployment.ScopeOf(Dir, null, null));

    [Fact]
    public void The_comparison_tolerates_case_and_a_trailing_separator()
        => Assert.Equal(InstallScope.PerUser, Deployment.ScopeOf(Dir, @"c:\program files\pawse\", null));
}

public class DeploymentModeTests
{
    // "Installed" is the installer's own entry naming this folder - in either hive. Anything
    // else is portable, and a portable copy writes nothing outside its folder.
    [Theory]
    [InlineData(InstallScope.PerUser, DeploymentMode.Installed)]
    [InlineData(InstallScope.PerMachine, DeploymentMode.Installed)]
    [InlineData(InstallScope.None, DeploymentMode.Portable)]
    public void The_uninstall_entry_for_this_folder_decides(InstallScope scope, DeploymentMode expected)
        => Assert.Equal(expected, Deployment.ModeOf(scope));
}
