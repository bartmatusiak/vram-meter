// The sign-in log: for VRAM Meter's first 3 minutes after it starts at sign-in, when each GPU shows
// up to apps (and which one DirectX calls power saving), and which GPU each app set to one lands on.
// Apps opened at sign-in can land on the other GPU than they're set to; this shows when and why.
// Kept in %LOCALAPPDATA%\VRAM Meter\sign-in.log, replaced each sign-in.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace VramMeter;

public static class SignInLog
{
    public static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRAM Meter", "sign-in.log");

    public static void Start() => new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Sign-in log" }.Start();

    static void Run()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            using var w = new StreamWriter(File, append: false) { AutoFlush = true };
            var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
            string Stamp() => $"{DateTime.Now:HH:mm:ss} +{(DateTime.Now - boot).TotalSeconds,4:0}s";
            w.WriteLine($"VRAM Meter sign-in log. Windows started {boot:HH:mm:ss}; VRAM Meter {DateTime.Now:HH:mm:ss}.");
            w.WriteLine("Times are since Windows started. 'Order' is the GPU order an app that sets nothing gets.");

            var category = new PerformanceCounterCategory("GPU Process Memory");
            var seen = new HashSet<(int, long)>();
            string lastGpus = "";
            var until = DateTime.UtcNow.AddMinutes(3);
            for (int tick = 0; DateTime.UtcNow < until; tick++)
            {
                try
                {
                    var gpus = Dxgi.Gpus();
                    string Name(long luid) => gpus.FirstOrDefault(g => g.Luid == luid)?.ShortName ?? $"0x{luid:X}";
                    string state = "GPUs: " + (gpus.Count == 0 ? "none" : string.Join(", ", gpus.Select(g =>
                                       $"{g.ShortName} 0x{g.Luid:X}{(g.PowerSaving ? " [power saving]" : "")}{(g.HighPerformance ? " [high performance]" : "")}")))
                                   + ".  Order: " + string.Join(" > ", Dxgi.DefaultOrder().Select(a => a.Name));
                    if (state != lastGpus)
                    {
                        w.WriteLine($"{Stamp()}  {state}");
                        lastGpus = state;
                    }

                    // Each process of an app set to a GPU, once it holds memory: where it landed.
                    var memory = new Dictionary<int, Dictionary<long, long>>();
                    foreach (InstanceData d in category.ReadCategory()["Local Usage"].Values)
                    {
                        if (!GpuMonitor.Parse(d.InstanceName, out var pid, out var luid) || pid <= 0 || d.RawValue <= 0) continue;
                        if (!memory.TryGetValue(pid, out var m)) memory[pid] = m = new();
                        m[luid] = m.GetValueOrDefault(luid) + d.RawValue;
                    }
                    foreach (var (pid, m) in memory)
                    {
                        var p = Processes.Get(pid);
                        if (p?.Path == null || seen.Contains((pid, p.Start)) || m.Values.Sum() < 1 << 20) continue;
                        var pref = GpuPreferences.Get(p.Path);
                        if (pref == GpuChoice.Auto) continue;
                        seen.Add((pid, p.Start));
                        var wanted = pref == GpuChoice.PowerSaving ? gpus.FirstOrDefault(g => g.PowerSaving) : gpus.FirstOrDefault(g => g.HighPerformance);
                        long on = m.OrderByDescending(v => v.Value).First().Key;
                        string where = string.Join(" + ", m.OrderByDescending(v => v.Value).Select(v => $"{Name(v.Key)} {v.Value >> 20} MB"));
                        string started = p.Start == 0 ? "?" : $"{DateTime.FromFileTime(p.Start):HH:mm:ss} +{(DateTime.FromFileTime(p.Start) - boot).TotalSeconds:0}s";
                        w.WriteLine($"{Stamp()}  {p.Name} (pid {pid}, started {started}) set to {wanted?.ShortName ?? pref.ToString()}: on {where}"
                                    + (wanted != null && on != wanted.Luid ? "   <-- WRONG GPU" : ""));
                    }
                    if (tick % 30 == 29) w.WriteLine($"{Stamp()}  ...");
                }
                catch (Exception e) { w.WriteLine($"{Stamp()}  error: {e.Message}"); }
                Thread.Sleep(1000);
            }
            w.WriteLine($"{Stamp()}  Done.");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
