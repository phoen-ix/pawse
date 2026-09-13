using Timer = System.Windows.Forms.Timer; // implicit usings also bring System.Threading.Timer

namespace Pawse.UI;

/// <summary>
/// System tray icon using WinForms <see cref="NotifyIcon"/> - event-driven, so it
/// can never freeze. Left-click locks;
/// while locked a DOUBLE-click unlocks and a lone click only shows a hint, so a
/// stray paw-click on the tray can't undo the lock with zero friction (the user
/// can switch back to the classic single-click toggle, see
/// <see cref="DoubleClickUnlock"/>). Right-click opens the menu with a single
/// contextual Lock/Unlock item.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _ni;
    private readonly ToolStripMenuItem _toggle;
    private readonly Timer _clickTimer;
    private bool _locked;
    private bool _lockedAtLastClick;
    private bool _ignoreNextUp;
    // Captured at construction (the WPF UI thread) so Notify can be called from any
    // thread - SystemBlock raises it from a worker task, and NotifyIcon is not
    // thread-safe: an off-thread balloon silently never appears.
    private readonly System.Windows.Threading.Dispatcher _dispatcher =
        System.Windows.Threading.Dispatcher.CurrentDispatcher;

    /// <summary>Mirrors Config.General.TrayDoubleClickUnlock (App keeps it in sync).
    /// True: unlocking needs a double-click, a lone click while locked shows a hint.
    /// False: classic single-click toggle in both directions.</summary>
    public bool DoubleClickUnlock { get; set; } = true;

    public event Action? ToggleRequested;
    public event Action? SettingsRequested;
    public event Action? OpenConfigRequested;
    public event Action? RestartAsAdminRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        _toggle = new ToolStripMenuItem("Lock now", null, (_, _) => Post(() => ToggleRequested?.Invoke()));
        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => Post(() => SettingsRequested?.Invoke()));
        var openCfg = new ToolStripMenuItem("Open config file", null, (_, _) => Post(() => OpenConfigRequested?.Invoke()));
        var quit = new ToolStripMenuItem("Quit", null, (_, _) => Post(() => QuitRequested?.Invoke()));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settings);
        menu.Items.Add(openCfg);
        // Only offer elevation when we're not already elevated. Needed for the Win+L block
        // on managed PCs and for the browser/calculator/media-key block (Keyboard Filter).
        if (!Core.Elevation.IsElevated())
            menu.Items.Add(new ToolStripMenuItem("Restart as administrator", null,
                (_, _) => Post(() => RestartAsAdminRequested?.Invoke())));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);

        _ni = new NotifyIcon
        {
            Icon = IconFactory.Paw(false),
            Text = "Pawse - unlocked",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // The hint fires only after the double-click window has passed, so it never
        // flashes during a real double-click unlock.
        _clickTimer = new Timer { Interval = SystemInformation.DoubleClickTime + 100 };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            // An unlock (chord, hold, timer) can land inside the double-click window -
            // don't claim "Locked" when it no longer is.
            if (_locked) Notify("Pawse", "Locked - double-click the paw to unlock.");
        };
        _ni.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_ignoreNextUp) { _ignoreNextUp = false; return; }
            _lockedAtLastClick = _locked;
            if (!_locked || !DoubleClickUnlock)
                ToggleRequested?.Invoke(); // locking is always one click; classic mode unlocks on one too
            else
                _clickTimer.Start();       // guarded mode: unlocking needs the double-click
        };
        _ni.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _clickTimer.Stop();
            // The second click's MouseUp still fires after this - swallow it, or a
            // double-click would toggle a second time right after acting here.
            _ignoreNextUp = true;
            // Only unlock if the lock predates the first click of this pair - otherwise
            // a fast double-click while unlocked would lock and instantly unlock again.
            // (In classic mode the first click already unlocked - nothing to do here.)
            if (_lockedAtLastClick && DoubleClickUnlock) ToggleRequested?.Invoke();
        };

        Core.Log.Info("tray icon created");
    }

    /// <summary>Raise a menu item's event after the click handler has returned, rather than
    /// inside it.
    /// <para>A <see cref="ContextMenuStrip"/> shown by <see cref="NotifyIcon"/> puts WinForms
    /// into ToolStrip "menu mode", which normally unwinds through an
    /// <c>Application.AddMessageFilter</c> filter - and Pawse pumps messages with WPF's
    /// dispatcher, which never runs those. Opening a window from inside that handler is the
    /// worst moment for it: the window shows but does not become foreground, so it takes
    /// mouse input while every keystroke lands elsewhere. One turn of the dispatcher lets the
    /// menu finish closing first.</para>
    /// <para>Background, not ApplicationIdle: it still runs after the input and render work
    /// queued by the menu closing, but it sits ABOVE the idle priorities, so a DispatcherTimer
    /// (auto-unlock, the daily update check) cannot starve a menu click indefinitely.</para>
    /// <para>The tray CLICK handlers below stay synchronous - no menu is involved there and
    /// the double-click guard's timing depends on them firing at once.</para></summary>
    private void Post(Action raise) =>
        _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, raise);

    public void SetLocked(bool locked)
    {
        _locked = locked;
        _toggle.Text = locked ? "Unlock" : "Lock now";
        _ni.Text = locked ? "Pawse - locked" : "Pawse - unlocked";
        _ni.Icon = IconFactory.Paw(locked);
        Core.Log.Info($"tray set_locked({locked})");
    }

    public void Notify(string title, string body)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => Notify(title, body));
            return;
        }
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
        _clickTimer.Dispose();
        _ni.Visible = false;
        var menu = _ni.ContextMenuStrip;
        _ni.Dispose();
        // NotifyIcon.Dispose does not dispose its menu. The icons are NOT disposed
        // here on purpose - IconFactory hands out shared, process-lifetime caches.
        menu?.Dispose();
    }
}
