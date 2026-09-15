//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SunlightFlow;

// Reads the busier of processor and graphics load without elevation or drivers.
// The CPU goes through kernel32 because counter names are localised.
internal sealed partial class LoadMonitor : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [GeneratedRegex(@"engtype_(?<type>\w+)$")]
    private static partial Regex EngineType();

    private const string GpuCategory = "GPU Engine";
    private const string GpuCounter = "Utilization Percentage";

    // Instances come and go with processes, so the set is refreshed now and then.
    private static readonly TimeSpan InstanceRefresh = TimeSpan.FromSeconds(30);

    // Video encoding is left out: a streaming tool keeps it busy while the machine idles.
    private static readonly string[] Engines = ["3D", "Compute"];

    private readonly Lock _lock = new();

    private long _idle, _kernel, _user;
    private bool _cpuPrimed;

    private Dictionary<string, PerformanceCounter[]> _gpu = [];
    private DateTime _gpuStamp = DateTime.MinValue;
    private bool _gpuBroken;

    public double Sample() => Math.Max(SampleCpu(), SampleGpu());

    // First call only records the counters, so it reports zero by design.
    private double SampleCpu()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;

        double result = 0;
        if (_cpuPrimed)
        {
            double idleDelta = idle - _idle;
            double total = (kernel - _kernel) + (user - _user);
            if (total > 0) result = Math.Clamp(1.0 - idleDelta / total, 0, 1);
        }

        _idle = idle;
        _kernel = kernel;
        _user = user;
        _cpuPrimed = true;

        return result;
    }

    // Task Manager style: engines of one type add up, the busiest type wins.
    private double SampleGpu()
    {
        if (_gpuBroken) return 0;

        try
        {
            RefreshGpuInstances();

            double best = 0;
            lock (_lock)
            {
                foreach (var group in _gpu.Values)
                {
                    double sum = 0;
                    foreach (var counter in group) sum += counter.NextValue();
                    if (sum > best) best = sum;
                }
            }

            return Math.Clamp(best / 100.0, 0, 1);
        }
        catch (InvalidOperationException)
        {
            // A process ended between enumeration and the read, so its instance
            // is gone. Rebuild the set rather than give up on the GPU.
            _gpuStamp = DateTime.MinValue;
            return 0;
        }
        catch (Exception e)
        {
            _gpuBroken = true;
            Diagnostics.Log($"GPU counters unavailable, falling back to CPU only: {e.Message}");
            return 0;
        }
    }

    private void RefreshGpuInstances()
    {
        if (DateTime.UtcNow - _gpuStamp < InstanceRefresh) return;
        _gpuStamp = DateTime.UtcNow;

        var category = new PerformanceCounterCategory(GpuCategory);
        var grouped = new Dictionary<string, List<PerformanceCounter>>();

        foreach (var instance in category.GetInstanceNames())
        {
            var match = EngineType().Match(instance);
            if (!match.Success) continue;

            string type = match.Groups["type"].Value;
            if (!Wanted(type)) continue;

            if (!grouped.TryGetValue(type, out var list)) grouped[type] = list = [];
            list.Add(new PerformanceCounter(GpuCategory, GpuCounter, instance, readOnly: true));
        }

        var replacement = grouped.ToDictionary(p => p.Key, p => p.Value.ToArray());

        // A counter reports a rate, so the first read only sets the baseline.
        foreach (var group in replacement.Values)
            foreach (var counter in group) counter.NextValue();

        Dictionary<string, PerformanceCounter[]> old;
        lock (_lock)
        {
            old = _gpu;
            _gpu = replacement;
        }

        foreach (var group in old.Values)
            foreach (var counter in group) counter.Dispose();
    }

    // Engines are numbered per adapter, so Compute_0 has to match Compute.
    private static bool Wanted(string type) =>
        Engines.Any(e => type.StartsWith(e, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        Dictionary<string, PerformanceCounter[]> snapshot;
        lock (_lock)
        {
            snapshot = _gpu;
            _gpu = [];
        }

        foreach (var group in snapshot.Values)
            foreach (var counter in group) counter.Dispose();
    }
}
