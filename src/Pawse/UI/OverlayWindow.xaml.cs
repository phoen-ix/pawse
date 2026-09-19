using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Pawse.Core;

namespace Pawse.UI;

/// <summary>
/// The floating lock popup: a small, borderless, transparent, always-on-top
/// window shown on a chosen monitor while locked. The desktop stays visible
/// around it. Never steals focus (<c>ShowActivated=False</c> + SWP_NOACTIVATE).
///
/// Two looks, chosen by <see cref="Config.OverlayCfg.Style"/>: the card with its
/// hold-to-unlock button, or the tray paw drawn large with an optional hint box
/// beneath - held to unlock in exactly the same way.
///
/// All unlock KEY input arrives through the global hook (the keyboard is
/// swallowed, so no control here can get focus); this window only provides the
/// optional mouse hold-to-unlock and the visual state.
/// </summary>
public partial class OverlayWindow : Window
{
    public event Action? UnlockByHold;

    /// <summary>The card's fixed size; the paw layout sizes itself to its content instead.</summary>
    private const double CardWidth = 460, CardHeight = 240;

    /// <summary>Side of the paw's tile in the icon's 256-unit design space (IconFactory draws
    /// it at 12,12 232×232). The green fill's clip is expressed in the tile's own coordinates.</summary>
    private const double PawTile = 232;

    private Config _cfg = new();
    private readonly DispatcherTimer _holdTimer;
    private DateTime _holdStart;
    private bool _holding;

    /// <summary>Mouse-hold is on AND the mouse is not blocked. With mouse blocking on, the
    /// hook swallows clicks before WPF sees them - offering a hold then would be advertising
    /// a control that cannot work (and teaching the user that Pawse is broken).</summary>
    private bool _holdUsable;

    public OverlayWindow()
    {
        InitializeComponent();
        BuildPawBeans();

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _holdTimer.Tick += HoldTick;

        // The card's button and the paw are the same gesture on two surfaces.
        foreach (UIElement surface in new UIElement[] { HoldButton, PawButton })
        {
            surface.PreviewMouseLeftButtonDown += (_, _) => StartHold();
            surface.PreviewMouseLeftButtonUp += (_, _) => CancelHold();
            surface.MouseLeave += (_, _) => CancelHold();
        }
    }

    /// <summary>Which display this window sits on. One window per selected display, so the
    /// target lives here rather than being read out of the config - the config holds the whole
    /// set, and no single window owns it.</summary>
    public int TargetDisplay { get; set; }

    /// <summary>The five beans, from the table the tray icon is drawn from, placed in the
    /// same 256-unit space with the same scale about the centre.</summary>
    private void BuildPawBeans()
    {
        const float cx = 128f, cy = 128f, s = IconFactory.PawScale;
        var cream = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(255, 248, 240));
        cream.Freeze();
        foreach (var (dx, dy, rx, ry) in IconFactory.Beans)
        {
            var bean = new System.Windows.Shapes.Ellipse
            {
                Width = rx * 2 * s,
                Height = ry * 2 * s,
                Fill = cream,
            };
            System.Windows.Controls.Canvas.SetLeft(bean, cx + (dx - rx) * s);
            System.Windows.Controls.Canvas.SetTop(bean, cy + (dy - ry) * s);
            PawBeans.Children.Add(bean);
        }
    }

    public void Configure(Config cfg)
    {
        _cfg = cfg;
        Opacity = Math.Clamp(cfg.Overlay.Opacity, Config.OverlayCfg.MinOpacity, 1.0);
        _holdUsable = cfg.Unlock.MouseHold.Enabled && !cfg.General.BlockMouse;
        // Same guard as App.StartAutoUnlock: a hand-edited Seconds <= 0 arms no timer,
        // so advertising "Auto-unlocks after 0s" would promise an unlock that never comes.
        bool timerUsable = cfg.Unlock.Timer.Enabled && cfg.Unlock.Timer.Seconds > 0;
        string autoText = timerUsable ? $"Auto-unlocks after {cfg.Unlock.Timer.Seconds}s" : "";
        bool paw = cfg.Overlay.StyleValue == Config.OverlayStyle.Paw;

        // A layout switch mid-hold must not leave the other layout's fill half-way.
        CancelHold();
        CardRoot.Visibility = paw ? Visibility.Collapsed : Visibility.Visible;
        PawRoot.Visibility = paw ? Visibility.Visible : Visibility.Collapsed;

        if (paw)
        {
            PawButton.Width = PawButton.Height = cfg.Overlay.Paw.Size;
            PawButton.Cursor = _holdUsable
                ? System.Windows.Input.Cursors.Hand
                : System.Windows.Input.Cursors.Arrow;
            PawHintBox.Visibility = cfg.Overlay.Paw.ShowHint ? Visibility.Visible : Visibility.Collapsed;
            PawHintText.Text = BuildHint(cfg, pawStyle: true);
            PawAutoUnlockText.Visibility = timerUsable ? Visibility.Visible : Visibility.Collapsed;
            PawAutoUnlockText.Text = autoText;
            // Sized to content, unlike the card: the paw and its hint box decide. Measure with
            // no constraint and take what they ask for; PositionOnMonitor reads Width/Height.
            PawRoot.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Width = Math.Ceiling(PawRoot.DesiredSize.Width);
            Height = Math.Ceiling(PawRoot.DesiredSize.Height);
        }
        else
        {
            HintText.Text = BuildHint(cfg, pawStyle: false);
            HoldArea.Visibility = _holdUsable ? Visibility.Visible : Visibility.Collapsed;
            AutoUnlockText.Visibility = timerUsable ? Visibility.Visible : Visibility.Collapsed;
            AutoUnlockText.Text = autoText;
            Width = CardWidth;
            Height = CardHeight;
        }
    }

    private static string BuildHint(Config cfg, bool pawStyle)
    {
        // Every method named here must actually work in the current config - a hint
        // pointing at a dead unlock path is worse than none (the user trusts it and
        // concludes Pawse is broken, or worse, feels locked out).
        var parts = new List<string>();
        if (cfg.Unlock.Chord.Enabled && cfg.Unlock.Chord.Keys.Count > 0)
            parts.Add($"press {Keys.ChordToText(cfg.Unlock.Chord.Keys)}");
        // Only advertise the lockphrase if every character can register through the hook
        // (a-z, 0-9, space). Whether to print the phrase itself is the user's call
        // (Overlay.ShowLockphrase): Pawse is a cat lock, and the tray already unlocks for
        // any human, so the lockout worth guarding against is a phrase the user cannot
        // recall - but someone sharing their screen can keep it to "type your lockphrase".
        if (cfg.Unlock.Lockphrase.Enabled && Keys.IsTypeableLockphrase(cfg.Unlock.Lockphrase.Text))
        {
            parts.Add(cfg.Overlay.ShowLockphrase
                ? $"type “{cfg.Unlock.Lockphrase.Text}”"
                : "type your lockphrase");
        }
        if (cfg.Unlock.MouseHold.Enabled && !cfg.General.BlockMouse)
            parts.Add(pawStyle ? "hold the paw" : "hold the button below");
        if (parts.Count > 0)
            return "To unlock: " + string.Join(", or ", parts) + ".";
        return cfg.Unlock.Timer.Enabled && cfg.Unlock.Timer.Seconds > 0
            ? "Unlocks automatically."
            : "To unlock: use the tray icon.";
    }

    public void ShowLocked()
    {
        Configure(_cfg);
        if (!IsVisible) Show();
        PositionOnMonitor();
        Topmost = true;
    }

    public void HideLocked()
    {
        CancelHold();
        if (IsVisible) Hide();
    }

    private void StartHold()
    {
        if (!_holdUsable) return;
        _holding = true;
        _holdStart = DateTime.UtcNow;
        _holdTimer.Start();
    }

    private void CancelHold()
    {
        _holding = false;
        _holdTimer.Stop();
        HoldFill.Width = 0;
        PawFillClip.Rect = new Rect(0, PawTile, PawTile, 0);
    }

    private void HoldTick(object? sender, EventArgs e)
    {
        if (!_holding) return;
        double elapsed = (DateTime.UtcNow - _holdStart).TotalMilliseconds;
        double need = Math.Max(1, _cfg.Unlock.MouseHold.HoldMs);
        double frac = Math.Min(1.0, elapsed / need);
        // Both fills advance; only the visible layout's shows. The bar grows to the right,
        // the paw's green rises from the bottom.
        HoldFill.Width = HoldArea.ActualWidth * frac;
        PawFillClip.Rect = new Rect(0, PawTile * (1 - frac), PawTile, PawTile * frac);
        if (frac >= 1.0)
        {
            CancelHold();
            UnlockByHold?.Invoke();
        }
    }

    /// <summary>Place on the chosen monitor at the configured horizontal and vertical
    /// fractions of the room left over (0 = left/top edge, 100 = right/bottom, 50 = centred),
    /// using physical-pixel coordinates so multi-DPI setups land correctly.</summary>
    private void PositionOnMonitor()
    {
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (screens.Length == 0) return;

            int idx = Math.Clamp(TargetDisplay, 0, screens.Length - 1);
            var b = screens[idx].Bounds; // physical pixels (per-monitor-v2 aware)

            double scale = 1.0;
            var center = new NativeMethods.POINT { x = b.Left + b.Width / 2, y = b.Top + b.Height / 2 };
            IntPtr mon = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero &&
                NativeMethods.GetDpiForMonitor(mon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
                dpiX > 0)
            {
                scale = dpiX / 96.0;
            }

            int w = (int)Math.Round(Width * scale);
            int h = (int)Math.Round(Height * scale);
            double xf = Math.Clamp(_cfg.Overlay.HorizontalPercent, 0, 100) / 100.0;
            double yf = Math.Clamp(_cfg.Overlay.VerticalPercent, 0, 100) / 100.0;
            int x = b.Left + (int)Math.Round((b.Width - w) * xf);
            int y = b.Top + (int)Math.Round((b.Height - h) * yf);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
        }
        catch (Exception ex)
        {
            Log.Error("overlay positioning", ex);
        }
    }

    /// <summary>Set by App just before a real shutdown so <see cref="OnClosing"/> lets the
    /// window actually close; otherwise a stray Alt+F4 only hides it (App owns the lifecycle).</summary>
    public bool AllowClose { get; set; }

    // Closing via Alt+F4 etc. should just HIDE, not destroy the window - a destroyed window
    // would throw on the next ShowLocked/Configure call from OnLockedChanged.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HideLocked();
        }
        base.OnClosing(e);
    }
}
