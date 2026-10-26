// Whether an NVIDIA card's driver is the latest, from NVIDIA's own driver lookup (the one its download
// page uses): once a day, in the background, asking only for the card's model and Windows' version.
// The one thing VRAM Meter reads from the internet; off in the tray menu. AMD and Intel have no such
// lookup (and their sites turn automated requests away), so for their GPUs the DRIVER tile links to
// their own update tools instead: AMD's Auto-Detect and Install, Intel's Driver & Support Assistant.
using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VramMeter;

public sealed record LatestDriver(string Version, string Released, string Url);

public static class DriverCheck
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static Task? running;

    // NVIDIA's numbering for a Windows driver version: 32.0.16.1742 -> 617.42 (the last five digits).
    public static string? NvidiaVersion(string? windowsVersion)
    {
        if (windowsVersion == null) return null;
        var digits = string.Concat(windowsVersion.Split('.').Skip(2));
        return digits.Length >= 5 ? $"{digits[^5..^2]}.{digits[^2..]}" : null;
    }

    // Where AMD's and Intel's own tools check a PC for newer drivers; null for any other GPU.
    public static (string Label, string Url)? VendorTool(Gpu gpu) => gpu.Vendor switch
    {
        GpuVendor.Amd => ("CHECK AMD ›", "https://www.amd.com/en/support/download/drivers.html"),
        GpuVendor.Intel => ("CHECK INTEL ›", "https://www.intel.com/content/www/us/en/support/detect.html"),
        _ => null,
    };

    // The latest driver for this card, as last looked up; looks again in the background once a day.
    public static LatestDriver? Latest(Gpu gpu, Settings settings)
    {
        if (gpu.Vendor != GpuVendor.Nvidia || !settings.DriverCheck) return null;
        if ((DateTime.UtcNow - settings.DriverCheckedAt).TotalHours >= 24 && running is not { IsCompleted: false })
            running = Task.Run(() => Lookup(gpu, settings));
        return settings.LatestDriver;
    }

    static async Task Lookup(Gpu gpu, Settings settings)
    {
        try
        {
            // The card's product and series ids, from NVIDIA's product list, by its exact name.
            var list = await Http.GetStringAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3");
            // (Its tags have line breaks between them.)
            var m = Regex.Match(list, $@"<LookupValue ParentID=""(\d+)"">\s*<Name>\s*{Regex.Escape(gpu.Name)}\s*</Name>\s*<Value>(\d+)</Value>");
            if (!m.Success) throw new InvalidOperationException("The card isn't in NVIDIA's list.");
            string series = m.Groups[1].Value, product = m.Groups[2].Value;
            // Windows 11, 64-bit (57), English, WHQL Game Ready, DCH: the newest one.
            var json = await Http.GetStringAsync("https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                $"?func=DriverManualLookup&psid={series}&pfid={product}&osID=57&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1");
            using var doc = JsonDocument.Parse(json);
            var info = doc.RootElement.GetProperty("IDS")[0].GetProperty("downloadInfo");
            settings.LatestDriver = new LatestDriver(
                info.GetProperty("Version").GetString() ?? "",
                info.GetProperty("ReleaseDateTime").GetString() ?? "",
                info.GetProperty("DetailsURL").GetString() ?? "https://www.nvidia.com/drivers");
            settings.DriverCheckedAt = DateTime.UtcNow;
            settings.Save();
        }
        catch (Exception)
        {
            // Offline, or NVIDIA changed its lookup: keep what was known, and try again in an hour
            // (never sooner: a failure must not become a request every second).
            settings.DriverCheckedAt = DateTime.UtcNow.AddHours(-23);
        }
    }

    // Newer, by NVIDIA's numbering (621.01 > 617.42).
    public static bool IsNewer(string latest, string installed) =>
        decimal.TryParse(latest, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var a)
        && decimal.TryParse(installed, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var b)
        && a > b;
}
