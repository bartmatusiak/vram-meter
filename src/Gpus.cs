// The GPUs (from DirectX, which also says which one Windows means by "power saving" and "high
// performance") and how much of each one's memory is in use, and by which process (from Windows'
// GPU performance counters, the ones Task Manager reads).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VramMeter;

public enum GpuVendor { Other, Nvidia, Amd, Intel }

// Total: the memory the GPU works in. A built-in GPU (the power saving one, beside a separate card)
// has only a small slice of its own and borrows the rest from system RAM, so it counts both.
public sealed record Gpu(long Luid, string Name, long Total, uint VendorId, bool PowerSaving, bool HighPerformance, bool BuiltIn)
{
    public GpuVendor Vendor => VendorId switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        _ => GpuVendor.Other,
    };

    // "NVIDIA GeForce RTX 5080" -> "RTX 5080"; "AMD Radeon(TM) Graphics" -> "Radeon Graphics"
    public string ShortName => Regex.Replace(Name, @"^(NVIDIA|AMD|Intel\(R\))\s+(GeForce\s+)?|\(TM\)|\(R\)", "").Trim();
}

static unsafe class Dxgi
{
    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory2(uint flags, Guid* riid, IntPtr* factory);

    static readonly Guid IID_IDXGIFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");

    const int MinimumPower = 1, HighPerformance = 2;
    const uint SoftwareAdapter = 2;

    struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    // The hardware GPUs, fastest first.
    public static List<Gpu> Gpus()
    {
        var gpus = new List<Gpu>();
        IntPtr factory;
        Guid iid = IID_IDXGIFactory6;
        if (CreateDXGIFactory2(0, &iid, &factory) < 0) return gpus;
        try
        {
            long? low = FirstLuid(factory, MinimumPower), high = FirstLuid(factory, HighPerformance);
            for (uint i = 0; Enum(factory, i, HighPerformance, out var adapter); i++)
            {
                try
                {
                    var d = Desc(adapter);
                    if ((d.Flags & SoftwareAdapter) != 0) continue;
                    long luid = ((long)d.LuidHigh << 32) | d.LuidLow;
                    bool builtIn = luid == low && luid != high;
                    long total = (long)d.DedicatedVideoMemory + (builtIn ? (long)d.SharedSystemMemory : 0);
                    gpus.Add(new Gpu(luid, new string(d.Description), total, d.VendorId, luid == low, luid == high, builtIn));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return gpus;
    }

    static long? FirstLuid(IntPtr factory, int preference)
    {
        if (!Enum(factory, 0, preference, out var adapter)) return null;
        try
        {
            var d = Desc(adapter);
            return ((long)d.LuidHigh << 32) | d.LuidLow;
        }
        finally { Marshal.Release(adapter); }
    }

    // IDXGIFactory6::EnumAdapterByGpuPreference, slot 29 of its vtable.
    static bool Enum(IntPtr factory, uint index, int preference, out IntPtr adapter)
    {
        var fn = (delegate* unmanaged<IntPtr, uint, int, Guid*, IntPtr*, int>)(*(*(void***)factory + 29));
        Guid iid = IID_IDXGIAdapter1;
        IntPtr a = IntPtr.Zero;
        int hr = fn(factory, index, preference, &iid, &a);
        adapter = a;
        return hr >= 0 && a != IntPtr.Zero;
    }

    // IDXGIAdapter1::GetDesc1, slot 10.
    static AdapterDesc1 Desc(IntPtr adapter)
    {
        var fn = (delegate* unmanaged<IntPtr, AdapterDesc1*, int>)(*(*(void***)adapter + 10));
        AdapterDesc1 d;
        fn(adapter, &d);
        return d;
    }
}

public readonly record struct ProcRef(int Pid, long Start);

// Every process of one program (by its .exe) that holds GPU memory.
public sealed class AppGroup
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public string? Path { get; init; }
    public string? ProtectedWhy { get; set; }
    public List<ProcRef> Procs { get; } = new();
    public Dictionary<long, long> Dedicated { get; } = new();
    public long Shared { get; set; }
    public long TotalDedicated => Dedicated.Values.Sum();
}

public sealed record Snapshot(List<Gpu> Gpus, Dictionary<long, long> Used, List<AppGroup> Apps);

public sealed class GpuMonitor
{
    static readonly Regex Instance = new(@"^(?:pid_(\d+)_)?luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})", RegexOptions.IgnoreCase);

    readonly PerformanceCounterCategory adapters = new("GPU Adapter Memory");
    readonly PerformanceCounterCategory processes = new("GPU Process Memory");
    List<Gpu> gpus = new();
    DateTime gpusAt;

    public Snapshot Read()
    {
        // The GPUs change only when a driver does; look again every half minute.
        if (gpus.Count == 0 || (DateTime.UtcNow - gpusAt).TotalSeconds > 30)
        {
            gpus = Dxgi.Gpus();
            gpusAt = DateTime.UtcNow;
        }
        var known = gpus.Select(g => g.Luid).ToHashSet();
        var builtIn = gpus.Where(g => g.BuiltIn).Select(g => g.Luid).ToHashSet();

        var used = new Dictionary<long, long>();
        var adapterData = adapters.ReadCategory();
        var adapterShared = adapterData["Shared Usage"];
        foreach (InstanceData d in adapterData["Dedicated Usage"].Values)
        {
            if (!Parse(d.InstanceName, out _, out var luid) || !known.Contains(luid)) continue;
            long u = d.RawValue;
            if (builtIn.Contains(luid) && adapterShared.Contains(d.InstanceName)) u += adapterShared[d.InstanceName].RawValue;
            used[luid] = used.GetValueOrDefault(luid) + u;
        }

        var all = processes.ReadCategory();
        var dedicated = all["Dedicated Usage"];
        var shared = all["Shared Usage"];
        var groups = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (InstanceData d in dedicated.Values)
        {
            if (!Parse(d.InstanceName, out var pid, out var luid) || pid <= 0 || !known.Contains(luid)) continue;
            long ded = d.RawValue;
            long sh = shared.Contains(d.InstanceName) ? shared[d.InstanceName].RawValue : 0;
            if (ded <= 0 && sh <= 0) continue;
            // On a built-in GPU the borrowed system RAM is its memory: count it there.
            if (builtIn.Contains(luid))
            {
                ded += sh;
                sh = 0;
            }

            var p = Processes.Get(pid);
            if (p == null) continue;  // ended since the counters were read
            string key = p.Path ?? p.Name;
            if (!groups.TryGetValue(key, out var g))
            {
                groups[key] = g = new AppGroup { Key = key, Name = p.Name, Title = p.Title, Path = p.Path };
            }
            if (!g.Procs.Any(r => r.Pid == pid)) g.Procs.Add(new ProcRef(pid, p.Start));
            g.Dedicated[luid] = g.Dedicated.GetValueOrDefault(luid) + ded;
            g.Shared += sh;
            g.ProtectedWhy ??= p.ProtectedWhy;
        }

        return new Snapshot(gpus, used, groups.Values.OrderByDescending(g => g.TotalDedicated).ToList());
    }

    // "pid_44252_luid_0x00000000_0x0002BB96_phys_0" -> 44252 and the LUID (high part, then low)
    static bool Parse(string instance, out int pid, out long luid)
    {
        pid = 0;
        luid = 0;
        var m = Instance.Match(instance);
        if (!m.Success) return false;
        if (m.Groups[1].Success) pid = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        luid = ((long)uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber) << 32)
             | uint.Parse(m.Groups[3].Value, NumberStyles.HexNumber);
        return true;
    }
}
