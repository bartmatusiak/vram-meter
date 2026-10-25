using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VramMeter;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    readonly GpuMonitor monitor = new();
    readonly DispatcherTimer timer = new();
    readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly Settings settings = Settings.Load();
    bool reading;

    // The app you clicked to measure, until the window closes or you click it again; otherwise the
    // biggest game running, and failing that the biggest app.
    string? focusKey;

    public Settings Settings => settings;
    string status = "", note = "";
    Brush statusBrush = Format.Res("Focus");

    public ObservableCollection<MeterVm> Meters { get; } = new();
    public ObservableCollection<AppRowVm> Apps { get; } = new();
    public Snapshot? Last { get; private set; }
    public bool Exiting { get; set; }

    // The main GPU's figures, for the tray: bytes for the focus app, everyone else, and the card.
    public event Action<long, long, long, string?>? Reading;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Status { get => status; set { status = value; Raise(nameof(Status)); Raise(nameof(StatusVisibility)); } }
    public Visibility StatusVisibility => status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Brush StatusBrush { get => statusBrush; set { statusBrush = value; Raise(nameof(StatusBrush)); } }
    public string Note { get => note; set { note = value; Raise(nameof(Note)); Raise(nameof(NoteVisibility)); } }
    public Visibility NoteVisibility => note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // The two GPUs sorting moves apps between, when there are two.
    (Gpu Low, Gpu High)? Pair
    {
        get
        {
            var low = Last?.Gpus.FirstOrDefault(g => g.PowerSaving);
            var high = Last?.Gpus.FirstOrDefault(g => g.HighPerformance);
            return low != null && high != null && low.Luid != high.Luid ? (low, high) : null;
        }
    }
    public bool CanSort => Pair != null;
    public string SortTip => CanSort
        ? "Keep games on the fast GPU and move everything else to the other one"
        : "Only one GPU is on. Turn on the CPU's graphics in the BIOS to sort apps between two.";
    public string AutoSortText => settings.AutoSort ? "NEW APPS: SORTED AUTOMATICALLY" : "NEW APPS: LEFT AS THEY ARE";

    // Tells the tray, when the window is hidden, what auto-sort just moved.
    public event Action<string>? AutoSorted;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Grain.Fill = GrainBrush();
        timer.Tick += async (_, _) => await Refresh();
        statusTimer.Tick += (_, _) => { Status = ""; statusTimer.Stop(); };
        IsVisibleChanged += (_, _) => Pace();
        Pace();
        timer.Start();
        _ = Refresh();
    }

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Every 1.5 s while you're looking, every 4 s for the tray icon alone.
    void Pace() => timer.Interval = TimeSpan.FromSeconds(IsVisible ? 1.5 : 4);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // A dark title bar in the page's black.
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x000B0C0B;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }

    // Closing the window leaves VRAM Meter in the tray; Exit is on the tray icon.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!Exiting)
        {
            e.Cancel = true;
            focusKey = null;
            Hide();
        }
        base.OnClosing(e);
    }

    async Task Refresh()
    {
        if (reading) return;
        reading = true;
        try
        {
            var s = await Task.Run(monitor.Read);
            Last = s;
            Raise(nameof(CanSort));
            Raise(nameof(SortTip));
            AutoSort(s);
            Draw(s);
        }
        catch (Exception ex)
        {
            Note = "No reading from Windows' GPU counters: " + ex.Message;
        }
        finally { reading = false; }
    }

    void Draw(Snapshot s)
    {
        // Meters: every GPU in use, or the first when none is.
        var shown = s.Gpus.Where(g => s.Used.GetValueOrDefault(g.Luid) > 0).ToList();
        if (shown.Count == 0 && s.Gpus.Count > 0) shown.Add(s.Gpus[0]);
        Sync(Meters, shown, g => g.Luid, m => m.Luid, g => new MeterVm { Luid = g.Luid });
        var focusKeys = new HashSet<string>();
        bool first = true;
        foreach (var g in shown)
        {
            long used = s.Used.GetValueOrDefault(g.Luid);
            var candidates = s.Apps.Where(a => a.Dedicated.GetValueOrDefault(g.Luid) > 0).ToList();
            var focus = candidates.FirstOrDefault(a => a.Key == focusKey)
                        ?? candidates.Where(a => Sorter.Classify(a).Kind == AppKind.Game).OrderByDescending(a => a.Dedicated[g.Luid]).FirstOrDefault()
                        ?? candidates.OrderByDescending(a => a.Dedicated[g.Luid]).FirstOrDefault();
            long focusBytes = focus == null ? 0 : Math.Min(focus.Dedicated[g.Luid], used);
            if (focus != null) focusKeys.Add(focus.Key);
            Meters.First(m => m.Luid == g.Luid).Update(g, used, Math.Max(g.Total, used), focus?.Title, focusBytes);
            if (first) Reading?.Invoke(focusBytes, Math.Max(0, used - focusBytes), Math.Max(g.Total, used), focus?.Title);
            first = false;
        }

        long max = s.Apps.Select(a => a.TotalDedicated).DefaultIfEmpty(1).Max();
        Sync(Apps, s.Apps, a => a.Key, r => r.Key, a => new AppRowVm { Key = a.Key });
        foreach (var a in s.Apps)
        {
            var pref = a.Path != null ? GpuPreferences.Get(a.Path) : GpuChoice.Auto;
            Apps.First(r => r.Key == a.Key).Update(a, s, focusKeys.Contains(a.Key), max, pref);
        }
    }

    // Brings a list in line with the reading without rebuilding it, so rows keep their place and state.
    static void Sync<TData, TVm>(ObservableCollection<TVm> list, List<TData> data, Func<TData, object> dataKey,
        Func<TVm, object> vmKey, Func<TData, TVm> make)
    {
        var keys = data.Select(dataKey).ToList();
        for (int i = list.Count - 1; i >= 0; i--)
            if (!keys.Contains(vmKey(list[i]))) list.RemoveAt(i);
        for (int i = 0; i < data.Count; i++)
        {
            int at = -1;
            for (int j = 0; j < list.Count; j++)
                if (Equals(vmKey(list[j]), keys[i])) { at = j; break; }
            if (at < 0) list.Insert(i, make(data[i]));
            else if (at != i) list.Move(at, i);
        }
    }

    void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppRowVm row) return;
        focusKey = focusKey == row.Key ? null : row.Key;
        if (Last != null) Draw(Last);
    }

    void End_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppRowVm row) return;
        var app = row.App;
        int n = app.Procs.Count;
        string what = n > 1 ? $"all {n} of its processes ({app.Name}.exe)" : $"{app.Name}.exe";
        if (!HudDialog.Ask(this, $"END {app.Title.ToUpperInvariant()}?",
                $"This closes {what} straight away, like End task in Task Manager. Anything unsaved in it is lost.",
                "END IT", danger: true))
            return;
        try
        {
            var r = Processes.End(app);
            if (r.Refused.Count > 0)
                Say($"{app.Title}: ended {r.Ended}, Windows refused {r.Refused.Count}. It may run as administrator; VRAM Meter never does, so end it from Task Manager.", warn: true);
            else
                Say($"Ended {app.Title}.");
        }
        catch (Exception ex) { Say($"Couldn't end {app.Title}: {ex.Message}", warn: true); }
        _ = Refresh();
    }

    void Gpu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not AppRowVm row || row.App.Path == null || Last == null) return;
        var app = row.App;
        var current = GpuPreferences.Get(app.Path);
        var low = Last.Gpus.FirstOrDefault(g => g.PowerSaving);
        var high = Last.Gpus.FirstOrDefault(g => g.HighPerformance);
        bool oneGpu = low == null || high == null || low.Luid == high.Luid;

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(GpuChoice choice, string header)
        {
            var item = new MenuItem { Header = Text(header, 14), IsChecked = current == choice };
            item.Click += (_, _) => Move(app, choice, choice switch
            {
                GpuChoice.PowerSaving => low?.Name,
                GpuChoice.HighPerformance => high?.Name,
                _ => null,
            });
            menu.Items.Add(item);
        }
        Add(GpuChoice.Auto, "LET WINDOWS DECIDE");
        Add(GpuChoice.PowerSaving, "POWER SAVING  ·  " + (low?.ShortName ?? "none").ToUpperInvariant());
        Add(GpuChoice.HighPerformance, "HIGH PERFORMANCE  ·  " + (high?.ShortName ?? "none").ToUpperInvariant());
        if (oneGpu)
        {
            menu.Items.Add(new MenuItem
            {
                IsEnabled = false,
                Header = Text("Only one GPU is on, so both choices are the same card. Turn on the CPU's graphics in the BIOS to split them; the choice is kept until then.", 12, 300),
            });
        }
        menu.IsOpen = true;
    }

    static TextBlock Text(string text, double size, double wrap = 0)
    {
        var t = new TextBlock { Text = text, FontSize = size };
        if (wrap > 0)
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.MaxWidth = wrap;
            t.FontFamily = (FontFamily)Application.Current.Resources["Plain"];
        }
        return t;
    }

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (Last == null || Pair is not var (low, high)) return;
        var items = Sorter.Assign(Last.Apps, settings.Decided);
        var answer = SortDialog.Ask(this, items, low, high, settings.AutoSort);
        if (answer == null) return;
        settings.AutoSort = answer.AutoSort;
        // Every app on the board is placed now: auto-sort leaves them where you put them.
        foreach (var i in items) settings.Decided.Add(i.App.Path!);
        var moved = new List<AppGroup>();
        var failed = new List<string>();
        foreach (var i in answer.Changed)
        {
            try
            {
                GpuPreferences.Set(i.App.Path!, i.To);
                moved.Add(i.App);
            }
            catch (Exception ex) { failed.Add($"{i.App.Title} ({ex.Message})"); }
        }
        settings.Save();
        Raise(nameof(AutoSortText));
        Draw(Last);
        if (failed.Count > 0) Say($"Moved {moved.Count}; couldn't move {string.Join(", ", failed)}.", warn: true);
        else if (moved.Count > 0) Say($"Moved {moved.Count} app{(moved.Count == 1 ? "" : "s")}.");

        // Restart the ones that moved, games aside, so they switch now.
        if (answer.Restart)
        {
            var apps = answer.Changed.Where(i => i.Kind != AppKind.Game && moved.Contains(i.App)).Select(i => i.App).ToList();
            _ = RestartAll(apps);
        }
    }

    // Restarts apps one at a time (an app with several moved helpers once), reporting as it goes.
    async Task RestartAll(List<AppGroup> apps)
    {
        var plans = new List<RestartPlan>();
        var skipped = new List<string>();
        foreach (var app in apps)
        {
            var (plan, why) = Restarter.PlanFor(app);
            if (plan == null) skipped.Add($"{app.Title} ({why})");
            else if (plans.All(p => p.Pid != plan.Pid)) plans.Add(plan);
        }
        var problems = new List<string>(skipped);
        foreach (var plan in plans)
        {
            Say($"Restarting {plan.Title}…");
            var problem = await Task.Run(() => Restarter.Restart(plan));
            if (problem != null) problems.Add(problem);
        }
        if (problems.Count > 0) Say("Restarted what it could. " + string.Join(" ", problems), warn: true);
        else if (plans.Count > 0) Say($"Restarted {string.Join(", ", plans.Select(p => p.Title))} on their new GPU.");
        await Refresh();
    }

    // New apps, as they first use a GPU: sorted like Sort would, unless you've set them yourself.
    void AutoSort(Snapshot s)
    {
        if (!settings.AutoSort || Pair is not var (low, _)) return;
        var moved = new List<string>();
        foreach (var app in s.Apps)
        {
            if (app.Path == null || app.ProtectedWhy != null || settings.Decided.Contains(app.Path)) continue;
            settings.Decided.Add(app.Path);
            if (GpuPreferences.Get(app.Path) != GpuChoice.Auto) continue;  // set elsewhere: leave it
            var to = Sorter.Target(Sorter.Classify(app).Kind);
            try
            {
                GpuPreferences.Set(app.Path, to);
                if (to == GpuChoice.PowerSaving) moved.Add(app.Title);
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        settings.Save();
        if (moved.Count == 0) return;
        string text = $"Moved {string.Join(", ", moved)} to the {low.ShortName} from {(moved.Count == 1 ? "its" : "their")} next start.";
        Say(text);
        if (!IsVisible) AutoSorted?.Invoke(text);
    }

    void Move(AppGroup app, GpuChoice choice, string? gpuName)
    {
        try
        {
            bool changed = GpuPreferences.Get(app.Path!) != choice;
            GpuPreferences.Set(app.Path!, choice);
            settings.Decided.Add(app.Path!);
            settings.Save();
            if (Last != null) Draw(Last);
            string where = choice == GpuChoice.Auto ? "the GPU Windows picks" : gpuName ?? "that GPU";
            Say($"{app.Title} will run on {where} from its next start.");
            if (!changed) return;

            // It reads its GPU only as it starts: offer to restart it now.
            var (plan, _) = Restarter.PlanFor(app);
            if (plan == null) return;
            bool game = Sorter.Classify(app).Kind == AppKind.Game;
            string whom = plan.IsHelper ? $"{plan.Title}, which runs {app.Title}," : plan.Title;
            if (HudDialog.Ask(this, $"RESTART {plan.Title.ToUpperInvariant()} NOW?",
                    $"So it switches straight away, this closes {whom} and opens it again." +
                    (game ? " It's a game: you'll leave the match or lose unsaved progress." : " Anything unsaved in it is lost."),
                    "RESTART", danger: game))
                _ = RestartAll(new List<AppGroup> { app });
        }
        catch (Exception ex) { Say($"Couldn't set {app.Title}'s GPU: {ex.Message}", warn: true); }
    }

    void Say(string text, bool warn = false)
    {
        Status = text.ToUpperInvariant();
        StatusBrush = Format.Res(warn ? "Warn" : "Focus");
        statusTimer.Stop();
        statusTimer.Start();
    }

    static ImageBrush GrainBrush()
    {
        const int size = 160;
        var rng = new Random(7);
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            byte v = (byte)rng.Next(256);
            px[i] = px[i + 1] = px[i + 2] = v;
            px[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        bmp.Freeze();
        return new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute };
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
