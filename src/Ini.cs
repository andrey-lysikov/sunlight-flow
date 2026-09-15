//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace SunlightFlow;

// Reads "[Section] Key = value" files. The format is chosen for the comments
// that sit next to every key, which JSON cannot carry.
internal sealed class Ini
{
    private readonly Dictionary<string, string> _values =
        new(StringComparer.OrdinalIgnoreCase);

    public static Ini Parse(string text)
    {
        var ini = new Ini();
        string section = "";

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            // Drop a trailing comment if the line carries one. It needs a space
            // before the hash, otherwise a colour like #0078FF would be cut away.
            int gap = value.IndexOfAny([' ', '\t']);
            int hash = gap >= 0 ? value.IndexOf('#', gap) : -1;
            if (hash >= 0) value = value[..hash].TrimEnd();

            ini._values[$"{section}.{key}"] = value;
        }

        return ini;
    }

    private string? Raw(string section, string key) =>
        _values.TryGetValue($"{section}.{key}", out var v) && v.Length > 0 ? v : null;

    public double Double(string section, string key, double fallback) =>
        double.TryParse(Raw(section, key), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : fallback;

    public bool Bool(string section, string key, bool fallback) => Raw(section, key) switch
    {
        null => fallback,
        var s when s.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        var s when s is "1" or "yes" or "on" => true,
        var s when s is "0" or "no" or "off" => false,
        _ => fallback
    };

    public string String(string section, string key, string fallback) =>
        Raw(section, key) ?? fallback;
}
