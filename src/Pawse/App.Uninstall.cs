using Pawse.Core;

namespace Pawse;

/// <summary>Not in the Store build (see Pawse.csproj): an MSIX has no uninstaller to call this.</summary>
public partial class App
{
    partial void RunUninstallCleanup(string[] args, ref bool handled)
    {
        if (!args.Contains(UninstallCleanup.Arg)) return;
        handled = true;
        Shutdown(UninstallCleanup.Run(allUsers: args.Contains(UninstallCleanup.AllUsersArg)));
    }
}
