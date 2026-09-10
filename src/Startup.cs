//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace SunlightFlow;

// A package changes its exe path on update, so autostart goes through the
// manifest StartupTask. Without a package HKCU\Run remains.
internal static class Startup
{
    public const string TaskId = "SunlightFlowAutoStart";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Sunlight-Flow";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    private static readonly Lazy<bool> _hasIdentity = new(() =>
    {
        try
        {
            int length = 0;
            // APPMODEL_ERROR_NO_PACKAGE = 15700
            return GetCurrentPackageFullName(ref length, null) != 15700;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    });

    // True when the process runs with package identity, which is the only case
    // where Windows hands over background lighting control.
    public static bool HasPackageIdentity => _hasIdentity.Value;

    // Checked on every start: the user may have switched autostart off in Task
    // Manager, and a package update can reset the task state.
    public static async Task EnsureEnabledAsync()
    {
        try
        {
            if (await IsEnabledAsync())
            {
                Diagnostics.Log("autostart already on");
                return;
            }

            bool now = await SetAsync(true);
            Diagnostics.Log(now
                ? "autostart enabled"
                : "could not enable autostart, most likely blocked in Task Manager");
        }
        catch (Exception e)
        {
            Diagnostics.Log($"autostart check failed: {e.Message}");
        }
    }

    public static async Task<bool> IsEnabledAsync()
    {
        if (HasPackageIdentity)
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            catch
            {
                return false;
            }
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    // Returns the state that actually took effect: Windows may refuse when the
    // user has disabled autostart for this app.
    public static async Task<bool> SetAsync(bool enabled)
    {
        if (HasPackageIdentity)
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (enabled)
                {
                    var state = await task.RequestEnableAsync();
                    return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
                }

                task.Disable();
                return false;
            }
            catch
            {
                return await IsEnabledAsync();
            }
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null) return false;

        if (enabled)
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(RunValue, $"\"{exe}\"");
            return true;
        }

        key.DeleteValue(RunValue, throwOnMissingValue: false);
        return false;
    }
}
