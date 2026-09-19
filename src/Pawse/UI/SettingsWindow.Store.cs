using System.Windows;

namespace Pawse.UI;

/// <summary>
/// Store build only (see Pawse.csproj): the Store updates Pawse, so the About page says so in
/// place of the updater's panel, which SettingsWindow.Updates.cs would show - and which this
/// build does not compile.
/// </summary>
public partial class SettingsWindow
{
    partial void InitUpdateSection() => LblStoreUpdates.Visibility = Visibility.Visible;
}
