using System.Drawing;
using System.Drawing.Drawing2D;

namespace Pawse.UI;

/// <summary>Draws the tray paw icon at runtime (green = unlocked, red = locked) so
/// there's no binary asset to ship. Matches the paw in packaging/pawse.ico. The two
/// icons are cached for the app's life.</summary>
internal static class IconFactory
{
    private static Icon? _locked;
    private static Icon? _unlocked;

    public static Icon Paw(bool locked)
    {
        if (locked) return _locked ??= Build(true);
        return _unlocked ??= Build(false);
    }

    // Paw geometry in a 256-unit design space (same as packaging/pawse.ico): four toe
    // beans + a heel pad, each an ellipse given as (dx, dy, rx, ry) offsets from centre.
    private static readonly (float dx, float dy, float rx, float ry)[] Beans =
    {
        (-46, -26, 13, 17), (-16, -47, 14, 19), (16, -47, 14, 19), (46, -26, 13, 17),
        (0, 34, 46, 37),
    };

    private static Icon Build(bool locked)
    {
        const int S = 32;
        const float k = S / 256f;
        using var bmp = new Bitmap(S, S);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Rounded-square tile: green when unlocked, red when locked.
            Color bg = locked ? Color.FromArgb(224, 72, 72) : Color.FromArgb(76, 175, 96);
            using (var path = RoundedRect(12 * k, 12 * k, 232 * k, 232 * k, 52 * k))
            using (var bgBrush = new SolidBrush(bg))
                g.FillPath(bgBrush, path);

            // Cream paw, centred, scaled to fill the tile.
            const float cx = 128f, cy = 128f, s = 1.25f;
            using var paw = new SolidBrush(Color.FromArgb(255, 248, 240));
            foreach (var (dx, dy, rx, ry) in Beans)
                g.FillEllipse(paw,
                    (cx + (dx - rx) * s) * k, (cy + (dy - ry) * s) * k,
                    rx * 2 * s * k, ry * 2 * s * k);
        }

        // GetHicon leaks the HICON, but there are only ever two, cached for life.
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        float d = r * 2;
        var p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
