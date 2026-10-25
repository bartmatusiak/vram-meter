// Each GPU's vitals: temperature, power, fan and memory clock from Windows (the driver's own report,
// as Task Manager reads it, for any vendor), and for NVIDIA cards watts, clocks, fan and PCIe link
// from NVIDIA's monitoring library (nvml.dll, installed with the driver).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VramMeter;

public sealed record GpuVitals
{
    public double? TempC { get; init; }
    public double? TempMaxC { get; init; }
    public double? PowerPct { get; init; }
    public double? PowerW { get; init; }
    public double? PowerLimitW { get; init; }
    public uint? CoreMHz { get; init; }
    public uint? MemoryMHz { get; init; }
    public uint? FanPct { get; init; }
    public uint? FanRpm { get; init; }
    public string? Pcie { get; init; }
    public string? Driver { get; init; }
}

public static class GpuStats
{
    static readonly Dictionary<long, (double? TempMax, uint? FanMax)> Caps = new();
    static Dictionary<string, string>? drivers;

    public static GpuVitals Read(Gpu gpu)
    {
        var v = Kmt(gpu);
        if (gpu.Vendor == GpuVendor.Nvidia) v = Nvml.Read(gpu.Name, v);
        return v with { Driver = Driver(gpu.Name) };
    }

    // D3DKMT_ADAPTER_PERFDATA: memory clock, fan, power (tenths of a %) and temperature (tenths of a °C).
    static unsafe GpuVitals Kmt(Gpu gpu)
    {
        var open = new OpenFromLuid { Low = (uint)(gpu.Luid & 0xFFFFFFFF), High = (int)(gpu.Luid >> 32) };
        if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return new GpuVitals();
        try
        {
            var perf = new byte[64];
            if (Query(open.Handle, 62, perf) != 0) return new GpuVitals();
            if (!Caps.ContainsKey(gpu.Luid))
            {
                var caps = new byte[40];
                Caps[gpu.Luid] = Query(open.Handle, 63, caps) == 0
                    ? (Positive(BitConverter.ToUInt32(caps, 28) / 10.0), NonZero(BitConverter.ToUInt32(caps, 24)))
                    : (null, null);
            }
            var (tempMax, fanMax) = Caps[gpu.Luid];
            ulong memHz = BitConverter.ToUInt64(perf, 8);
            return new GpuVitals
            {
                MemoryMHz = memHz > 0 ? (uint)(memHz / 1_000_000) : null,
                FanRpm = fanMax != null ? BitConverter.ToUInt32(perf, 48) : null,
                PowerPct = Positive(BitConverter.ToUInt32(perf, 52) / 10.0),
                TempC = Positive(BitConverter.ToUInt32(perf, 56) / 10.0),
                TempMaxC = tempMax,
            };
        }
        finally
        {
            var h = open.Handle;
            D3DKMTCloseAdapter(ref h);
        }
    }

    static double? Positive(double v) => v > 0 ? v : null;
    static uint? NonZero(uint v) => v > 0 ? v : null;

    static unsafe int Query(uint adapter, int type, byte[] buffer)
    {
        fixed (byte* p = buffer)
        {
            var q = new QueryInfo { Adapter = adapter, Type = type, Data = (IntPtr)p, Size = (uint)buffer.Length };
            return D3DKMTQueryAdapterInfo(ref q);
        }
    }

    // Driver versions by GPU name, from the display adapters' registry keys, read once.
    static string? Driver(string name)
    {
        if (drivers == null)
        {
            drivers = new(StringComparer.OrdinalIgnoreCase);
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var k = cls!.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is string n && k.GetValue("DriverVersion") is string d) drivers[n] = d;
                }
                catch (System.Security.SecurityException) { }
            }
        }
        return drivers.TryGetValue(name, out var v) ? v : null;
    }

    [StructLayout(LayoutKind.Sequential)] struct OpenFromLuid { public uint Low; public int High; public uint Handle; }
    [StructLayout(LayoutKind.Sequential)] struct QueryInfo { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }
    [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromLuid(ref OpenFromLuid o);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref uint h);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QueryInfo q);

    // NVIDIA's management library; absent without an NVIDIA driver, which leaves Windows' figures.
    static class Nvml
    {
        static bool tried, ready;
        static readonly Dictionary<string, IntPtr> Devices = new(StringComparer.OrdinalIgnoreCase);

        public static GpuVitals Read(string name, GpuVitals v)
        {
            if (!Init() || !Devices.TryGetValue(name, out var d)) return v;
            uint x;
            static uint? Ok(int rc, uint value) => rc == 0 ? value : null;
            var power = Ok(nvmlDeviceGetPowerUsage(d, out x), x);
            var limit = Ok(nvmlDeviceGetEnforcedPowerLimit(d, out x), x);
            uint gen, width, maxGen;
            string? pcie = nvmlDeviceGetCurrPcieLinkGeneration(d, out gen) == 0 && nvmlDeviceGetCurrPcieLinkWidth(d, out width) == 0
                ? $"PCIE {gen}.0 ×{width}" + (nvmlDeviceGetMaxPcieLinkGeneration(d, out maxGen) == 0 && maxGen > gen ? $" (UP TO {maxGen}.0)" : "")
                : null;
            return v with
            {
                PowerW = power / 1000.0,
                PowerLimitW = limit / 1000.0,
                CoreMHz = Ok(nvmlDeviceGetClockInfo(d, 0, out x), x),
                MemoryMHz = Ok(nvmlDeviceGetClockInfo(d, 2, out x), x) ?? v.MemoryMHz,
                FanPct = Ok(nvmlDeviceGetFanSpeed(d, out x), x),
                TempC = Ok(nvmlDeviceGetTemperature(d, 0, out x), x) is uint t ? t : v.TempC,
                Pcie = pcie,
            };
        }

        static bool Init()
        {
            if (tried) return ready;
            tried = true;
            try
            {
                if (nvmlInit_v2() != 0 || nvmlDeviceGetCount_v2(out uint count) != 0) return false;
                for (uint i = 0; i < count; i++)
                {
                    if (nvmlDeviceGetHandleByIndex_v2(i, out var d) != 0) continue;
                    var sb = new StringBuilder(96);
                    if (nvmlDeviceGetName(d, sb, (uint)sb.Capacity) == 0) Devices[sb.ToString()] = d;
                }
                ready = true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return ready;
        }

        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint gen);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetMaxPcieLinkGeneration(IntPtr device, out uint gen);
    }
}
