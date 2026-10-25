// How busy each GPU and the CPU are, as Task Manager shows them: a GPU's use is its busiest engine
// (3D, video decode, copy, ...) over the last interval, from Windows' GPU Engine counters.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VramMeter;

public sealed class Load : IDisposable
{
    static readonly Regex Engine = new(@"luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_\d+_eng_\d+_engtype_(.*)$", RegexOptions.IgnoreCase);

    readonly PerformanceCounterCategory engines = new("GPU Engine");
    readonly PerformanceCounter cpu = new("Processor Information", "% Processor Utility", "_Total");
    Dictionary<string, (long Busy, long Time)> last = new();

    public float Cpu { get; private set; }

    // Percent busy per GPU (by LUID) since the last call.
    public Dictionary<long, double> Gpus()
    {
        try { Cpu = Math.Min(100, cpu.NextValue()); }
        catch (InvalidOperationException) { }

        var now = new Dictionary<string, (long, long)>();
        var byEngine = new Dictionary<(long Luid, string Type), double>();
        foreach (InstanceData d in engines.ReadCategory()["Utilization Percentage"].Values)
        {
            var m = Engine.Match(d.InstanceName);
            if (!m.Success) continue;
            var sample = (d.Sample.RawValue, d.Sample.TimeStamp100nSec);
            now[d.InstanceName] = sample;
            if (!last.TryGetValue(d.InstanceName, out var before) || sample.Item2 <= before.Time) continue;
            double busy = 100.0 * (sample.Item1 - before.Busy) / (sample.Item2 - before.Time);
            long luid = ((long)Convert.ToUInt32(m.Groups[1].Value, 16) << 32) | Convert.ToUInt32(m.Groups[2].Value, 16);
            var key = (luid, m.Groups[3].Value);
            byEngine[key] = byEngine.GetValueOrDefault(key) + Math.Max(0, busy);
        }
        last = now;

        var result = new Dictionary<long, double>();
        foreach (var (key, busy) in byEngine)
            result[key.Luid] = Math.Min(100, Math.Max(result.GetValueOrDefault(key.Luid), busy));
        return result;
    }

    public void Dispose() => cpu.Dispose();
}
