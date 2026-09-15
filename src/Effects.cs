//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Numerics;

using Color = Windows.UI.Color;

namespace SunlightFlow;

// Windows stops drawing its effect once the lamps are ours, so the one picked
// in Settings is redrawn here.
internal static class Effects
{
    public static WindowsEffect Resolve() =>
        WindowsLighting.ReadEffect() ?? WindowsEffect.Solid(ColorMath.Default);

    // The rainbow has no base to oppose; wave and wheel take their second colour.
    // Otherwise the table picks one that stands out against the base.
    public static Color? LoadColor(WindowsEffect effect) => effect.Type switch
    {
        _ when !effect.TintsUnderLoad => null,
        WindowsEffectType.Rainbow => ColorMath.DefaultLoad,
        WindowsEffectType.Wave or WindowsEffectType.Wheel when !effect.Color2.Equals(effect.Color) => effect.Color2,
        _ => ColorMath.LoadColorFor(effect.Color)
    };

    // Cycles per second: a full sweep takes 25 s at the slowest and 2.5 s at the fastest.
    public static double Rate(WindowsEffect effect) => 0.04 * effect.Speed;

    // Lamp positions come normalised to 0..1 across the device: X to the right,
    // Y downwards, Z away from the viewer. Phase is 0..1.
    public static void Render(WindowsEffect effect, double phase, ReadOnlySpan<Vector3> positions, Span<Color> buffer)
    {
        switch (effect.Type)
        {
            // One colour fading down to dark and back.
            case WindowsEffectType.Breathing:
                buffer.Fill(ColorMath.Scale(effect.Color, Wave(phase)));
                break;

            // Forward, backward.
            case WindowsEffectType.Rainbow:
                double hue = (effect.Mode == 1 ? 1 - phase : phase) * 360;
                buffer.Fill(ColorMath.FromHue(hue));
                break;

            case WindowsEffectType.Wave:
                for (int i = 0; i < buffer.Length; i++)
                {
                    var p = positions[i];

                    // Right, left, to the back, to the front, down, up, outwards, inwards.
                    double along = effect.Mode switch
                    {
                        1 => 1 - p.X,
                        2 => p.Z,
                        3 => 1 - p.Z,
                        4 => p.Y,
                        5 => 1 - p.Y,
                        6 => FromCentre(p),
                        7 => 1 - FromCentre(p),
                        _ => p.X
                    };
                    buffer[i] = ColorMath.Mix(effect.Color, effect.Color2, Wave(along - phase));
                }
                break;

            case WindowsEffectType.Wheel:
                for (int i = 0; i < buffer.Length; i++)
                {
                    var p = positions[i];

                    // Y grows downwards, so the angle grows clockwise on the device.
                    double angle = (Math.Atan2(p.Y - 0.5, p.X - 0.5) / (2 * Math.PI) + 1) % 1;

                    // Clockwise, counterclockwise.
                    double turn = effect.Mode == 1 ? angle + phase : angle - phase;
                    buffer[i] = ColorMath.Mix(effect.Color, effect.Color2, Wave(turn));
                }
                break;

            case WindowsEffectType.Gradient:
                for (int i = 0; i < buffer.Length; i++)
                {
                    var p = positions[i];

                    // Horizontal, front to back, outwards, vertical.
                    double k = effect.Mode switch
                    {
                        1 => p.Z,
                        2 => FromCentre(p),
                        3 => p.Y,
                        _ => p.X
                    };
                    buffer[i] = ColorMath.Mix(effect.Color, effect.Color2, k);
                }
                break;

            default:
                buffer.Fill(effect.Color);
                break;
        }
    }

    // Smooth 0 -> 1 -> 0 over one period, so colours flow rather than jump at the seam.
    private static double Wave(double t) => 0.5 - 0.5 * Math.Cos(2 * Math.PI * t);

    // 0 at the centre of the device, 1 at the edge.
    private static double FromCentre(Vector3 p) =>
        Math.Min(1, 2 * Vector3.Distance(p, new Vector3(0.5f)));

    // Spreads lamp positions over 0..1 on each axis. A flat strip gets 0.5 on
    // the axes it has no extent along.
    public static Vector3[] Normalise(IReadOnlyList<Vector3> positions)
    {
        if (positions.Count == 0) return [];

        var min = positions.Aggregate(Vector3.Min);
        var size = positions.Aggregate(Vector3.Max) - min;

        static float Scale(float v, float extent) => extent < 1e-4f ? 0.5f : v / extent;

        return [.. positions.Select(p => p - min).Select(p =>
            new Vector3(Scale(p.X, size.X), Scale(p.Y, size.Y), Scale(p.Z, size.Z)))];
    }
}
