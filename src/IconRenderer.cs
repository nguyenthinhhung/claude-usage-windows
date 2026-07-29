using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Draws the tray glyph: two concentric arcs, outer = weekly, inner = 5-hour session.
/// Rendered at 4x and downsampled so 16px arcs still read cleanly.
/// </summary>
public static class IconRenderer
{
    const int Supersample = 4;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    public static Icon Render(int size, UsageSnapshot? snapshot)
    {
        size = Math.Max(16, size);
        int hi = size * Supersample;

        using var big = new Bitmap(hi, hi);
        using (var g = Graphics.FromImage(big))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float stroke = hi * 0.145f;
            float gap = hi * 0.07f;

            // Outer arc: weekly. Inner arc: current 5-hour session.
            var outer = Inset(hi, stroke / 2 + hi * 0.025f);
            var inner = Inset(hi, stroke / 2 + hi * 0.025f + stroke + gap);

            if (snapshot is null)
            {
                DrawArc(g, outer, stroke, Theme.Dim(Theme.Unknown, 105), 100);
                DrawArc(g, inner, stroke, Theme.Dim(Theme.Unknown, 105), 100);
            }
            else
            {
                DrawRing(g, outer, stroke, snapshot.Weekly);
                DrawRing(g, inner, stroke, snapshot.Session);
            }
        }

        using var small = new Bitmap(size, size);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(big, new Rectangle(0, 0, size, size));
        }

        // GetHicon leaks unless we destroy the handle after cloning into a managed Icon.
        IntPtr handle = small.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    static void DrawRing(Graphics g, RectangleF rect, float stroke, LimitWindow limit)
    {
        // A window that rolled over since the last poll gets the same blank track as "no data",
        // because that is exactly what we have for the window now in effect.
        if (limit.IsExpired)
        {
            DrawArc(g, rect, stroke, Theme.Dim(Theme.Unknown, 105), 100);
            return;
        }

        var color = Theme.For(limit.Severity);
        // The unfilled track has to stay visible on a dark taskbar, otherwise a low
        // percentage reads as a stray fragment instead of a gauge.
        DrawArc(g, rect, stroke, Theme.Dim(color, 90), 100);
        if (limit.Percent > 0)
            DrawArc(g, rect, stroke, color, limit.Percent);
    }

    static void DrawArc(Graphics g, RectangleF rect, float stroke, Color color, double percent)
    {
        float sweep = (float)(Math.Clamp(percent, 0, 100) / 100.0 * 360.0);
        using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (sweep >= 359.9f)
            g.DrawEllipse(pen, rect);
        else
            g.DrawArc(pen, rect, -90f, sweep);
    }

    static RectangleF Inset(int size, float inset) =>
        new(inset, inset, size - inset * 2, size - inset * 2);
}
