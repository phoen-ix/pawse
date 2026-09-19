using System.Windows;
using Pawse.Core;

namespace Pawse.UI;

/// <summary>
/// About → Updates: the update-mode choice, "Check now" and what came of it. Not compiled into
/// the Store build (see Pawse.csproj), where the Store owns updates - SettingsWindow.Store.cs
/// takes its place there. The panel is collapsed in XAML and shown from here, and the buttons
/// are wired here rather than with Click= in XAML, so the Store build's XAML refers to no
/// handler it doesn't have.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>Raised by "Check now". App owns the check itself (and the download that may
    /// follow); this window only shows what came of it - see <see cref="ShowUpdateStatus"/>.</summary>
    public event Action? CheckUpdatesRequested;

    /// <summary>Raised by the "Downloads page" button that appears after a failed check. App
    /// owns opening it, the same as it owns the check itself.</summary>
    public event Action? DownloadsPageRequested;

    partial void InitUpdateSection()
    {
        UpdatesPanel.Visibility = Visibility.Visible;
        BtnCheckUpdates.Click += OnCheckUpdates;
        BtnDownloadsPage.Click += OnOpenDownloadsPage;
    }

    partial void LoadUpdates()
    {
        RbUpdManual.IsChecked = _cfg.Update.ModeValue == Config.UpdateMode.Manual;
        RbUpdNotify.IsChecked = _cfg.Update.ModeValue == Config.UpdateMode.Notify;
        RbUpdAuto.IsChecked = _cfg.Update.ModeValue == Config.UpdateMode.Automatic;
        ShowUpdateCaveat();
        LblUpdateStatus.Text = _cfg.Update.LastCheckUtc is { } last
            ? $"Last checked {last.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "Never checked";
    }

    partial void SaveUpdates()
    {
        _cfg.Update.ModeValue =
            RbUpdAuto.IsChecked == true ? Config.UpdateMode.Automatic :
            RbUpdNotify.IsChecked == true ? Config.UpdateMode.Notify :
            Config.UpdateMode.Manual;
    }

    private void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdates.IsEnabled = false;
        BtnDownloadsPage.Visibility = Visibility.Collapsed;
        LblUpdateStatus.Text = "Checking…";
        CheckUpdatesRequested?.Invoke();
    }

    private void OnOpenDownloadsPage(object sender, RoutedEventArgs e) => DownloadsPageRequested?.Invoke();

    /// <summary>Progress while a check is still running: the text changes, the button stays
    /// disabled. <see cref="ShowUpdateStatus"/> is the terminal one and re-enables it.</summary>
    public void ShowUpdateProgress(string text) => LblUpdateStatus.Text = text;

    /// <summary>A check that reached nobody. The button becomes the retry - a first attempt
    /// fails far more often than a second - and the downloads page moves in beside it rather
    /// than interrupting with a dialog.</summary>
    public void ShowUpdateFailure(string text)
    {
        LblUpdateStatus.Text = text;
        BtnCheckUpdates.Content = "Try again";
        BtnCheckUpdates.IsEnabled = true;
        BtnDownloadsPage.Visibility = Visibility.Visible;
    }

    /// <summary>Report a finished check. Called by App on the UI thread; safe to call after
    /// the user has closed the window (App null-checks its reference, WPF ignores the rest).</summary>
    public void ShowUpdateStatus(string text)
    {
        LblUpdateStatus.Text = text;
        BtnCheckUpdates.Content = "Check now";
        BtnCheckUpdates.IsEnabled = true;
        BtnDownloadsPage.Visibility = Visibility.Collapsed;
    }

    /// <summary>Say up front when this copy cannot take an update on its own, so choosing
    /// "automatically" never quietly means "notify". Both reasons are properties of where
    /// Pawse is installed, so neither can change while the window is open.</summary>
    private void ShowUpdateCaveat()
    {
        // The same test App.AutoInstallRefusal applies, so the two cannot drift apart.
        string? reason = UpdateCheck.LocalInstallObstacle(UpdateCheck.DetectInstall()) switch
        {
            LocalObstacle.PerMachine => "This copy is installed for everyone on this PC, so updates are offered "
                                      + "rather than installed - installing needs administrator rights.",
            LocalObstacle.FolderNotWritable => "Pawse can't write to its own folder, so it can only tell you about "
                                             + "updates - installing one is up to you.",
            _ => null,
        };

        LblUpdateCaveat.Text = reason ?? "";
        LblUpdateCaveat.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
