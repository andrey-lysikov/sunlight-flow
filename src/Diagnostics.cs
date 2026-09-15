//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace SunlightFlow;

// Log in %LOCALAPPDATA%\Sunlight-Flow. The app has no console and no windows,
// so this is the only way to find out why the lighting is not ours.
internal static class Diagnostics
{
    private const long MaxBytes = 256 * 1024;

    private static readonly Lock _lock = new();
    private static readonly string _path =
        Path.Combine(AppConfig.Directory, "Sunlight-Flow.log");

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(AppConfig.Directory);

                // Truncate wholesale once it grows: half-lines read badly and
                // history older than a couple of runs is of no use.
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes) info.Delete();

                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                File.AppendAllText(_path, $"{stamp}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never bring the app down.
        }
    }
}
