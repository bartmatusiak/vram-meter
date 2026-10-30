// After sign-in, once the apps that start with Windows have started (on the power saving GPU, its
// display being main), show only the fast GPU's display, so whatever opens next gets the fast GPU.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace VramMeter;

public static class SignInSwitch
{
    // At least this long after sign-in, then until no new app has used a GPU for Quiet, at most Limit.
    static readonly TimeSpan Least = TimeSpan.FromSeconds(45), Quiet = TimeSpan.FromSeconds(20), Limit = TimeSpan.FromMinutes(3);

    // Runs off the UI thread; done(what happened) is called once it's over, with null when there was
    // nothing to do.
    public static void Start(Settings settings, Action<string?> done) => new Thread(() =>
    {
        string? message;
        try { message = Run(settings); }
        catch (Exception e) { App.Log(e); message = null; }
        done(message);
    }) { IsBackground = true, Name = "Sign-in switch" }.Start();

    static string? Run(Settings settings)
    {
        // Windows may bring back both displays (it keeps a saved layout per kind of layout): one monitor
        // shows one input, so the other would be an invisible screen apps open on. Straight away, the
        // power saving GPU's display alone, and the monitor on it.
        var first = Dxgi.Gpus();
        var lo = first.FirstOrDefault(g => g.PowerSaving);
        var hi = first.FirstOrDefault(g => g.HighPerformance);
        if (lo != null && hi != null && Displays.Available(lo, hi) && Displays.Current(lo, hi) == DisplayLayout.PowerSavingMain)
        {
            try
            {
                Displays.PowerSavingOnly(lo, hi);
                Thread.Sleep(1500);
                if (Displays.Monitors().FirstOrDefault(m => m.Luid == lo.Luid)?.Cable is string loCable) MonitorInput.SwitchTo(settings, loCable);
            }
            catch (InvalidOperationException) { }
        }

        var started = Stopwatch.StartNew();
        var monitor = new GpuMonitor();
        var seen = new HashSet<int>();
        var lastNew = Stopwatch.StartNew();
        while (started.Elapsed < Limit)
        {
            try
            {
                foreach (var app in monitor.Read().Apps)
                    foreach (var p in app.Procs)
                        if (seen.Add(p.Pid)) lastNew.Restart();
            }
            catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException) { }
            if (started.Elapsed >= Least && lastNew.Elapsed >= Quiet) break;
            Thread.Sleep(2000);
        }

        var gpus = Dxgi.Gpus();
        var low = gpus.FirstOrDefault(g => g.PowerSaving);
        var high = gpus.FirstOrDefault(g => g.HighPerformance);
        if (low == null || high == null || !Displays.Available(low, high))
            return "Couldn't switch displays: both GPUs need a display plugged in.";
        if (Displays.Current(low, high) is not (DisplayLayout.PowerSavingOnly or DisplayLayout.PowerSavingMain)) return null;  // already switched, or set by hand
        try
        {
            Displays.FastOnly(low, high);
            // And the monitor to that display's input.
            Thread.Sleep(1500);
            if (Displays.Monitors().FirstOrDefault(m => m.Luid == high.Luid)?.Cable is string cable) MonitorInput.SwitchTo(settings, cable);
            return $"Showing only the {high.ShortName}'s display now. Apps that started at sign-in stay on the {low.ShortName}; what opens next gets the {high.ShortName}.";
        }
        catch (InvalidOperationException e) { return "Couldn't switch displays: " + e.Message; }
    }
}
