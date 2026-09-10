//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using Color = Windows.UI.Color;

namespace SunlightFlow;

// Brightness rides on the colour itself rather than BrightnessLevel: without
// hardware dimming the latter gives a coarse, visibly stepped ramp.
internal static class ColorMath
{
    public const string DefaultHex = "#0078FF";
    public const string DefaultBusyHex = "#FF3000";

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

    public static bool IsValid(string? hex) => TryParse(hex, out _);

    public static Color Parse(string? hex) =>
        TryParse(hex, out var c) ? c : Parse(DefaultHex);

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (hex is null) return false;

        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return false;

        try
        {
            color = Color.FromArgb(255,
                Convert.ToByte(s[..2], 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
