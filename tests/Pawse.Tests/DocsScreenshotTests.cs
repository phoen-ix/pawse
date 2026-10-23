using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pawse.Core;
using Pawse.UI;
using Xunit;
using TabControl = System.Windows.Controls.TabControl;

namespace Pawse.Tests;

/// <summary>
/// The docs screenshot tour of the app's own windows (docs/screenshots/app-*.png): each window
/// is built from a fixture config, rendered off its visual tree at 2x (RenderTargetBitmap - no
/// title bar, nothing from the desktop behind it) and saved as a PNG. Each test asserts the
/// state it is about to shoot first, so the tour doubles as a render check.
/// <para>Opt-in: skipped unless DOCS_SCREENSHOT_DIR names the output folder. Windows only, like
/// the rest of the suite. Run through scripts/docs-screenshots.sh, which docs-screenshots.yml
/// (the PNGs to commit) and ci.yml's render check call.</para>
/// </summary>
[Trait("Category", "Docs")]
public class DocsScreenshotTests
{
    private static readonly string? OutDir = Environment.GetEnvironmentVariable("DOCS_SCREENSHOT_DIR");

    /// <summary>Lockphrase on as well as the defaults, so the popup's hint names three ways out.</summary>
    private static Config Fixture()
    {
        var c = new Config();
        c.Unlock.Lockphrase.Enabled = true;
        c.Unlock.Lockphrase.Text = "whiskers";
        return c;
    }

    [Fact]
    public void Settings_pages() => OnSta(() =>
    {
        var w = Settings(DeploymentMode.Installed, locked: false);
        string[] pages = { "general", "locking", "unlocking", "lock-popup", "about" };
        var tabs = Tabs(w);
        Assert.Equal(pages.Length, tabs.Items.Count);
        for (int i = 0; i < pages.Length; i++)
        {
            tabs.SelectedIndex = i;
            Shoot(w, "app-settings-" + pages[i]);
        }

        tabs.SelectedIndex = 3;
        w.RbStylePaw.IsChecked = true;
        Assert.Equal(Visibility.Visible, w.PnlPawOptions.Visibility);
        Shoot(w, "app-settings-lock-popup-paw");
        w.Close();
    });

    [Fact]
    public void Settings_while_locked() => OnSta(() =>
    {
        var w = Settings(DeploymentMode.Installed, locked: true);
        Tabs(w).SelectedIndex = 2;
        Assert.Equal(Visibility.Visible, w.PnlLockedBanner.Visibility);
        Assert.False(w.TxtLockphrase.IsEnabled);
        Shoot(w, "app-settings-locked");
        w.Close();
    });

    [Fact]
    public void Settings_portable() => OnSta(() =>
    {
        var w = Settings(DeploymentMode.Portable, locked: false);
        Assert.False(w.ChkAutostart.IsEnabled);
        Assert.Equal(Visibility.Visible, w.LblAutostartPortable.Visibility);
        Shoot(w, "app-settings-portable");
        w.Close();
    });

    [Fact]
    public void Lock_popup() => OnSta(() =>
    {
        var cfg = Fixture();
        var w = new OverlayWindow();
        w.Configure(cfg);
        Show(w);
        Assert.Equal(Visibility.Visible, w.HoldArea.Visibility);
        Assert.Equal("To unlock: press Ctrl+L, or type “whiskers”, or hold the button below.", w.HintText.Text);
        Shoot(w, "app-lock-popup");

        cfg.Overlay.StyleValue = Config.OverlayStyle.Paw;
        w.Configure(cfg);
        Assert.Equal(Visibility.Visible, w.PawRoot.Visibility);
        Assert.Equal(Visibility.Visible, w.PawHintBox.Visibility);
        Shoot(w, "app-lock-popup-paw");
        w.AllowClose = true;
        w.Close();
    });

    [Fact]
    public void Shortcut_recorder() => OnSta(() =>
    {
        var controller = new LockController(Fixture());
        var w = new ShortcutRecorderWindow(controller);
        Show(w);
        // Three keys held, not yet released: the recorder shows what it has so far. Fed
        // through the hook's capture sink, the path real keys take.
        foreach (int vk in new[] { 0x11, 0x10, 'U' }) controller.CaptureSink!(vk, true);
        Flush();
        Assert.Equal("Ctrl+Shift+U", w.LblChord.Text);
        Shoot(w, "app-shortcut-recorder");
        w.Close();
    });

    [Fact]
    public void Tray_menu() => OnSta(() =>
    {
        using var tray = new TrayIcon();
        var field = typeof(TrayIcon).GetField("_ni", BindingFlags.NonPublic | BindingFlags.Instance);
        var menu = ((System.Windows.Forms.NotifyIcon)field!.GetValue(tray)!).ContextMenuStrip!;
        menu.Show(-10000, -10000);
        Flush();
        Assert.Equal("Lock now", menu.Items[0].Text);
        // WinForms draws through GDI at the screen's own DPI, so this one is 1x.
        using var bmp = new System.Drawing.Bitmap(menu.Width, menu.Height);
        menu.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, menu.Width, menu.Height));
        menu.Close();
        bmp.Save(Path.Combine(OutDir!, "app-tray-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
    });

    // ---- helpers ----

    private static SettingsWindow Settings(DeploymentMode mode, bool locked)
    {
        // The test host is "portable" by Deployment's rules (no installer entry names its
        // folder); the docs mostly show an installed copy, so set the cached answer.
        var field = typeof(Deployment).GetField("_mode", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        field.SetValue(null, (DeploymentMode?)mode);

        var w = new SettingsWindow(Fixture(), () => locked, _ => null);
        // The constructor shrinks to the work area; the runner's screen is small.
        w.Width = 800;
        w.Height = 680;
        Show(w);
        return w;
    }

    /// <summary>Off every screen, so the pointer cannot hover anything in the shot.</summary>
    private static void Show(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = w.Top = -10000;
        w.Show();
    }

    private static TabControl Tabs(SettingsWindow w) =>
        ((System.Windows.Controls.Grid)w.Content).Children.OfType<TabControl>().Single();

    /// <summary>The window's client area: its template root, which also paints the Background.</summary>
    private static void Shoot(Window w, string name)
    {
        Flush();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 2),
                                         (int)Math.Ceiling(root.ActualHeight * 2),
                                         192, 192, PixelFormats.Pbgra32);
        bmp.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(Path.Combine(OutDir!, name + ".png"));
        png.Save(file);
    }

    /// <summary>Run everything queued down to idle - layout, render, BeginInvoke'd handlers.</summary>
    private static void Flush()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>WPF needs an STA thread with its own dispatcher; xunit's are MTA.</summary>
    private static void OnSta(Action body)
    {
        Assert.SkipWhen(OutDir is null, "DOCS_SCREENSHOT_DIR is not set (scripts/docs-screenshots.sh sets it)");
        Directory.CreateDirectory(OutDir!);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
