// Sorting apps between GPUs: games stay on the high performance GPU, everything else moves to the
// power saving one, and capture or overlay tools stay with the games (recording a game across two
// GPUs copies every frame between them).
//
// A game is an .exe Windows' Game Bar has detected as one (HKCU\System\GameConfigStore), or one
// installed in a game library: Steam, EA, Epic, GOG, Ubisoft, Riot, the Xbox app.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace VramMeter;

public enum AppKind { Game, Capture, Other }

public sealed record SortStep(AppGroup App, AppKind Kind, string Why, GpuChoice From, GpuChoice To);

public static class Sorter
{
    static readonly string[] GameFolders =
    {
        @"\steamapps\common\", @"\EA Games\", @"\Origin Games\", @"\Epic Games\", @"\GOG Galaxy\Games\",
        @"\GOG Games\", @"\Ubisoft Game Launcher\games\", @"\Riot Games\", @"\XboxGames\",
    };

    // Launchers that live inside a library folder but aren't games.
    static readonly string[] NotGames = { @"\Epic Games\Launcher\", @"\Riot Games\Riot Client\" };

    static readonly HashSet<string> CaptureTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "obs32", "obs", "discord_clips", "Medal", "MedalEncoder", "Streamlabs OBS", "XSplit.Core",
        "NVIDIA Overlay", "NVIDIA App", "NVIDIA Share", "nvcontainer", "nvsphelper64", "RadeonSoftware",
        "AMDRSServ", "AMDRSSrcExt", "GameBar", "GameBarFTServer", "Overwolf",
    };

    static HashSet<string> detected = new(StringComparer.OrdinalIgnoreCase);
    static DateTime detectedAt;

    public static (AppKind Kind, string Why) Classify(AppGroup app)
    {
        string path = app.Path ?? "";
        if (CaptureTools.Contains(app.Name) || path.Contains(@"\NVIDIA Corporation\", StringComparison.OrdinalIgnoreCase))
            return (AppKind.Capture, "Captures or draws over games: stays with them");
        if (DetectedGames().Contains(path)) return (AppKind.Game, "Windows knows it as a game");
        if (GameFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)) &&
            !NotGames.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)))
            return (AppKind.Game, "Installed in a game library");
        return (AppKind.Other, "Not a game");
    }

    public static GpuChoice Target(AppKind kind) => kind == AppKind.Other ? GpuChoice.PowerSaving : GpuChoice.HighPerformance;

    // What sorting would change for these apps: the ones that can move and aren't there already.
    public static List<SortStep> Plan(IEnumerable<AppGroup> apps)
    {
        var steps = new List<SortStep>();
        foreach (var app in apps)
        {
            if (app.Path == null || app.ProtectedWhy != null) continue;
            var (kind, why) = Classify(app);
            var from = GpuPreferences.Get(app.Path);
            var to = Target(kind);
            if (from != to) steps.Add(new SortStep(app, kind, why, from, to));
        }
        return steps.OrderBy(s => s.Kind).ThenBy(s => s.App.Title).ToList();
    }

    static HashSet<string> DetectedGames()
    {
        if ((DateTime.UtcNow - detectedAt).TotalMinutes < 1) return detected;
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var name in root?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = root!.OpenSubKey(name);
                if (k?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0) games.Add(exe);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        detected = games;
        detectedAt = DateTime.UtcNow;
        return games;
    }
}
