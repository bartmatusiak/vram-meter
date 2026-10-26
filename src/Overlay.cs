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
using System.Windows.Media.Animation;
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
                // Back to its spot the moment its size changes (a game's figures coming or going), not a
                // reading later, so it doesn't draw grown from the wrong side and then jump.
                badge.Resized += b =>
                {
                    var on = displays.FirstOrDefault(s => s.Device == b.Device);
                    if (on != null && !b.Dragging) Place(b, on);
                };
                badge.Show();
            }
            var gpu = gpus.FirstOrDefault(g => g.Luid == d.Luid);
            // The game on this display's GPU: the app in front if it's using the GPU, else the biggest game.
            var game = frontApp != null && frontApp.Dedicated.GetValueOrDefault(d.Luid) > 64L << 20 && Sorter.Classify(frontApp).Kind != AppKind.Other
                ? frontApp
                : r.Snapshot.Apps.Where(a => a.Dedicated.GetValueOrDefault(d.Luid) > 0 && Sorter.Classify(a).Kind == AppKind.Game)
                    .OrderByDescending(a => a.Dedicated[d.Luid]).FirstOrDefault();
            var vitals = r.Vitals.GetValueOrDefault(d.Luid);
            long used = r.Snapshot.Used.GetValueOrDefault(d.Luid);
            long total = Math.Max(gpu?.Total ?? used, used);
            badge.Set(
                (gpu?.ShortName ?? "GPU").ToUpperInvariant(),
                $"{d.Cable}  ·  {Resolution(d.Width, d.Height)} @ {Math.Round(d.Hz)} HZ",
                gpu?.Vendor ?? GpuVendor.Other,
                game != null,
                fpsOn == d.Device && front is var (_, rate, _, _) ? $"{rate:0}" : "—",
                fpsOn == d.Device && front is var (_, _, gen, _) && gen is double share ? (share > 0 ? $"{share * 100:0}%" : "OFF") : "—",
                $"{busy.GetValueOrDefault(d.Luid):0}%",
                // The temperature, for the color of its name.
                vitals?.TempC,
                $"{r.Cpu:0}%",
                game != null ? $"{game.Dedicated[d.Luid] / GB:0.0} GB" : "—",
                $"{100.0 * used / Math.Max(1, total):0}%",
                $"OF {total / GB:0.0} GB");
            if (!badge.Dragging) Place(badge, d);
        }
    }

    // A GPU temperature's color: green up to 55 °C, blending to amber at 65 °C, to red at 75 °C. These stay the
    // familiar temperature colours, not the app's teal.
    static Color HeatColor(double celsius)
    {
        Color green = Color.FromRgb(0x8F, 0xBF, 0x4F), amber = Color.FromRgb(0xE0, 0xA4, 0x3A), red = Color.FromRgb(0xE8, 0x56, 0x4A);
        static Color Mix(Color a, Color b, double f) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
        if (celsius <= 55) return green;
        if (celsius <= 65) return Mix(green, amber, (celsius - 55) / 10);
        if (celsius <= 75) return Mix(amber, red, (celsius - 65) / 10);
        return red;
    }

    // A display's resolution by its common name: 1080P, 1440P, 4K, ...; UW for an ultrawide.
    static string Resolution(int width, int height)
    {
        string name = height switch
        {
            2160 => "4K",
            2880 => "5K",
            4320 => "8K",
            _ => $"{height}P",
        };
        return (double)width / height > 2.2 ? name + " UW" : name;
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
        const double LabelSize = 16, ValueSize = 18, Gap = 16;
        // The labels, cable and dividers: lighter than the window's gray, to read over a game.
        static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0xCD, 0xCD));
        readonly Run name = new() { FontSize = ValueSize };
        readonly Run detail = new() { FontSize = ValueSize };
        readonly TextBlock fps = new(), generated = new(), gpuUse = new(), cpu = new(), game = new(), vram = new();
        // The card's size after VRAM's share: fixed for a display, so it needs no slot of its own.
        readonly TextBlock vramOf = new() { FontSize = ValueSize, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        readonly StackPanel frames, gameCell;
        readonly TextBlock framesDivider;

        // A row of figures, each a dim label and its value, one even gap apart. Digits are all one
        // width (tabular). Each value sits right-aligned in a slot as wide as its widest possible value,
        // so nothing in the line, nor the line itself, ever moves as figures change.
        static StackPanel Cells(params (string Label, TextBlock Value, string[] Widest)[] cells)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < cells.Length; i++)
            {
                var label = new TextBlock
                {
                    Text = cells[i].Label, FontSize = LabelSize, Foreground = LabelBrush,
                    Margin = new Thickness(i == 0 ? 0 : Gap, 0, 5, 0), VerticalAlignment = VerticalAlignment.Bottom,
                };
                var value = cells[i].Value;
                value.FontSize = ValueSize;
                value.TextAlignment = TextAlignment.Right;
                value.VerticalAlignment = VerticalAlignment.Bottom;
                value.Width = Math.Ceiling(cells[i].Widest.Max(w => Measure(w, ValueSize))) + 2;
                Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
                row.Children.Add(label);
                row.Children.Add(value);
            }
            return row;
        }

        static double Measure(string text, double size) =>
            new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface((FontFamily)Application.Current.Resources["Hud"], FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size, Brushes.White, 1.0).WidthIncludingTrailingWhitespace;

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
            detail.Foreground = LabelBrush;
            var display = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            display.Inlines.Add(detail);
            Typography.SetNumeralAlignment(display, FontNumeralAlignment.Tabular);
            var gpuName = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            gpuName.Inlines.Add(name);
            // One line, in groups: FPS FG | GPU CPU | VRAM GAME | the display | the GPU.
            // Measured with 8s: in this face the digits are all one width, and 8 is as wide as any.
            TextBlock Divider() => new()
            {
                Text = "|", Foreground = LabelBrush, FontSize = ValueSize, Margin = new Thickness(Gap * 0.8, 0, Gap * 0.8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            // FPS, FG and GAME only while a game is running (see Set).
            frames = Cells(("FPS", fps, new[] { "888" }), ("FG", generated, new[] { "OFF", "88%" }));
            framesDivider = Divider();
            gameCell = Cells(("GAME", game, new[] { "88.8 GB" }));
            gameCell.Margin = new Thickness(Gap, 0, 0, 0);
            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    frames, framesDivider,
                    Cells(("GPU", gpuUse, new[] { "100%" }), ("CPU", cpu, new[] { "100%" })), Divider(),
                    Cells(("VRAM", vram, new[] { "100%" })), vramOf, gameCell, Divider(),
                    display, Divider(), gpuName,
                },
            };
            // A shadow to read it on anything, drawn as its own layer: the text's shape in black, blurred,
            // under the text itself, which has no effect on it (an effect turns what it's on into a
            // bitmap, and that blurred the letters). The background is all but clear (not quite: fully
            // clear pixels don't take the mouse, and it's dragged by them).
            var shape = new VisualBrush(line) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
            line.SizeChanged += (_, e) => shape.Viewbox = new Rect(e.NewSize);
            var shadow = new System.Windows.Shapes.Rectangle
            {
                Fill = Brushes.Black,
                OpacityMask = shape,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 },
                IsHitTestVisible = false,
            };
            TextOptions.SetTextFormattingMode(line, TextFormattingMode.Display);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
                Padding = new Thickness(4, 2, 4, 2),
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                Child = new Grid { Children = { shadow, line } },
            };
            SourceInitialized += (_, _) =>
            {
                // Out of Alt+Tab, and never activated: clicking or dragging it leaves the game focused.
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLongPtr(h, -20, (IntPtr)((long)GetWindowLongPtr(h, -20) | 0x80 | 0x08000000));
            };
            SizeChanged += (_, _) => Resized?.Invoke(this);
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

        public event Action<Badge>? Resized;
        bool? inGameShown;
        bool inGameWanted, fading;

        // A game's figures coming or going: fade out, change, fade back in (placed again while unseen).
        void ShowGame(bool inGame)
        {
            inGameWanted = inGame;
            void Apply() => frames.Visibility = framesDivider.Visibility = gameCell.Visibility =
                (inGameShown = inGameWanted) == true ? Visibility.Visible : Visibility.Collapsed;
            if (inGameShown == null) { Apply(); return; }
            if (inGameShown == inGame || fading) return;
            fading = true;
            var content = (UIElement)Content;
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fadeOut.Completed += (_, _) =>
            {
                Apply();
                fading = false;
                content.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase() });
            };
            content.BeginAnimation(OpacityProperty, fadeOut);
        }

        public void Set(string gpu, string rest, GpuVendor vendor, bool inGame, string fpsText, string generatedText, string gpuText, double? heat, string cpuText, string gameText, string vramText, string vramOfText)
        {
            ShowGame(inGame);
            name.Text = gpu;
            detail.Text = rest;
            fps.Text = fpsText;
            generated.Text = generatedText;
            gpuUse.Text = gpuText;
            // The GPU's name in its temperature's color, blended: green while fine, through amber when warm,
            // to red near its limit.
            name.Foreground = heat is double h ? new SolidColorBrush(HeatColor(h)) : Format.Res("Fg");
            cpu.Text = cpuText;
            game.Text = gameText;
            vram.Text = vramText;
            vramOf.Text = vramOfText;
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
