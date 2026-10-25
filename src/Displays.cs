// Which display is main, through Windows' display configuration API (SetDisplayConfig).
//
// Windows starts an app on the GPU its main display is plugged into, and (on some builds) ignores the
// per-app GPU setting for apps opened normally. With a monitor cabled to both GPUs, the saved layout
// puts the power saving GPU's display first, so the apps that start at sign-in land on it; once they
// have, VRAM Meter shows only the fast GPU's display, without saving that, so everything opened after
// (games) gets the fast GPU and the next boot starts on the power saving display again.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VramMeter;

public enum DisplayLayout { Unknown, FastOnly, PowerSavingMain, FastMain }

public static class Displays
{
    // Both GPUs drive a display: the setup this needs.
    public static bool Available(Gpu? low, Gpu? high)
    {
        if (low == null || high == null || low.Luid == high.Luid) return false;
        var luids = Paths(QdcAllPaths).Where(p => p.TargetAvailable).Select(p => p.SourceLuid).ToHashSet();
        return luids.Contains(low.Luid) && luids.Contains(high.Luid);
    }

    public static DisplayLayout Current(Gpu low, Gpu high) => Describe(Paths(QdcOnlyActivePaths, out var modes), modes, low, high);

    // The layout Windows has saved for these monitors: what the next boot starts with.
    public static DisplayLayout Saved(Gpu low, Gpu high) => Describe(Paths(QdcDatabaseCurrent, out var modes), modes, low, high);

    // Only the fast GPU's displays, not saved: games opened from now on get the fast GPU.
    public static void FastOnly(Gpu low, Gpu high, bool save = false)
    {
        EnsureExtended();
        var paths = Paths(QdcOnlyActivePaths, out var modes);
        var keep = paths.Where(p => p.SourceLuid != low.Luid).ToList();
        if (keep.Count == 0) throw new InvalidOperationException("No display is plugged into the " + high.ShortName + ".");
        Apply(keep, modes, save);
    }

    // Every display on, the power saving GPU's first (main): saved, so each boot starts this way.
    public static void PowerSavingMain(Gpu low, Gpu high, bool save = true)
    {
        EnsureExtended();
        var paths = Paths(QdcOnlyActivePaths, out var modes);
        if (!paths.Any(p => p.SourceLuid == low.Luid)) throw new InvalidOperationException("No display is plugged into the " + low.ShortName + ".");
        Apply(paths.OrderBy(p => p.SourceLuid == low.Luid ? 0 : 1).ToList(), modes, save);
    }

    // Back to the layout Windows has saved for these monitors.
    public static void Saved()
    {
        int rc = SetDisplayConfig(0, null, 0, null, SdcApply | SdcUseDatabaseCurrent);
        if (rc != 0) throw new InvalidOperationException($"Windows refused the display change (error {rc}).");
    }

    // Lays the paths out left to right in order (the first at 0,0 is main) and applies them.
    static void Apply(List<Path> paths, Mode[] modes, bool save)
    {
        // Only the modes these paths use, renumbered.
        var used = new List<Mode>();
        uint Take(uint index)
        {
            if (index == Invalid || index >= modes.Length) return Invalid;
            used.Add(modes[index]);
            return (uint)used.Count - 1;
        }
        var raw = new PathInfo[paths.Count];
        int x = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i].Raw;
            p.sourceModeInfoIdx = Take(p.sourceModeInfoIdx);
            p.targetModeInfoIdx = Take(p.targetModeInfoIdx);
            if (p.sourceModeInfoIdx != Invalid)
            {
                var m = used[(int)p.sourceModeInfoIdx];
                m.positionX = x;
                m.positionY = 0;
                x += (int)m.width;
                used[(int)p.sourceModeInfoIdx] = m;
            }
            raw[i] = p;
        }
        var outModes = used.ToArray();
        uint flags = SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | (save ? SdcSaveToDatabase : 0);
        int rc = SetDisplayConfig((uint)raw.Length, raw, (uint)outModes.Length, outModes, flags);
        if (rc != 0) throw new InvalidOperationException($"Windows refused the display change (error {rc}).");
    }

    // Turns on every connected display (extended), not saved, so both GPUs' displays can be arranged.
    static void EnsureExtended()
    {
        var active = Paths(QdcOnlyActivePaths).Select(p => p.SourceLuid).Distinct().Count();
        var available = Paths(QdcAllPaths).Where(p => p.TargetAvailable).Select(p => p.SourceLuid).Distinct().Count();
        if (active < available) SetDisplayConfig(0, null, 0, null, SdcApply | SdcTopologyExtend);
    }

    // Each display showing now: its Windows name (\\.\DISPLAY1), GPU, cable, size, refresh and place.
    public sealed record Active(string Device, long Luid, string Cable, int Width, int Height, double Hz, int X, int Y);

    public static List<Active> ActiveDisplays()
    {
        var list = new List<Active>();
        var paths = Paths(QdcOnlyActivePaths, out var modes);
        foreach (var p in paths)
        {
            var r = p.Raw;
            if (r.sourceModeInfoIdx >= modes.Length) continue;
            var m = modes[r.sourceModeInfoIdx];
            var name = new SourceName { type = 1, size = Marshal.SizeOf<SourceName>(), adapterLow = r.sourceAdapterLow, adapterHigh = r.sourceAdapterHigh, id = r.sourceId };
            if (DisplayConfigGetDeviceInfo(ref name) != 0) continue;
            double hz = r.refreshDenominator == 0 ? 0 : (double)r.refreshNumerator / r.refreshDenominator;
            list.Add(new Active(name.gdiName, p.SourceLuid, Cable(r.outputTechnology), (int)m.width, (int)m.height, hz, m.positionX, m.positionY));
        }
        return list;
    }

    static string Cable(int tech) => tech switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        10 => "DP",
        11 => "DP",
        12 => "UDI",
        13 => "UDI",
        16 => "MIRACAST",
        unchecked((int)0x80000000) => "BUILT-IN",
        _ => "DISPLAY",
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName
    {
        public int type, size;
        public uint adapterLow;
        public int adapterHigh;
        public uint id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiName;
    }

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref SourceName info);

    static DisplayLayout Describe(List<Path> paths, Mode[] modes, Gpu low, Gpu high)
    {
        if (paths.Count == 0) return DisplayLayout.Unknown;
        if (paths.All(p => p.SourceLuid != low.Luid)) return DisplayLayout.FastOnly;
        var main = paths.FirstOrDefault(p => p.Raw.sourceModeInfoIdx < modes.Length
            && modes[p.Raw.sourceModeInfoIdx].positionX == 0 && modes[p.Raw.sourceModeInfoIdx].positionY == 0);
        if (main == null) return DisplayLayout.Unknown;
        return main.SourceLuid == low.Luid ? DisplayLayout.PowerSavingMain : DisplayLayout.FastMain;
    }

    sealed record Path(PathInfo Raw)
    {
        public long SourceLuid => ((long)Raw.sourceAdapterHigh << 32) | Raw.sourceAdapterLow;
        public bool TargetAvailable => Raw.targetAvailable != 0;
    }

    static List<Path> Paths(uint flags) => Paths(flags, out _);

    static List<Path> Paths(uint flags, out Mode[] modes)
    {
        modes = Array.Empty<Mode>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(flags, out uint np, out uint nm) != 0) return new();
            var paths = new PathInfo[np];
            var m = new Mode[nm];
            int rc = flags == QdcDatabaseCurrent
                ? QueryDisplayConfig(flags, ref np, paths, ref nm, m, out _)
                : QueryDisplayConfig(flags, ref np, paths, ref nm, m, IntPtr.Zero);
            if (rc == 122) continue;  // ERROR_INSUFFICIENT_BUFFER: the setup changed; ask again
            if (rc != 0) return new();
            modes = m.Take((int)nm).ToArray();
            return paths.Take((int)np).Select(p => new Path(p)).ToList();
        }
        return new();
    }

    const uint Invalid = 0xFFFFFFFF;
    const uint QdcAllPaths = 0x1, QdcOnlyActivePaths = 0x2, QdcDatabaseCurrent = 0x4;
    const uint SdcTopologyExtend = 0x4, SdcUseDatabaseCurrent = 0xF, SdcUseSuppliedDisplayConfig = 0x20, SdcApply = 0x80,
               SdcSaveToDatabase = 0x200, SdcAllowChanges = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo
    {
        public uint sourceAdapterLow;
        public int sourceAdapterHigh;
        public uint sourceId, sourceModeInfoIdx, sourceStatusFlags;
        public uint targetAdapterLow;
        public int targetAdapterHigh;
        public uint targetId, targetModeInfoIdx;
        public int outputTechnology, rotation, scaling;
        public uint refreshNumerator, refreshDenominator;
        public int scanLineOrdering, targetAvailable;
        public uint targetStatusFlags, flags;
    }

    // DISPLAYCONFIG_MODE_INFO: a source mode's size and position at the start of its 48-byte union.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct Mode
    {
        [FieldOffset(0)] public int infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public uint adapterLow;
        [FieldOffset(12)] public int adapterHigh;
        [FieldOffset(16)] public uint width;
        [FieldOffset(20)] public uint height;
        [FieldOffset(24)] public int pixelFormat;
        [FieldOffset(28)] public int positionX;
        [FieldOffset(32)] public int positionY;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] Mode[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] Mode[] modes, out int topology);

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, [In] PathInfo[]? paths, uint numModes, [In] Mode[]? modes, uint flags);
}
