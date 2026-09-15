//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using Microsoft.Win32;
using Windows.UI.ViewManagement;

using Color = Windows.UI.Color;

namespace SunlightFlow;

internal sealed record LightingState(Color Color, double Brightness);

// Values as the Settings page stores them in EffectType. Undocumented, mapped by
// walking through the list: 3 is skipped.
internal enum WindowsEffectType { Solid = 0, Breathing = 1, Rainbow = 2, Wave = 4, Wheel = 5, Gradient = 6 }

// The effect picked in Settings > Dynamic Lighting. Speed runs 1..10; Mode is
// the index of the direction in the list the Settings page shows for the effect.
internal sealed record WindowsEffect(WindowsEffectType Type, Color Color, Color Color2, int Speed, int Mode)
{
    public static WindowsEffect Solid(Color color) => new(WindowsEffectType.Solid, color, color, 5, 0);

    public bool IsAnimated => Type is not (WindowsEffectType.Solid or WindowsEffectType.Gradient);

    // Under load these tint towards the load colour; gradient and breathing pulse faster instead.
    public bool TintsUnderLoad => Type is WindowsEffectType.Solid or WindowsEffectType.Rainbow
        or WindowsEffectType.Wave or WindowsEffectType.Wheel;

    public override string ToString() =>
        $"{Type} {ColorMath.ToHex(Color)}/{ColorMath.ToHex(Color2)}, speed {Speed}, mode {Mode}";
}

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

            double brightness = Brightness(key);
            if (brightness <= 0) return null;

            var state = new LightingState(PrimaryColor(key), brightness);
            Diagnostics.Log($"Windows was showing {ColorMath.ToHex(state.Color)} at {brightness * 100:F0}%");
            return state;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"could not read the Windows lighting state: {e.Message}");
            return null;
        }
    }

    // Null when Dynamic Lighting is off in Windows, so there is nothing to follow.
    public static WindowsEffect? ReadEffect()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return null;

            if (key.GetValue("AmbientLightingEnabled") as int? != 1) return null;

            int type = key.GetValue("EffectType") as int? ?? 0;

            return new WindowsEffect(
                Enum.IsDefined(typeof(WindowsEffectType), type) ? (WindowsEffectType)type : WindowsEffectType.Solid,
                PrimaryColor(key),
                FromColorRef(key.GetValue("Color2")),
                Math.Clamp(key.GetValue("Speed") as int? ?? 5, 1, 10),
                key.GetValue("EffectMode") as int? ?? 0);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"could not read the Windows lighting effect: {e.Message}");
            return null;
        }
    }

    // The brightness slider in Settings, 0..1. Null when Dynamic Lighting is off.
    public static double? ReadBrightness()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null || key.GetValue("AmbientLightingEnabled") as int? != 1) return null;

            return Brightness(key);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"could not read the Windows lighting brightness: {e.Message}");
            return null;
        }
    }

    private static double Brightness(RegistryKey key) =>
        Math.Clamp((key.GetValue("Brightness") as int? ?? 0) / 100.0, 0, 1);

    private static Color PrimaryColor(RegistryKey key)
    {
        if (key.GetValue("UseSystemAccentColor") as int? == 1)
        {
            try { return new UISettings().GetColorValue(UIColorType.Accent); }
            catch (Exception e) { Diagnostics.Log($"could not read the accent colour: {e.Message}"); }
        }

        return FromColorRef(key.GetValue("Color"));
    }

    // Stored as a COLORREF, so the low byte is red.
    private static Color FromColorRef(object? value)
    {
        uint raw = unchecked((uint)(value as int? ?? 0));
        return Color.FromArgb(255,
            (byte)(raw & 0xFF),
            (byte)((raw >> 8) & 0xFF),
            (byte)((raw >> 16) & 0xFF));
    }
}
