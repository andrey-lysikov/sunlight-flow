//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;

namespace SunlightFlow;

internal sealed record Location(double Latitude, double Longitude, string Source);

// Latitude comes from the IP address, longitude from the time zone: a person sets
// the zone, while an IP may point at a VPN exit. If both agree, longitude uses IP too.
internal static class Geo
{
    private const double FallbackLatitude = 55.76;   // Moscow, when nothing else works

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private static string CachePath => Path.Combine(AppConfig.Directory, "location.json");

    public static async Task<Location> ResolveAsync(AppConfig cfg, CancellationToken ct = default)
    {
        var (zoneName, offset) = ResolveZone(cfg.TimeZone);
        double lonFromTz = Math.Clamp(offset.TotalHours * 15.0, -180, 180);

        var byIp = await FromIpAsync(ct);

        if (byIp is null)
        {
            var cached = ReadCache();
            double lat = cached?.Latitude ?? FallbackLatitude;
            string source = cached is null ? $"{zoneName}, default latitude" : $"{zoneName} and cache";

            var result = new Location(lat, lonFromTz, source);
            Diagnostics.Log($"location: {Describe(result)}");
            return result;
        }

        // Half a zone: beyond that the gap can no longer be explained by
        // sitting near one edge of the time zone.
        bool agree = Math.Abs(byIp.Value.Longitude - lonFromTz) <= 15.0;

        var final = new Location(
            byIp.Value.Latitude,
            agree ? byIp.Value.Longitude : lonFromTz,
            agree ? "IP" : $"latitude by IP, longitude by {zoneName}");

        if (!agree)
            Diagnostics.Log($"IP gives longitude {byIp.Value.Longitude:F2}, time zone «{zoneName}» " +
                            $"gives {lonFromTz:F2}; the time zone wins");

        WriteCache(final);
        Diagnostics.Log($"location: {Describe(final)}");
        return final;
    }

    private static string Describe(Location l) => string.Create(CultureInfo.InvariantCulture,
        $"{l.Latitude:F4}, {l.Longitude:F4} ({l.Source})");

    // Auto means the system zone. Otherwise an IANA id such as Europe/Moscow,
    // a Windows zone name, or a plain UTC offset like +03:00.
    private static (string Name, TimeSpan Offset) ResolveZone(string setting)
    {
        var now = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(setting) ||
            setting.Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            var local = TimeZoneInfo.Local;
            return (ToIana(local.Id), local.GetUtcOffset(now));
        }

        string value = setting.Trim().Trim('"');

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(value);
            return (zone.Id, zone.GetUtcOffset(now));
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (TryParseOffset(value, out var offset)) return (value, offset);

            Diagnostics.Log($"unknown time zone «{value}», falling back to the system one");
            var local = TimeZoneInfo.Local;
            return (ToIana(local.Id), local.GetUtcOffset(now));
        }
    }

    private static string ToIana(string windowsId) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out var iana) ? iana : windowsId;

    private static bool TryParseOffset(string value, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;

        string text = value.StartsWith("UTC", StringComparison.OrdinalIgnoreCase) ? value[3..] : value;
        text = text.Trim();
        if (text.Length == 0) return true;

        int sign = text[0] == '-' ? -1 : 1;
        if (text[0] is '+' or '-') text = text[1..];

        if (!TimeSpan.TryParse(text.Contains(':') ? text : $"{text}:00",
                               CultureInfo.InvariantCulture, out var parsed))
            return false;

        offset = sign < 0 ? -parsed : parsed;
        return Math.Abs(offset.TotalHours) <= 14;
    }

    private static async Task<(double Latitude, double Longitude)?> FromIpAsync(CancellationToken ct)
    {
        try
        {
            const string url = "http://ip-api.com/json/?fields=status,lat,lon";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            var root = doc.RootElement;

            if (root.GetProperty("status").GetString() != "success") return null;

            double lat = root.GetProperty("lat").GetDouble();
            double lon = root.GetProperty("lon").GetDouble();
            if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180) return null;

            return (lat, lon);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"IP geolocation unavailable: {e.Message}");
            return null;
        }
    }

    private static Location? ReadCache()
    {
        try
        {
            return File.Exists(CachePath)
                ? JsonSerializer.Deserialize<Location>(File.ReadAllText(CachePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(Location location)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Directory);
            File.WriteAllText(CachePath,
                JsonSerializer.Serialize(location, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Diagnostics.Log($"location cache not saved: {e.Message}");
        }
    }
}
