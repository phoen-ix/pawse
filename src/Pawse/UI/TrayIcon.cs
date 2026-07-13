using System.Windows.Forms;

namespace Pawse.UI;

/// <summary>
/// System tray icon using WinForms <see cref="NotifyIcon"/> - event-driven, so
/// (unlike the previous render-loop tray) it can never freeze. Left-click toggles
/// the lock; right-click opens the menu with a single contextual Lock/Unlock item.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _ni;
    private readonly ToolStripMenuItem _toggle;

    public event Action? ToggleRequested;
    public event Action? SettingsRequested;
    public event Action? OpenConfigRequested;
    public event Action? RestartAsAdminRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        _toggle = new ToolStripMenuItem("Lock now", null, (_, _) => ToggleRequested?.Invoke());
        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke());
        var openCfg = new ToolStripMenuItem("Open config file", null, (_, _) => OpenConfigRequested?.Invoke());
        var quit = new ToolStripMenuItem("Quit", null, (_, _) => QuitRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settings);
        menu.Items.Add(openCfg);
        // Only offer elevation when we're not already elevated. Needed for the
        // Win+L block on managed PCs (the DisableLockWorkstation policy key needs admin).
        if (!Core.Elevation.IsElevated())
            menu.Items.Add(new ToolStripMenuItem("Restart as administrator", null,
                (_, _) => RestartAsAdminRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);

        _ni = new NotifyIcon
        {
            Icon = IconFactory.Paw(false),
            Text = "Pawse - unlocked",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _ni.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleRequested?.Invoke();
        };

        Core.Log.Info("tray icon created");
    }

    public void SetLocked(bool locked)
    {
        _toggle.Text = locked ? "Unlock" : "Lock now";
        _ni.Text = locked ? "Pawse - locked" : "Pawse - unlocked";
        _ni.Icon = IconFactory.Paw(locked);
        Core.Log.Info($"tray set_locked({locked})");
    }

    public void Notify(string title, string body)
    {
        try
        {
            _ni.BalloonTipTitle = title;
            _ni.BalloonTipText = body;
            _ni.ShowBalloonTip(2500);
        }
        catch (Exception ex) { Core.Log.Error("tray notify", ex); }
    }

    public void Dispose()
    {
        _ni.Visible = false;
        _ni.Dispose();
    }
}
