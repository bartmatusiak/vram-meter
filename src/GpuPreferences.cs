// Which GPU an app runs on: Windows' own per-app setting (Settings > System > Display > Graphics),
// kept in HKCU\Software\Microsoft\DirectX\UserGpuPreferences under the app's .exe path. It lasts
// until changed, and an app picks it up the next time it starts. The value can hold other settings
// (Auto HDR, ...), so only its GpuPreference entry is touched.
using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace VramMeter;

public enum GpuChoice { Auto = 0, PowerSaving = 1, HighPerformance = 2 }

public static class GpuPreferences
{
    const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
    const string Entry = "GpuPreference=";

    public static GpuChoice Get(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        if (key?.GetValue(exePath) is not string value) return GpuChoice.Auto;
        var entry = value.Split(';').FirstOrDefault(e => e.StartsWith(Entry, StringComparison.OrdinalIgnoreCase));
        return entry != null && int.TryParse(entry[Entry.Length..], out var n) && Enum.IsDefined((GpuChoice)n)
            ? (GpuChoice)n
            : GpuChoice.Auto;
    }

    public static void Set(string exePath, GpuChoice choice)
    {
        if (!Path.IsPathFullyQualified(exePath) || !File.Exists(exePath))
            throw new ArgumentException("Not an app on this PC: " + exePath);
        using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        var others = ((key.GetValue(exePath) as string) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => !e.StartsWith(Entry, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (choice != GpuChoice.Auto) others.Insert(0, Entry + (int)choice);
        if (others.Count == 0) key.DeleteValue(exePath, throwOnMissingValue: false);
        else key.SetValue(exePath, string.Join(";", others) + ";", RegistryValueKind.String);
    }
}
