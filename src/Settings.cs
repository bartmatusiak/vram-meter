using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace VramMeter;

// What VRAM Meter remembers, in %LOCALAPPDATA%\VRAM Meter\settings.json.
public sealed class Settings
{
    static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRAM Meter", "settings.json");

    public bool TrayHintShown { get; set; }

    // Windows starts on the power saving GPU's display (saved), so the apps that start at sign-in land
    // on that GPU; VRAM Meter then shows only the fast GPU's display (not saved). See Displays.
    public bool SignInOnPowerSaving { get; set; }

    // The monitor's input code for each cable (DP, HDMI), learned over DDC/CI (see MonitorInput).
    public Dictionary<string, uint> InputCodes { get; set; } = new();

    // Checking NVIDIA for a newer driver once a day (see DriverCheck), and what it found.
    public bool DriverCheck { get; set; } = true;
    public DateTime DriverCheckedAt { get; set; }
    public LatestDriver? LatestDriver { get; set; }

    // The badge on each display (see Overlay).
    public bool Overlay { get; set; } = true;

    // Where you've dragged each display's badge: x, y from the display's top-left, in pixels.
    public Dictionary<string, int[]> OverlaySpots { get; set; } = new();

    // Sort each new app between the GPUs as it shows up.
    public bool AutoSort { get; set; }

    // Apps whose GPU is settled (chosen by you, or sorted): auto-sort leaves them alone.
    public HashSet<string> Decided { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Starting with Windows: a value in HKCU\...\Run, as Task Manager's Startup apps lists it.
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "VRAM Meter";

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue(RunName, throwOnMissingValue: false);
        }
    }
}
