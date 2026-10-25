using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace VramMeter;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

static class Format
{
    const double GB = 1024.0 * 1024 * 1024;
    public static string Gb(long bytes) => (bytes / GB).ToString("0.00") + " GB";
    public static string Gb1(long bytes) => (bytes / GB).ToString("0.0");

    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}

public sealed class MeterVm : Observable
{
    public long Luid { get; init; }

    string name = "", focusTitle = "", focusValue = "", otherValue = "", totalText = "";
    Geometry? logo;
    Brush? logoBrush;
    double logoHeight;
    GridLength focusWidth, otherWidth, freeWidth = new(1, GridUnitType.Star);

    public string Name { get => name; set => Set(ref name, value); }
    public Geometry? Logo { get => logo; set { Set(ref logo, value); Raise(nameof(LogoVisibility)); } }
    public Brush? LogoBrush { get => logoBrush; set => Set(ref logoBrush, value); }
    public double LogoHeight { get => logoHeight; set => Set(ref logoHeight, value); }
    public Visibility LogoVisibility => logo == null ? Visibility.Collapsed : Visibility.Visible;
    public string FocusTitle { get => focusTitle; set => Set(ref focusTitle, value); }
    public string FocusValue { get => focusValue; set => Set(ref focusValue, value); }
    public string OtherValue { get => otherValue; set => Set(ref otherValue, value); }
    public string TotalText { get => totalText; set => Set(ref totalText, value); }
    public GridLength FocusWidth { get => focusWidth; set => Set(ref focusWidth, value); }
    public GridLength OtherWidth { get => otherWidth; set => Set(ref otherWidth, value); }
    public GridLength FreeWidth { get => freeWidth; set => Set(ref freeWidth, value); }

    // The GPU's vitals as tiles (label over value), and a line per monitor plugged into it.
    // Each kind of tile is a fixed width, so a figure changing length doesn't shift the rest.
    public sealed record Tile(string Label, string Value)
    {
        public double Width => Label switch
        {
            "USE" => 70,
            "TEMP" => 110,
            "POWER" => 120,
            "CORE" => 115,
            "MEMORY" => 125,
            "FAN" => 155,
            "LINK" => 135,
            _ => 145,
        };
    }

    List<Tile> tiles = new();
    List<string> monitors = new();
    public List<Tile> Tiles { get => tiles; set => Set(ref tiles, value); }
    public List<string> Monitors { get => monitors; set => Set(ref monitors, value); }

    public void UpdateVitals(Gpu gpu, Reading r)
    {
        var v = r.Vitals.GetValueOrDefault(gpu.Luid) ?? new GpuVitals();
        var t = new List<Tile> { new("USE", $"{r.Use.GetValueOrDefault(gpu.Luid):0}%") };
        if (v.TempC is double c) t.Add(new("TEMP", $"{c:0}°C" + (v.TempMaxC is double max ? $" / {max:0}" : "")));
        if (v.PowerW is double w) t.Add(new("POWER", $"{w:0} W" + (v.PowerLimitW is double lim ? $" / {lim:0}" : "")));
        else if (v.PowerPct is double pct) t.Add(new("POWER", $"{pct:0}%"));
        if (v.CoreMHz is uint core) t.Add(new("CORE", $"{core} MHZ"));
        if (v.MemoryMHz is uint mem && mem > 0) t.Add(new("MEMORY", $"{mem} MHZ"));
        if (v.FanPct is uint fan) t.Add(new("FAN", $"{fan}%" + (v.FanRpm is uint rpm && rpm > 0 ? $" · {rpm} RPM" : "")));
        else if (v.FanRpm is uint rpm2) t.Add(new("FAN", $"{rpm2} RPM"));
        if (v.Pcie != null) t.Add(new("LINK", v.Pcie));
        if (v.Driver != null) t.Add(new("DRIVER", v.Driver));
        if (!Tiles.SequenceEqual(t)) Tiles = t;

        var m = r.Monitors.Where(x => x.Luid == gpu.Luid).Select(x =>
            $"{x.Name.ToUpperInvariant()}  ·  {x.Cable}  ·  " +
            (x.Showing is { } s ? $"{s.Width}×{s.Height} @ {System.Math.Round(s.Hz)} HZ" + (x.Main ? "  ·  MAIN" : "") : "CONNECTED, OFF")).ToList();
        if (m.Count == 0) m.Add("NO MONITOR PLUGGED IN");
        if (!Monitors.SequenceEqual(m)) Monitors = m;
    }

    // focus and other are bytes of total; the bar's columns share it by those weights.
    public void Update(Gpu gpu, long used, long total, string? focusTitle, long focus)
    {
        long other = System.Math.Max(0, used - focus);
        Name = gpu.Name.ToUpperInvariant();
        // The maker's mark, every one the same height.
        (string? key, string? brush, double height) mark = gpu.Vendor switch
        {
            GpuVendor.Nvidia => ("NvidiaMark", "NvidiaGreen", 34),
            GpuVendor.Amd => ("AmdMark", "AmdBrand", 34),
            GpuVendor.Intel => ("IntelMark", "IntelBrand", 34),
            _ => (null, null, 0),
        };
        Logo = mark.key == null ? null : (Geometry)Application.Current.Resources[mark.key];
        LogoBrush = mark.brush == null ? null : Format.Res(mark.brush);
        LogoHeight = mark.height;
        FocusTitle = (focusTitle ?? "NO APP").ToUpperInvariant();
        FocusValue = Format.Gb(focus);
        OtherValue = Format.Gb(other);
        TotalText = $"{Format.Gb1(used)}/{Format.Gb1(total)} GB";
        FocusWidth = new GridLength(focus, GridUnitType.Star);
        OtherWidth = new GridLength(other, GridUnitType.Star);
        FreeWidth = new GridLength(System.Math.Max(0, total - focus - other), GridUnitType.Star);
    }
}

public sealed class AppRowVm : Observable
{
    public required string Key { get; init; }
    public AppGroup App { get; private set; } = null!;

    string title = "", exe = "", vram = "", sharedRam = "", choice = "", nowOn = "";
    string? moveTip, endTip;
    bool isFocus, canEnd, canMove;
    double barWidth;
    Brush barFill = Format.Res("OtherStripes");

    public string Title { get => title; set => Set(ref title, value); }
    public string Exe { get => exe; set => Set(ref exe, value); }
    public string Vram { get => vram; set => Set(ref vram, value); }
    public string SharedRam { get => sharedRam; set => Set(ref sharedRam, value); }
    public double BarWidth { get => barWidth; set => Set(ref barWidth, value); }
    public Brush BarFill { get => barFill; set => Set(ref barFill, value); }
    public bool IsFocus { get => isFocus; set => Set(ref isFocus, value); }
    public string Choice { get => choice; set => Set(ref choice, value); }
    public string NowOn { get => nowOn; set => Set(ref nowOn, value); }
    Brush nowOnBrush = Format.Res("Dim");
    public Brush NowOnBrush { get => nowOnBrush; set => Set(ref nowOnBrush, value); }
    public bool CanMove { get => canMove; set => Set(ref canMove, value); }
    public string? MoveTip { get => moveTip; set => Set(ref moveTip, value); }
    public bool CanEnd { get => canEnd; set => Set(ref canEnd, value); }
    public string? EndTip { get => endTip; set => Set(ref endTip, value); }

    public void Update(AppGroup app, Snapshot s, bool focus, long max, GpuChoice pref)
    {
        App = app;
        long ded = app.TotalDedicated;
        Title = app.Title.ToUpperInvariant();
        Exe = System.IO.Path.GetFileName(app.Path ?? app.Name + ".exe") + (app.Procs.Count > 1 ? $" ×{app.Procs.Count}" : "");
        Vram = Format.Gb(ded);
        SharedRam = Format.Gb(app.Shared);
        BarWidth = 150.0 * ded / System.Math.Max(1, max);
        IsFocus = focus;
        BarFill = Format.Res(focus ? "FocusStripes" : "OtherStripes");

        // Where it runs: the GPU holding most of its memory. An app drawn on one GPU and shown on a
        // screen plugged into another keeps a little on that one too, for the frames it hands over.
        string Short(long luid) => s.Gpus.First(g => g.Luid == luid).ShortName.ToUpperInvariant();
        var on = app.Dedicated.Where(d => d.Value > 0).OrderByDescending(d => d.Value).ToList();
        NowOn = on.Count == 0 ? ""
            : "ON " + Short(on[0].Key) + string.Concat(on.Skip(1).Select(d => $"  +{d.Value / (1024.0 * 1024 * 1024):0.00} GB {Short(d.Key)}"));

        // Set to one GPU but running on the other: it started before the setting, or Windows didn't
        // apply it (some Windows builds ignore it for apps opened from Start, the taskbar or at sign-in).
        var wanted = pref switch
        {
            GpuChoice.PowerSaving => s.Gpus.FirstOrDefault(g => g.PowerSaving),
            GpuChoice.HighPerformance => s.Gpus.FirstOrDefault(g => g.HighPerformance),
            _ => null,
        };
        bool astray = wanted != null && on.Count > 0 && on[0].Key != wanted.Luid;
        NowOnBrush = Format.Res(astray ? "Warn" : "Dim");
        Choice = pref switch
        {
            GpuChoice.PowerSaving => s.Gpus.FirstOrDefault(g => g.PowerSaving)?.ShortName.ToUpperInvariant() ?? "POWER SAVING",
            GpuChoice.HighPerformance => s.Gpus.FirstOrDefault(g => g.HighPerformance)?.ShortName.ToUpperInvariant() ?? "HIGH PERFORMANCE",
            _ => "AUTO",
        };

        CanEnd = app.ProtectedWhy == null;
        EndTip = app.ProtectedWhy ?? $"Close {app.Title}";
        CanMove = app.ProtectedWhy == null && app.Path != null;
        MoveTip = app.ProtectedWhy != null ? app.ProtectedWhy
            : app.Path == null ? "Windows won't say where this app is installed, so its GPU can't be set."
            : astray ? $"Set to the {wanted!.ShortName}, but running on the {Short(on[0].Key)}: Windows didn't apply the setting. " +
                       "Either it started before the setting, or this is a Windows bug on some builds, which ignore it for apps " +
                       "opened from Start, the taskbar or at sign-in. Reopening it applies the setting only in the first case."
            : "Choose the GPU it runs on";
    }
}
