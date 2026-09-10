//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace SunlightFlow;

// Moon phase from the mean synodic month. Good to a few hours, which is far
// more than a sixteen-pixel icon can show.
internal static class Moon
{
    private static readonly DateTime KnownNewMoon = new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);
    private const double SynodicMonth = 29.530588853;

    // 0 and 1 are new moon, 0.5 is full. Waxing below 0.5, waning above.
    public static double Phase(DateTime utc)
    {
        double days = (utc - KnownNewMoon).TotalDays % SynodicMonth;
        if (days < 0) days += SynodicMonth;
        return days / SynodicMonth;
    }

    // Fraction of the disc that is lit, 0 at new moon and 1 at full.
    public static double Illumination(double phase) =>
        (1.0 - Math.Cos(2 * Math.PI * phase)) / 2.0;

    // Ecliptic latitude from the mean argument of latitude. The orbit is tilted
    // about 5.13°, and only near zero can the Earth's shadow reach the Moon.
    public static double Latitude(DateTime utc)
    {
        double d = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
        double argument = (93.272 + 13.229350 * d) % 360.0;
        return 5.128 * Math.Sin(argument * Math.PI / 180.0);
    }

    // A total lunar eclipse turns the Moon red, and it needs a full Moon sitting
    // close to a node. Roughly a couple of nights a year.
    public static bool IsBlood(DateTime utc)
    {
        double phase = Phase(utc);
        return Illumination(phase) > 0.985 && Math.Abs(Latitude(utc)) < 0.9;
    }
}
