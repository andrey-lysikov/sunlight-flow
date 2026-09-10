//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using Microsoft.Win32;

using Color = Windows.UI.Color;

namespace SunlightFlow;

internal sealed record LightingState(Color Color, double Brightness);

// What Windows was showing before we took the lamps, so the handover can start
// from that instead of snapping to our own colour.
internal static class WindowsLighting
{
    private const string KeyPath = @"Software\Microsoft\Lighting";

    public static LightingState? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return null;

            if (key.GetValue("AmbientLightingEnabled") as int? != 1) return null;

            int brightness = key.GetValue("Brightness") as int? ?? 0;
            if (brightness <= 0) return null;

            // Stored as a COLORREF, so the low byte is red.
            uint raw = unchecked((uint)(key.GetValue("Color") as int? ?? 0));
            var color = Color.FromArgb(255,
                (byte)(raw & 0xFF),
                (byte)((raw >> 8) & 0xFF),
                (byte)((raw >> 16) & 0xFF));

            var state = new LightingState(color, Math.Clamp(brightness / 100.0, 0, 1));
            Diagnostics.Log($"Windows was showing {ColorMath.ToHex(color)} at {brightness}%");
            return state;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"could not read the Windows lighting state: {e.Message}");
            return null;
        }
    }
}
