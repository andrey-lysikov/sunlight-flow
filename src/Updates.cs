//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace SunlightFlow;

// Compares the installed version against the latest GitHub release and raises
// a system notification when a newer one is out.
internal static class Updates
{
    private const string Repository = "andrey-lysikov/sunlight-flow";
    private const string LatestApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string ReleasesPage = $"https://github.com/{Repository}/releases/latest";

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private static string StampPath => Path.Combine(AppConfig.Directory, "update-check");

    public static async Task CheckAsync(bool force = false)
    {
        if (!force && !DueForCheck()) return;

        try
        {
            var latest = await FetchLatestAsync();
            if (latest is null) return;

            WriteStamp();

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            if (latest.Version <= Normalize(current))
            {
                Diagnostics.Log($"no updates, {Short(current)} is installed");
                return;
            }

            Diagnostics.Log($"version {Short(latest.Version)} is available, {Short(current)} installed");
            Notify(Short(latest.Version));
        }
        catch (Exception e)
        {
            Diagnostics.Log($"update check failed: {e.Message}");
        }
    }

    private sealed record Release(Version Version);

    private static bool DueForCheck()
    {
        try
        {
            if (!File.Exists(StampPath)) return true;
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(StampPath) > Interval;
        }
        catch
        {
            return true;
        }
    }

    private static void WriteStamp()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Directory);
            File.WriteAllText(StampPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception e)
        {
            Diagnostics.Log($"check timestamp not saved: {e.Message}");
        }
    }

    private static async Task<Release?> FetchLatestAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sunlight-Flow", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi));

        if (!doc.RootElement.TryGetProperty("tag_name", out var tag)) return null;

        string text = (tag.GetString() ?? "").TrimStart('v', 'V');
        return Version.TryParse(Pad(text), out var version) ? new Release(version) : null;
    }

    // Version.TryParse wants at least two parts, while a tag can be just "2".
    private static string Pad(string text) => text.Contains('.') ? text : $"{text}.0";

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0));

    private static string Short(Version v) => $"{v.Major}.{v.Minor}";

    private static void Notify(string version)
    {
        try
        {
            string xml = $"""
                <toast activationType="protocol" launch="{ReleasesPage}">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>Sunlight-Flow {version}</text>
                      <text>A new version is out. Click to open the releases page.</text>
                    </binding>
                  </visual>
                </toast>
                """;

            var document = new XmlDocument();
            document.LoadXml(xml);

            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(document));
        }
        catch (Exception e)
        {
            Diagnostics.Log($"notification not shown: {e.Message}");
        }
    }
}
