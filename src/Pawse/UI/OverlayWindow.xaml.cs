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
/// All unlock KEY input arrives through the global hook (the keyboard is
/// swallowed, so no control here can get focus); this window only provides the
/// optional mouse hold-to-unlock and the visual state.
/// </summary>
public partial class OverlayWindow : Window
{
    public event Action? UnlockByHold;

    private Config _cfg = new();
    private readonly DispatcherTimer _holdTimer;
    private DateTime _holdStart;
    private bool _holding;

    public OverlayWindow()
    {
        InitializeComponent();

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _holdTimer.Tick += HoldTick;

        HoldButton.PreviewMouseLeftButtonDown += (_, _) => StartHold();
        HoldButton.PreviewMouseLeftButtonUp += (_, _) => CancelHold();
        HoldButton.MouseLeave += (_, _) => CancelHold();
    }

    public void Configure(Config cfg)
    {
        _cfg = cfg;
        Opacity = Math.Clamp(cfg.Overlay.Opacity, 0.2, 1.0);
        HintText.Text = BuildHint(cfg);
        HoldArea.Visibility = cfg.Unlock.MouseHold.Enabled ? Visibility.Visible : Visibility.Collapsed;
        CountdownText.Visibility = cfg.Unlock.Timer.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (cfg.Unlock.Timer.Enabled)
            CountdownText.Text = $"Auto-unlocks after {cfg.Unlock.Timer.Seconds}s";
    }

    private static string BuildHint(Config cfg)
    {
        var parts = new List<string>();
        if (cfg.Unlock.Chord.Enabled && cfg.Unlock.Chord.Keys.Count > 0)
            parts.Add($"press {Keys.ChordToText(cfg.Unlock.Chord.Keys)}");
        if (cfg.Unlock.Passphrase.Enabled && cfg.Unlock.Passphrase.Text.Length > 0)
            parts.Add($"type “{cfg.Unlock.Passphrase.Text}”");
        if (cfg.Unlock.MouseHold.Enabled)
            parts.Add("hold the button below");
        return parts.Count > 0
            ? "To unlock: " + string.Join(", or ", parts) + "."
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
        if (!_cfg.Unlock.MouseHold.Enabled) return;
        _holding = true;
        _holdStart = DateTime.UtcNow;
        _holdTimer.Start();
    }

    private void CancelHold()
    {
        _holding = false;
        _holdTimer.Stop();
        HoldFill.Width = 0;
    }

    private void HoldTick(object? sender, EventArgs e)
    {
        if (!_holding) return;
        double elapsed = (DateTime.UtcNow - _holdStart).TotalMilliseconds;
        double need = Math.Max(1, _cfg.Unlock.MouseHold.HoldMs);
        double frac = Math.Min(1.0, elapsed / need);
        HoldFill.Width = HoldArea.ActualWidth * frac;
        if (frac >= 1.0)
        {
            CancelHold();
            UnlockByHold?.Invoke();
        }
    }

    /// <summary>Center on the chosen monitor at the configured vertical fraction,
    /// using physical-pixel coordinates so multi-DPI setups land correctly.</summary>
    private void PositionOnMonitor()
    {
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (screens.Length == 0) return;

            int idx = Math.Clamp(_cfg.Overlay.Monitor, 0, screens.Length - 1);
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
            int x = b.Left + (b.Width - w) / 2;
            double yf = Math.Clamp(_cfg.Overlay.VerticalPercent, 0, 100) / 100.0;
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

    // Closing via Alt+F4 etc. should just hide (the app owns the lifecycle).
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
    }
}
