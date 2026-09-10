//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace SunlightFlow;

internal static class Program
{
    private const string MutexName = @"Local\Sunlight-Flow.single-instance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool first);
        if (!first)
        {
            MessageBox.Show("Sunlight-Flow is already running; look for its notification area icon.",
                            "Sunlight-Flow", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
