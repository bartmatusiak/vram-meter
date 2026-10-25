// The overlay: a small badge on each display saying which GPU and cable it is (so with one monitor on
// two inputs you can tell which you're looking at) and its resolution and refresh rate; then the
// game's frame rate and memory, the GPU's memory, and how busy the GPU and the CPU are. Drag it
// anywhere; let go near an edge, a corner or the middle of one and it snaps there, or hold Shift to
// place it exactly. Each display keeps its own spot. It never takes focus. Alt+O shows and hides it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VramMeter;

public sealed class Overlay : IDisposable
{
    const int Margin = 16, Snap = 64;

    readonly Settings settings;
    readonly Fps fps = new();
    readonly Dictionary<string, Badge> badges = new();
    List<Displays.Active> displays = new();
    Reading? last;

    public Overlay(Settings settings) => this.settings = settings;

    public bool Shown { get; private set; }

    public void Show()
    {
        Shown = true;
        if (last != null) Update(last);
    }

    public void Hide()
    {
        Shown = false;
        foreach (var b in badges.Values) b.Close();
        badges.Clear();
    }

    // Each reading (once a second while the overlay is on): one badge per display showing.
    public void Update(Reading r)
    {
        last = r;
        if (!Shown) return;
        var gpus = r.Snapshot.Gpus;
        var busy = r.Use;
        displays = r.Active;
        foreach (var gone in badges.Keys.Except(displays.Select(d => d.Device)).ToList())
        {
            badges[gone].Close();
            badges.Remove(gone);
        }
        // The app in front's frame rate, on the display its window is on.
        var front = fps.Foreground();
        string? fpsOn = null;
        if (front is var (_, _, _, hwnd) && GetWindowRect(hwnd, out var wr))
        {
            int cx = (wr.Left + wr.Right) / 2, cy = (wr.Top + wr.Bottom) / 2;
            fpsOn = displays.FirstOrDefault(s => cx >= s.X && cx < s.X + s.Width && cy >= s.Y && cy < s.Y + s.Height)?.Device;
        }
        // The app in front, whether or not it's drawing frames.
        GetWindowThreadProcessId(GetForegroundWindow(), out uint frontPid);
        var frontApp = r.Snapshot.Apps.FirstOrDefault(a => a.Procs.Any(p => p.Pid == frontPid));
        const double GB = 1024.0 * 1024 * 1024;
        foreach (var d in displays)
        {
            if (!badges.TryGetValue(d.Device, out var badge))
            {
                badges[d.Device] = badge = new Badge(d.Device);
                badge.Dropped += Dropped;
                badge.Show();
            }
            var gpu = gpus.FirstOrDefault(g => g.Luid == d.Luid);
            // The game on this display's GPU: the app in front if it's using the GPU, else the biggest game.
            var game = frontApp != null && frontApp.Dedicated.GetValueOrDefault(d.Luid) > 64L << 20 && Sorter.Classify(frontApp).Kind != AppKind.Other
                ? frontApp
                : r.Snapshot.Apps.Where(a => a.Dedicated.GetValueOrDefault(d.Luid) > 0 && Sorter.Classify(a).Kind == AppKind.Game)
                    .OrderByDescending(a => a.Dedicated[d.Luid]).FirstOrDefault();
            long used = r.Snapshot.Used.GetValueOrDefault(d.Luid);
            long total = Math.Max(gpu?.Total ?? used, used);
            badge.Set(
                (gpu?.ShortName ?? "GPU").ToUpperInvariant(),
                $"  ·  {d.Cable}  ·  {d.Width}×{d.Height} @ {Math.Round(d.Hz)} HZ",
                gpu?.Vendor ?? GpuVendor.Other,
                fpsOn == d.Device && front is var (_, rate, _, _) ? $"{rate:0}" : "—",
                fpsOn == d.Device && front is var (_, _, gen, _) && gen is double share ? (share > 0 ? $"{share * 100:0}%" : "OFF") : "—",
                $"{busy.GetValueOrDefault(d.Luid):0}%",
                $"{r.Cpu:0}%",
                game != null ? $"{game.Dedicated[d.Luid] / GB:0.0} GB" : "—",
                $"{100.0 * used / Math.Max(1, total):0}% OF {total / GB:0.0} GB");
            if (!badge.Dragging) Place(badge, d);
        }
    }

    // Where its display's badge goes: its saved spot, kept on the display; the top centre at first.
    // A spot is held by the side it's nearest (left, centre, right), so as the line grows or shrinks
    // with its figures, that side stays put: snapped to the right edge, it grows leftward.
    void Place(Badge badge, Displays.Active d)
    {
        var (w, h) = badge.Size();
        if (w == 0) return;
        int x, y;
        if (settings.OverlaySpots.TryGetValue(d.Device, out var spot) && spot.Length == 3)
        {
            x = spot[2] switch
            {
                2 => d.Width - spot[0] - w,       // right: spot[0] is the gap to the right edge
                1 => d.Width / 2 + spot[0] - w / 2,  // centre: spot[0] is the centre's offset
                _ => spot[0],                     // left
            };
            y = spot[1];
        }
        else
        {
            x = (d.Width - w) / 2;
            y = Margin;
        }
        x = Math.Clamp(x, 0, Math.Max(0, d.Width - w));
        y = Math.Clamp(y, 0, Math.Max(0, d.Height - h));
        badge.MoveTo(d.X + x, d.Y + y);
    }

    // Let go: snap to an edge, a corner or an edge's middle when near one (exactly where dropped with
    // Shift held), and keep the spot.
    void Dropped(Badge badge, int left, int top, bool exact)
    {
        var (w, h) = badge.Size();
        // The display it was dropped on (by its centre), which may not be the one it came from.
        int cx = left + w / 2, cy = top + h / 2;
        var d = displays.FirstOrDefault(s => cx >= s.X && cx < s.X + s.Width && cy >= s.Y && cy < s.Y + s.Height)
                ?? displays.FirstOrDefault(s => s.Device == badge.Device);
        if (d == null) return;
        if (d.Device != badge.Device) d = displays.First(s => s.Device == badge.Device);  // each display has its own badge

        int x = exact ? Math.Clamp(left - d.X, 0, Math.Max(0, d.Width - w)) : SnapTo(left - d.X, w, d.Width);
        int y = exact ? Math.Clamp(top - d.Y, 0, Math.Max(0, d.Height - h)) : SnapTo(top - d.Y, h, d.Height);
        // Held by the side it's nearest, by its centre's third of the display.
        int third = (x + w / 2) * 3 / Math.Max(1, d.Width);
        settings.OverlaySpots[d.Device] = third switch
        {
            >= 2 => new[] { d.Width - x - w, y, 2 },
            1 => new[] { x + w / 2 - d.Width / 2, y, 1 },
            _ => new[] { x, y, 0 },
        };
        settings.Save();
        badge.MoveTo(d.X + x, d.Y + y);
    }

    static int SnapTo(int pos, int size, int span)
    {
        int near = Margin, far = span - Margin - size, middle = (span - size) / 2;
        if (Math.Abs(pos - near) < Snap || pos < near) return near;
        if (Math.Abs(pos - far) < Snap || pos > far) return far;
        if (Math.Abs(pos - middle) < Snap) return middle;
        return pos;
    }

    public void Dispose()
    {
        Hide();
        fps.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    sealed class Badge : Window
    {
        const double LabelSize = 14, ValueSize = 16, Gap = 14;
        readonly Run name = new() { FontSize = ValueSize };
        readonly Run detail = new() { FontSize = LabelSize };
        readonly Run fps = new(), generated = new(), gpuUse = new(), cpu = new(), game = new(), vram = new();

        // A row of figures, each a dim label and its value, one even gap apart. Digits are all one
        // width (tabular), so a figure changes width only when it gains or loses a digit.
        static StackPanel Cells(params (string Label, Run Value)[] cells)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < cells.Length; i++)
            {
                var t = new TextBlock { FontSize = ValueSize, Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0) };
                Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
                t.Inlines.Add(new Run(cells[i].Label + " ") { Foreground = Format.Res("Dim"), FontSize = LabelSize });
                cells[i].Value.FontSize = ValueSize;
                t.Inlines.Add(cells[i].Value);
                row.Children.Add(t);
            }
            return row;
        }

        public string Device { get; }
        public bool Dragging { get; private set; }
        public event Action<Badge, int, int, bool>? Dropped;

        public Badge(string device)
        {
            Device = device;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            Left = -10000;
            Cursor = Cursors.SizeAll;
            ToolTip = "Drag to move: it snaps to edges and corners, or hold Shift to place it exactly. Alt+O shows and hides it.";
            detail.Foreground = Format.Res("Dim");
            var first = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            first.Inlines.Add(name);
            first.Inlines.Add(detail);
            // One line: the figures, a divider, then the GPU and display.
            var figures = Cells(("FPS", fps), ("FG", generated), ("GPU", gpuUse), ("CPU", cpu), ("GAME", game), ("VRAM", vram));
            Typography.SetNumeralAlignment(first, FontNumeralAlignment.Tabular);
            var divider = new TextBlock
            {
                Text = "|", Foreground = Format.Res("Dim"), FontSize = ValueSize, Margin = new Thickness(Gap * 0.7, 0, Gap * 0.7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Just the text, with a soft shadow so it reads over anything. The background is all but
            // clear (not quite: fully clear pixels don't take the mouse, and it's dragged by them).
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
                Padding = new Thickness(4, 2, 4, 2),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { figures, divider, first } },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.95 },
            };
            SourceInitialized += (_, _) =>
            {
                // Out of Alt+Tab, and never activated: clicking or dragging it leaves the game focused.
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLongPtr(h, -20, (IntPtr)((long)GetWindowLongPtr(h, -20) | 0x80 | 0x08000000));
            };
            MouseLeftButtonDown += (_, e) =>
            {
                Dragging = true;
                try { DragMove(); }
                catch (InvalidOperationException) { }
                Dragging = false;
                var h = new WindowInteropHelper(this).Handle;
                if (GetWindowRect(h, out var r)) Dropped?.Invoke(this, r.Left, r.Top, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
            };
        }

        public void Set(string gpu, string rest, GpuVendor vendor, string fpsText, string generatedText, string gpuText, string cpuText, string gameText, string vramText)
        {
            name.Text = gpu;
            detail.Text = rest;
            fps.Text = fpsText;
            generated.Text = generatedText;
            gpuUse.Text = gpuText;
            cpu.Text = cpuText;
            game.Text = gameText;
            vram.Text = vramText;
        }

        // In physical pixels, so it lands right whatever each display's scaling.
        public (int W, int H) Size()
        {
            var h = new WindowInteropHelper(this).Handle;
            return h != IntPtr.Zero && GetWindowRect(h, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);
        }

        public void MoveTo(int x, int y)
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010 | 0x0040);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
