using System.Drawing;
using System.Drawing.Drawing2D;

namespace Pawse.UI;

/// <summary>Draws the tray padlock icon at runtime (green = unlocked, red = locked)
/// so there's no binary asset to ship. The two icons are cached for the app's life.</summary>
internal static class IconFactory
{
    private static Icon? _locked;
    private static Icon? _unlocked;

    public static Icon Padlock(bool locked)
    {
        if (locked) return _locked ??= Build(true);
        return _unlocked ??= Build(false);
    }

    private static Icon Build(bool locked)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color body = locked ? Color.FromArgb(224, 72, 72) : Color.FromArgb(76, 175, 96);

            using (var shackle = new Pen(Color.FromArgb(235, 235, 235), 3f))
                g.DrawArc(shackle, 9, 5, 14, 16, 180, 180);

            using (var bodyBrush = new SolidBrush(body))
                g.FillRectangle(bodyBrush, 7, 14, 18, 13);

            using (var hole = new SolidBrush(Color.FromArgb(30, 30, 30)))
                g.FillEllipse(hole, 14, 17, 4, 4);
        }

        // GetHicon leaks the HICON, but there are only ever two, cached for life.
        return Icon.FromHandle(bmp.GetHicon());
    }
}
