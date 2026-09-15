//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace SunlightFlow;

// Colour, effect and brightness come from Settings > Dynamic Lighting; only the
// sun and load behaviour are configured here, in %LOCALAPPDATA%\Sunlight-Flow.
internal sealed record AppConfig(
    double SunHighDegrees = 10.0,   // above this sun elevation the lighting is off
    double SunLowDegrees = -6.0,    // below it brightness reaches the ceiling
    double VisibleFrom = 0.15,      // lower levels are invisible and count as zero
    string TimeZone = "Auto",       // zone the longitude is derived from
    bool UseWeather = true,         // cloud cover through Open-Meteo
    double Gamma = 2.2,             // the eye perceives brightness non-linearly
    bool LoadEnabled = false)       // tint towards the load colour under CPU or GPU load
{
    public static AppConfig Default => new();

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunlight-Flow");

    public static string FilePath => Path.Combine(Directory, "Sunlight-Flow.conf");

    // On first run drops the bundled sample in place, so the parameters explain themselves.
    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                WriteSample();
                Diagnostics.Log($"created config {FilePath}");
            }

            return Parse(File.ReadAllText(FilePath)).Sanitized();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"config unreadable ({e.Message}), falling back to defaults");
            return Default;
        }
    }

    private static AppConfig Parse(string text)
    {
        var ini = Ini.Parse(text);
        var d = Default;

        return new AppConfig(
            SunHighDegrees: ini.Double("General", "DimAbove", d.SunHighDegrees),
            SunLowDegrees: ini.Double("General", "FullBelow", d.SunLowDegrees),
            VisibleFrom: ini.Double("General", "VisibleFrom", d.VisibleFrom * 100.0) / 100.0,
            TimeZone: ini.String("General", "TimeZone", d.TimeZone),
            UseWeather: ini.Bool("General", "WeatherCloud", d.UseWeather),
            Gamma: ini.Double("General", "Gamma", d.Gamma),
            LoadEnabled: ini.Bool("General", "HardwareLoadSync", d.LoadEnabled));
    }

    private static void WriteSample()
    {
        System.IO.Directory.CreateDirectory(Directory);

        using var stream = typeof(AppConfig).Assembly.GetManifestResourceStream("sample.conf");
        if (stream is null) throw new InvalidOperationException("the sample config is missing from the assembly");

        using var file = File.Create(FilePath);
        stream.CopyTo(file);
    }

    // Repairs values that would make the level calculation divide by zero or go NaN.
    public AppConfig Sanitized()
    {
        double high = SunHighDegrees, low = SunLowDegrees;
        if (high - low < 0.1)
        {
            high = 10.0;
            low = -6.0;
        }

        return this with
        {
            SunHighDegrees = high,
            SunLowDegrees = low,
            Gamma = Gamma > 0.1 ? Gamma : 2.2,
            VisibleFrom = Math.Clamp(VisibleFrom, 0, 1)
        };
    }
}
