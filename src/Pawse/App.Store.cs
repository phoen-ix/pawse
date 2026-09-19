using System.IO;
using Pawse.Core;

namespace Pawse;

/// <summary>Store build only (see Pawse.csproj).</summary>
public partial class App
{
    /// <summary>What a packaged copy can't show anywhere else: whether it really runs as its
    /// package (an elevated relaunch must, or it loses its data folder and startup task), where
    /// that put the config, and what Windows says about start-at-sign-in. One line, so a report
    /// from a Store install is answerable from pawse.log alone.</summary>
    partial void LogPackageContext()
    {
        string package;
        // global:: - inside App, "Windows" is Application.Windows, the window list.
        try { package = global::Windows.ApplicationModel.Package.Current.Id.FullName; }
        catch { package = "none (not running as a package)"; }
        Log.Info($"store build: package {package}; data {Path.GetDirectoryName(Config.PathOnDisk())}; "
                 + $"startup task {Autostart.State()?.ToString() ?? "unknown"}; elevated {Elevation.IsElevated()}");
    }
}
