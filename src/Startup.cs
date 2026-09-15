//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel;

namespace SunlightFlow;

// Autostart goes through the StartupTask in the package manifest, so it survives
// updates and can be switched off in Task Manager.
internal static class Startup
{
    private const string TaskId = "SunlightFlowAutoStart";

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

    // Checked on every start: a package update can reset the task state. Without
    // identity, as in a build run from bin, there is no task to enable.
    public static async Task EnsureEnabledAsync()
    {
        if (!HasPackageIdentity) return;

        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (IsOn(task.State))
            {
                Diagnostics.Log("autostart already on");
                return;
            }

            bool now = IsOn(await task.RequestEnableAsync());
            Diagnostics.Log(now
                ? "autostart enabled"
                : "could not enable autostart, most likely blocked in Task Manager");
        }
        catch (Exception e)
        {
            Diagnostics.Log($"autostart check failed: {e.Message}");
        }
    }

    private static bool IsOn(StartupTaskState state) =>
        state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
}
