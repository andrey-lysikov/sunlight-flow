//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace SunlightFlow;

// Settings live next to the installed app, in %LOCALAPPDATA%\Sunlight-Flow.
// No coordinates here: they come from the IP address and the time zone.
internal sealed record AppConfig(
    double SunHighDegrees = 10.0,   // above this sun elevation the lighting is off
    double SunLowDegrees = -6.0,    // below it brightness reaches the ceiling
    double MaxBrightness = 1.0,     // ceiling the lighting ramps up to at night
    double Gamma = 2.2,             // the eye perceives brightness non-linearly
    bool UseWeather = true,         // cloud cover through Open-Meteo
    string Color = "#0078FF",
    string TimeZone = "Auto",       // zone the longitude is derived from
    bool LoadEnabled = false,       // react to processor or graphics load
    LoadSource LoadSource = LoadSource.Max,
    LoadEffect LoadEffect = LoadEffect.Color,
    string BusyColor = "#FF3000",
    double LoadIntervalSeconds = 1.0,
    string GpuEngines = "3D, Compute")
{
    public static AppConfig Default => new();

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunlight-Flow");

    public static string FilePath => Path.Combine(Directory, "Sunlight-Flow.conf");

    // On first run drops the bundled sample in place, comments and all,
    // so the parameters explain themselves.
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
            MaxBrightness: ini.Double("General", "BaseBrightness", 100.0) / 100.0,
            Gamma: ini.Double("General", "Gamma", d.Gamma),
            UseWeather: ini.Bool("General", "WeatherCloud", d.UseWeather),
            Color: ini.String("General", "BaseColor", d.Color),
            TimeZone: ini.String("General", "TimeZone", d.TimeZone),
            LoadEnabled: ini.Bool("DimOnLoad", "Enabled", d.LoadEnabled),
            LoadSource: ParseSource(ini.String("DimOnLoad", "Source", "Max")),
            LoadEffect: ParseEffect(ini.String("DimOnLoad", "Effect", "Color")),
            BusyColor: ini.String("DimOnLoad", "HiLoadColor", d.BusyColor),
            LoadIntervalSeconds: ini.Double("DimOnLoad", "Interval", d.LoadIntervalSeconds),
            GpuEngines: ini.String("DimOnLoad", "GpuEngines", d.GpuEngines));
    }

    public string[] GpuEngineList =>
        [.. GpuEngines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static LoadSource ParseSource(string value) => value.Trim().ToUpperInvariant() switch
    {
        "CPU" => LoadSource.Cpu,
        "GPU" => LoadSource.Gpu,
        _ => LoadSource.Max
    };

    private static LoadEffect ParseEffect(string value) =>
        value.Trim().Equals("Pulse", StringComparison.OrdinalIgnoreCase)
            ? LoadEffect.Pulse
            : LoadEffect.Color;

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
            MaxBrightness = Math.Clamp(MaxBrightness, 0, 1),
            Gamma = Gamma > 0.1 ? Gamma : 2.2,
            Color = ColorMath.IsValid(Color) ? Color : ColorMath.DefaultHex,
            BusyColor = ColorMath.IsValid(BusyColor) ? BusyColor : ColorMath.DefaultBusyHex,

            // Polling faster than once a second buys nothing and keeps writing
            // to the device for no visible gain.
            LoadIntervalSeconds = Math.Clamp(LoadIntervalSeconds, 1.0, 60.0)
        };
    }
}
