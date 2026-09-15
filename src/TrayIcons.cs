//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SunlightFlow;

// Draws the tray icon instead of shipping bitmaps: it has to follow the taskbar
// theme, the current scaling, and show the sun or the moon with its phase.
internal sealed class TrayIcons : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly record struct Key(int Size, bool Light, int Fill, int Phase, bool Sun, bool Active);

    private readonly Dictionary<Key, Icon> _cache = [];
    private readonly Lock _lock = new();

    // Taskbar colour, not app colour: a light taskbar needs a dark glyph.
    public static bool LightTaskbar
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") as int? == 1;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <param name="daylight">0 at night, 1 with the sun well up</param>
    /// <param name="active">false greys the glyph down for the off state</param>
    public Icon Get(double daylight, bool sunUp, bool active)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        double phase = Moon.Phase(DateTime.UtcNow);

        // Quantised so a slow drift does not rebuild the icon every frame.
        var key = new Key(size, LightTaskbar,
                          (int)Math.Round(Math.Clamp(daylight, 0, 1) * 8),
                          (int)Math.Round(phase * 16), sunUp, active);

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var icon = Render(key, phase);
            _cache[key] = icon;
            return icon;
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            foreach (var icon in _cache.Values) Release(icon);
            _cache.Clear();
        }
    }

    // Drawn four times larger and scaled down, or at 16 px a diagonal ray looks
    // thicker than a straight one.
    private const int Supersample = 4;

    private static Icon Render(Key key, double phase)
    {
        int large = key.Size * Supersample;

        int baseTone = key.Light ? 0 : 255;
        int alpha = key.Active ? 255 : 110;
        var bright = Color.FromArgb(alpha, baseTone, baseTone, baseTone);
        var faint = Color.FromArgb(alpha * 40 / 100, baseTone, baseTone, baseTone);

        using var canvas = new Bitmap(large, large, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            if (key.Sun) DrawSun(g, large, key.Fill / 8.0, bright, faint);
            else DrawMoon(g, large, phase, bright, faint);
        }

        using var bitmap = new Bitmap(key.Size, key.Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(canvas, new Rectangle(0, 0, key.Size, key.Size));
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle does not own the handle, so a copy is made and the
            // original destroyed right away.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    // Rays keep one thickness and grow in length as the sun climbs, which reads
    // better at sixteen pixels than counting lit ones.
    private static void DrawSun(Graphics g, int size, double fill, Color bright, Color faint)
    {
        float centre = size / 2f;
        float coreRadius = size * 0.22f;
        float rayInner = size * 0.30f;
        float rayLongest = size * 0.48f;
        float stroke = Math.Max(1f, size / 12f);

        float rayOuter = rayInner + (rayLongest - rayInner) * (float)fill;

        const int rays = 8;
        if (rayOuter - rayInner > 0.4f)
        {
            using var pen = new Pen(bright, stroke * 0.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < rays; i++)
            {
                double angle = i * 2 * Math.PI / rays - Math.PI / 2;
                g.DrawLine(pen,
                    centre + (float)(rayInner * Math.Cos(angle)), centre + (float)(rayInner * Math.Sin(angle)),
                    centre + (float)(rayOuter * Math.Cos(angle)), centre + (float)(rayOuter * Math.Sin(angle)));
            }
        }

        var core = new RectangleF(centre - coreRadius, centre - coreRadius, coreRadius * 2, coreRadius * 2);
        using (var pen = new Pen(bright, stroke)) g.DrawEllipse(pen, core);

        if (fill > 0.01)
        {
            float inner = coreRadius * (float)fill;
            using var brush = new SolidBrush(bright);
            g.FillEllipse(brush, centre - inner, centre - inner, inner * 2, inner * 2);
        }
    }

    // The lit part is the disc minus a terminator ellipse whose width follows
    // the phase, which is what makes a crescent read as a crescent.
    private static void DrawMoon(Graphics g, int size, double phase, Color bright, Color faint)
    {
        float centre = size / 2f;
        float radius = size * 0.36f;
        float stroke = Math.Max(1f, size / 14f);

        var disc = new RectangleF(centre - radius, centre - radius, radius * 2, radius * 2);

        // Thin outline so a sliver of lit disc still reads as thicker than it.
        // At new moon the outline is all there is, so it stays full strength.
        using (var pen = new Pen(bright, stroke * 0.55f)) g.DrawEllipse(pen, disc);

        double illumination = Moon.Illumination(phase);
        if (illumination < 0.005) return;

        bool waxing = phase < 0.5;
        float terminator = (float)Math.Abs(Math.Cos(2 * Math.PI * phase)) * radius;

        // A one percent crescent is thinner than a pixel. Holding it to twice
        // the stroke keeps it distinct from the outline it sits on.
        terminator = Math.Min(terminator, radius - stroke * 2f);

        using var half = new GraphicsPath();
        half.AddArc(disc, waxing ? -90 : 90, 180);
        half.CloseFigure();

        using var oval = new GraphicsPath();
        oval.AddEllipse(centre - terminator, centre - radius, terminator * 2, radius * 2);

        // Below half the terminator ellipse bites into the lit half, above it
        // the ellipse adds to it. Regions keep that honest at any phase.
        using var lit = new Region(half);
        if (illumination < 0.5) lit.Exclude(oval);
        else lit.Union(oval);

        using var discRegion = new Region(disc);
        lit.Intersect(discRegion);

        using var brush = new SolidBrush(bright);
        g.FillRegion(brush, lit);
    }

    private static void Release(Icon icon)
    {
        try { icon.Dispose(); } catch { /* already gone */ }
    }

    public void Dispose() => Invalidate();
}
