// Switching the monitor's own input to follow the display switch, over DDC/CI (the monitor's command
// channel; it must be on in the monitor's menu). Input codes are MCCS VCP 0x60: 0x0F DisplayPort 1,
// 0x10 DisplayPort 2, 0x11 HDMI 1, 0x12 HDMI 2, ... Which HDMI port a cable is in can't be asked, so it's
// learned: a code the monitor reports while it isn't on DisplayPort, with an HDMI display connected,
// is that HDMI cable's.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace VramMeter;

public static class MonitorInput
{
    const byte InputSource = 0x60;
    static readonly uint[] DisplayPorts = { 0x0F, 0x10 };
    static readonly uint[] Hdmis = { 0x11, 0x12 };

    // The monitor's current input code, read through any display showing; null without DDC/CI.
    public static uint? Current()
    {
        uint? code = null;
        Each(h => { if (code == null && GetVCPFeatureAndVCPFeatureReply(h, InputSource, IntPtr.Zero, out uint cur, out _)) code = cur & 0xFF; });
        return code;
    }

    // Learns which code each cable is: called with the monitors connected now.
    public static void Learn(Settings settings, IEnumerable<Displays.Monitor> monitors)
    {
        if (Current() is not uint code) return;
        var cables = monitors.Select(m => m.Cable).ToHashSet();
        bool changed = false;
        if (DisplayPorts.Contains(code) && cables.Contains("DP")) changed |= Remember(settings, "DP", code);
        else if (Hdmis.Contains(code) && cables.Contains("HDMI")) changed |= Remember(settings, "HDMI", code);
        if (changed) settings.Save();
    }

    static bool Remember(Settings settings, string cable, uint code)
    {
        lock (settings)  // not while Save is writing it out
        {
            if (settings.InputCodes.TryGetValue(cable, out var known) && known == code) return false;
            settings.InputCodes[cable] = code;
            return true;
        }
    }

    // Switches the monitor to the input a cable is plugged into; false without DDC/CI.
    public static bool SwitchTo(Settings settings, string cable)
    {
        uint code = settings.InputCodes.TryGetValue(cable, out var known) ? known : cable switch
        {
            "DP" => 0x0F,
            "HDMI" => 0x11,
            _ => 0,
        };
        if (code == 0 || Current() == code) return code != 0;
        bool ok = false;
        Each(h => { if (!ok) ok = SetVCPFeature(h, InputSource, code); });
        return ok;
    }

    // Runs action on each physical monitor behind the displays showing now.
    static void Each(Action<IntPtr> action)
    {
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint n) || n == 0) return true;
            var monitors = new PhysicalMonitor[n];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, n, monitors)) return true;
            try { foreach (var m in monitors) action(m.Handle); }
            finally { DestroyPhysicalMonitors(n, monitors); }
            return true;
        }, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    delegate bool MonitorEnum(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
}
