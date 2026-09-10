//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;

namespace SunlightFlow;

internal static class Sun
{
    private static double Rad(double deg) => deg * Math.PI / 180.0;

    // Sun elevation in degrees, NOAA algorithm. Driven by UTC: the time zone
    // has no bearing on where the sun is, only the coordinates do.
    public static double Elevation(DateTime utc, double latDeg, double lonDeg)
    {
        // days since the J2000.0 epoch
        double n = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;

        double meanLongitude = 280.460 + 0.9856474 * n;
        double meanAnomaly = Rad(357.528 + 0.9856003 * n);

        double eclipticLon = Rad(meanLongitude
                                 + 1.915 * Math.Sin(meanAnomaly)
                                 + 0.020 * Math.Sin(2 * meanAnomaly));
        double obliquity = Rad(23.439 - 0.0000004 * n);

        double declination = Math.Asin(Math.Sin(obliquity) * Math.Sin(eclipticLon));
        double rightAscension = Math.Atan2(Math.Cos(obliquity) * Math.Sin(eclipticLon),
                                           Math.Cos(eclipticLon)) * 180.0 / Math.PI;

        double gmst = (18.697374558 + 24.06570982441908 * n) % 24.0;
        if (gmst < 0) gmst += 24.0;

        double localSidereal = gmst * 15.0 + lonDeg;          // in degrees
        double hourAngle = Rad(localSidereal - rightAscension);

        double lat = Rad(latDeg);
        double sinAlt = Math.Sin(lat) * Math.Sin(declination)
                      + Math.Cos(lat) * Math.Cos(declination) * Math.Cos(hourAngle);

        return Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) * 180.0 / Math.PI;
    }
}

// Zero by day, rising towards MaxBrightness as the sun sets. Cloud cover adds
// on top: an overcast evening goes dark earlier than the sun alone suggests.
internal sealed class LevelCalculator
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private DateTime _weatherStamp = DateTime.MinValue;
    private double _cloudFactor;

    public double LastElevation { get; private set; }

    // Where the sun sits between the two thresholds: 0 at the dimming point,
    // 1 once it is low enough for full brightness.
    public double LastSunFactor { get; private set; }

    private static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return x * x * (3 - 2 * x);
    }

    // 0.0 is clear sky, 1.0 is heavy overcast. Any failure quietly yields 0.
    private async Task<double> CloudFactorAsync(AppConfig cfg, Location loc)
    {
        if (!cfg.UseWeather) return 0.0;
        if (DateTime.UtcNow - _weatherStamp < TimeSpan.FromMinutes(15)) return _cloudFactor;

        try
        {
            var inv = CultureInfo.InvariantCulture;
            string url = "https://api.open-meteo.com/v1/forecast" +
                         $"?latitude={loc.Latitude.ToString(inv)}" +
                         $"&longitude={loc.Longitude.ToString(inv)}" +
                         "&current=cloud_cover";

            // Cloud cover directly, not derived from radiation: at high latitudes
            // a clear sky never reaches the radiation of a tropical noon.
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            double cover = doc.RootElement.GetProperty("current")
                                          .GetProperty("cloud_cover").GetDouble();
            _cloudFactor = Math.Clamp(cover / 100.0, 0, 1);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"cloud cover unavailable: {e.Message}");
            _cloudFactor = 0.0;
        }

        _weatherStamp = DateTime.UtcNow;
        return _cloudFactor;
    }

    public async Task<double> TargetAsync(AppConfig cfg, Location loc)
    {
        double elevation = Sun.Elevation(DateTime.UtcNow, loc.Latitude, loc.Longitude);
        LastElevation = elevation;

        double sun = SmoothStep((cfg.SunHighDegrees - elevation) / (cfg.SunHighDegrees - cfg.SunLowDegrees));
        LastSunFactor = sun;

        double cloud = await CloudFactorAsync(cfg, loc);

        // Clouds only add brightness and never stand in for the sun: an overcast
        // noon still needs no lighting.
        double level = sun + (1.0 - sun) * cloud * 0.6;

        // Perceived brightness, the same scale the config speaks in. Gamma is
        // applied later, on the way to the hardware.
        return Math.Clamp(level * cfg.MaxBrightness, 0, 1);
    }
}
