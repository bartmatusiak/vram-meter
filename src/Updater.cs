// Updates VRAM Meter itself, from its GitHub releases: on start and every 6 hours it asks GitHub for the latest
// release, and when that is newer than this one, the window and the tray offer UPDATE TO x.y.z. One click downloads
// the release's VramMeterSetup.exe, checks it against the SHA-256 GitHub publishes for it, and runs it silently: the
// setup closes this VRAM Meter, installs over it (keeping its settings and choices) and opens the new one. The one
// other thing VRAM Meter reads from the internet (see DriverCheck); off in the tray menu.
// A VramMeter.exe run on its own, not from the setup's folder, has nothing to install over: there the button opens
// the release's page instead.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace VramMeter;

public sealed record Release(Version Version, string Page, string? Setup, string? Sha256);

public sealed class Updater
{
    const string Latest = "https://api.github.com/repos/bartmatusiak/vram-meter/releases/latest";
    const string SetupName = "VramMeterSetup.exe";

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5), MaxResponseContentBufferSize = 256 << 20 };
        // GitHub's API turns away a request without one.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"VramMeter/{Current.ToString(3)}");
        return http;
    }

    readonly Settings settings;
    readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromHours(6) };
    bool installing;

    // This VRAM Meter's version (the csproj's <Version>).
    public static Version Current { get; } = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    // A newer release than this one, once a check has found one.
    public Release? Available { get; private set; }

    // Raised on the UI thread when a check finds a newer release, or an install fails (with why).
    public event Action<Release>? Found;
    public event Action<string>? Failed;

    // Whether this copy is the setup's (Uninstall.exe beside it), so a setup can install over it.
    public static bool Installed =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Uninstall.exe"));

    public Updater(Settings settings)
    {
        this.settings = settings;
        timer.Tick += async (_, _) => await Check();
    }

    public void Start()
    {
        timer.Start();
        _ = Check();
    }

    async Task Check()
    {
        if (!settings.UpdateCheck || installing) return;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(Latest));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return;
            if (!Version.TryParse((root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V'), out var version)) return;
            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == SetupName);
            string? setup = null, sha = null;
            if (asset.ValueKind == JsonValueKind.Object)
            {
                setup = asset.GetProperty("browser_download_url").GetString();
                // "sha256:<hex>", which GitHub records for every asset uploaded since mid-2025.
                if (asset.TryGetProperty("digest", out var d) && d.GetString() is string digest && digest.StartsWith("sha256:"))
                    sha = digest["sha256:".Length..];
            }
            var release = new Release(Norm(version), root.GetProperty("html_url").GetString() ?? "https://github.com/bartmatusiak/vram-meter/releases", setup, sha);
            if (release.Version <= Norm(Current)) { Available = null; return; }
            bool fresh = Available?.Version != release.Version;
            Available = release;
            if (fresh) Found?.Invoke(release);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Offline, or GitHub answered something else: the next check, in 6 hours, tries again.
        }
    }

    // 1.0 and 1.0.0 are the same release.
    static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // Downloads the setup and runs it silently; it closes this VRAM Meter, installs, and opens the new one (in the
    // tray when `trayOnly`). Without a setup to run (a VramMeter.exe on its own, or a release without one), opens the
    // release's page. Returns once the setup is started, or with a reason it couldn't be.
    public async Task Install(bool trayOnly)
    {
        if (Available is not Release release || installing) return;
        if (!Installed || release.Setup == null || !release.Setup.StartsWith("https://"))
        {
            Open(release.Page);
            return;
        }
        installing = true;
        try
        {
            string file = Path.Combine(Path.GetTempPath(), $"VramMeterSetup-{release.Version.ToString(3)}.exe");
            var bytes = await Http.GetByteArrayAsync(release.Setup);
            if (release.Sha256 != null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download doesn't match the release.");
            await File.WriteAllBytesAsync(file, bytes);
            // Into this copy's own folder; /RELAUNCH opens the new one when the install is done.
            string dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            Process.Start(new ProcessStartInfo(file, $"/S /RELAUNCH={(trayOnly ? "tray" : "window")} /D={dir}") { UseShellExecute = true });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                      or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            installing = false;
            Failed?.Invoke($"Couldn't update to {release.Version.ToString(3)}: {e.Message}");
        }
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
