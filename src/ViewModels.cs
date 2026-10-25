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

    // focus and other are bytes of total; the bar's columns share it by those weights.
    public void Update(Gpu gpu, long used, long total, string? focusTitle, long focus)
    {
        long other = System.Math.Max(0, used - focus);
        Name = gpu.Name.ToUpperInvariant();
        // The maker's mark: NVIDIA's eye is square; AMD's and Intel's wordmarks are wide, so shorter.
        (string? key, string? brush, double height) mark = gpu.Vendor switch
        {
            GpuVendor.Nvidia => ("NvidiaMark", "NvidiaGreen", 40),
            GpuVendor.Amd => ("AmdMark", "AmdBrand", 22),
            GpuVendor.Intel => ("IntelMark", "IntelBrand", 30),
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

    string title = "", exe = "", overText = "", vram = "", sharedRam = "", choice = "", nowOn = "";
    string? moveTip, endTip;
    bool isFocus, canEnd, canMove;
    double barWidth;
    Brush barFill = Format.Res("OtherStripes"), vramBrush = Format.Res("Fg");

    public string Title { get => title; set => Set(ref title, value); }
    public string Exe { get => exe; set => Set(ref exe, value); }
    public string OverText { get => overText; set => Set(ref overText, value); }
    public string Vram { get => vram; set => Set(ref vram, value); }
    public Brush VramBrush { get => vramBrush; set => Set(ref vramBrush, value); }
    public string SharedRam { get => sharedRam; set => Set(ref sharedRam, value); }
    public double BarWidth { get => barWidth; set => Set(ref barWidth, value); }
    public Brush BarFill { get => barFill; set => Set(ref barFill, value); }
    public bool IsFocus { get => isFocus; set => Set(ref isFocus, value); }
    public string Choice { get => choice; set => Set(ref choice, value); }
    public string NowOn { get => nowOn; set => Set(ref nowOn, value); }
    public bool CanMove { get => canMove; set => Set(ref canMove, value); }
    public string? MoveTip { get => moveTip; set => Set(ref moveTip, value); }
    public bool CanEnd { get => canEnd; set => Set(ref canEnd, value); }
    public string? EndTip { get => endTip; set => Set(ref endTip, value); }
    public bool IsOver { get; private set; }

    public void Update(AppGroup app, Snapshot s, bool over, bool focus, long maxPlain, GpuChoice pref)
    {
        App = app;
        IsOver = over;
        long ded = app.TotalDedicated;
        Title = app.Title.ToUpperInvariant();
        Exe = System.IO.Path.GetFileName(app.Path ?? app.Name + ".exe") + (app.Procs.Count > 1 ? $" ×{app.Procs.Count}" : "");
        OverText = over ? "   OVERCOUNTED" : "";
        Vram = Format.Gb(ded);
        VramBrush = Format.Res(over ? "Warn" : "Fg");
        SharedRam = Format.Gb(app.Shared);
        BarWidth = over ? 150 : 150.0 * ded / System.Math.Max(1, maxPlain);
        IsFocus = focus;
        BarFill = Format.Res(focus ? "FocusStripes" : "OtherStripes");

        // Where its memory is now: the GPU holding most of it.
        var on = app.Dedicated.Where(d => d.Value > 0).OrderByDescending(d => d.Value).Select(d => d.Key).ToList();
        NowOn = on.Count == 0 ? "" : "ON " + string.Join(" + ", on.Select(l => s.Gpus.First(g => g.Luid == l).ShortName.ToUpperInvariant()));
        Choice = pref switch
        {
            GpuChoice.PowerSaving => "POWER SAVING",
            GpuChoice.HighPerformance => "HIGH PERFORMANCE",
            _ => "AUTO",
        };

        CanEnd = app.ProtectedWhy == null;
        EndTip = app.ProtectedWhy ?? $"Close {app.Title}";
        CanMove = app.ProtectedWhy == null && app.Path != null;
        MoveTip = app.ProtectedWhy != null ? app.ProtectedWhy
            : app.Path == null ? "Windows won't say where this app is installed, so its GPU can't be set."
            : "Choose the GPU it runs on";
    }
}
