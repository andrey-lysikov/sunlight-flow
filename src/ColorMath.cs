//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using Color = Windows.UI.Color;

namespace SunlightFlow;

// Brightness rides on the colour itself rather than BrightnessLevel: without
// hardware dimming the latter gives a coarse, visibly stepped ramp.
internal static class ColorMath
{
    // Shown while Dynamic Lighting is off in Windows, and the load colour of last resort.
    public static readonly Color Default = Rgb(0x0078FF);
    public static readonly Color DefaultLoad = Rgb(0xFF3000);

    // Hue range of the base, by its upper bound in degrees -> colour at full load.
    // Meant to read as a warning against the base, not as its strict complement.
    private static readonly (double UpTo, Color Load)[] LoadColors =
    [
        (5, Rgb(0xFFD000)),   // red                -> yellow
        (19, Rgb(0xFFD000)),  // red-orange         -> yellow
        (40, Rgb(0xFF0000)),  // orange             -> red
        (50, Rgb(0xFF2000)),  // amber              -> red
        (70, Rgb(0xFF6000)),  // yellow             -> orange
        (100, Rgb(0xFF2000)), // lime, olive        -> red
        (155, Rgb(0xFF8000)), // green              -> orange
        (180, Rgb(0xFF4500)), // teal               -> orange-red
        (200, Rgb(0xFF3000)), // cyan               -> red
        (230, Rgb(0xFF3000)), // blue               -> red
        (255, Rgb(0xFF4000)), // indigo             -> red-orange
        (285, Rgb(0xFF8000)), // purple             -> orange
        (315, Rgb(0xFFC000)), // violet, magenta    -> yellow
        (345, Rgb(0xFFC000)), // pink, crimson      -> yellow
        (360, Rgb(0xFFD000)), // red                -> yellow
    ];

    // Below this saturation a colour reads as grey, and red stands out on grey.
    private const double GreyBelow = 0.18;

    public static Color LoadColorFor(Color baseColor)
    {
        int max = Math.Max(baseColor.R, Math.Max(baseColor.G, baseColor.B));
        int min = Math.Min(baseColor.R, Math.Min(baseColor.G, baseColor.B));
        if (max == 0 || (max - min) / (double)max < GreyBelow) return DefaultLoad;

        double hue = Hue(baseColor);
        return LoadColors.First(row => hue < row.UpTo).Load;
    }

    // Hue in degrees, 0..360.
    public static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (delta <= 0) return 0;

        double h = max == r ? (g - b) / delta % 6
                 : max == g ? (b - r) / delta + 2
                 : (r - g) / delta + 4;

        return (h * 60 + 360) % 360;
    }

    // Fully saturated, full-value colour of the given hue.
    public static Color FromHue(double hue)
    {
        double h = (hue % 360 + 360) % 360 / 60;
        double x = 1 - Math.Abs(h % 2 - 1);

        (double r, double g, double b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x)
        };

        return Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    public static Color Mix(Color a, Color b, double k)
    {
        k = Math.Clamp(k, 0.0, 1.0);
        return Color.FromArgb(255,
            (byte)(a.R + (b.R - a.R) * k),
            (byte)(a.G + (b.G - a.G) * k),
            (byte)(a.B + (b.B - a.B) * k));
    }

    public static Color Scale(Color c, double k)
    {
        k = Math.Clamp(k, 0.0, 1.0);
        return Color.FromArgb(255, (byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color Rgb(uint rgb) =>
        Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
