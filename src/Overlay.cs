// The overlay: a small badge on each display saying which GPU and cable it is (so with one monitor on
// two inputs you can tell which you're looking at), its resolution and refresh rate, and how busy its
// GPU and the CPU are. Drag it anywhere; let go near an edge, a corner or the middle of one and it
// snaps there. Each display keeps its own spot. It never takes focus.
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
        foreach (var d in displays)
        {
            if (!badges.TryGetValue(d.Device, out var badge))
            {
                badges[d.Device] = badge = new Badge(d.Device);
                badge.Dropped += Dropped;
                badge.Show();
            }
            var gpu = gpus.FirstOrDefault(g => g.Luid == d.Luid);
            badge.Set(
                (gpu?.ShortName ?? "GPU").ToUpperInvariant(),
                $"  ·  {d.Cable}  ·  {d.Width}×{d.Height} @ {Math.Round(d.Hz)} HZ  ·  ",
                gpu?.Vendor ?? GpuVendor.Other,
                $"GPU {busy.GetValueOrDefault(d.Luid):0}%   CPU {r.Cpu:0}%");
            if (!badge.Dragging) Place(badge, d);
        }
    }

    // Where its display's badge goes: its saved spot, kept on the display; the top centre at first.
    void Place(Badge badge, Displays.Active d)
    {
        var (w, h) = badge.Size();
        if (w == 0) return;
        int x, y;
        if (settings.OverlaySpots.TryGetValue(d.Device, out var spot) && spot.Length == 2)
        {
            x = Math.Clamp(spot[0], 0, Math.Max(0, d.Width - w));
            y = Math.Clamp(spot[1], 0, Math.Max(0, d.Height - h));
        }
        else
        {
            x = (d.Width - w) / 2;
            y = Margin;
        }
        badge.MoveTo(d.X + x, d.Y + y);
    }

    // Let go: snap to an edge, a corner or an edge's middle when near one, and keep the spot.
    void Dropped(Badge badge, int left, int top)
    {
        var (w, h) = badge.Size();
        // The display it was dropped on (by its centre), which may not be the one it came from.
        int cx = left + w / 2, cy = top + h / 2;
        var d = displays.FirstOrDefault(s => cx >= s.X && cx < s.X + s.Width && cy >= s.Y && cy < s.Y + s.Height)
                ?? displays.FirstOrDefault(s => s.Device == badge.Device);
        if (d == null) return;
        if (d.Device != badge.Device) d = displays.First(s => s.Device == badge.Device);  // each display has its own badge

        int x = SnapTo(left - d.X, w, d.Width), y = SnapTo(top - d.Y, h, d.Height);
        settings.OverlaySpots[d.Device] = new[] { x, y };
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

    public void Dispose() => Hide();

    sealed class Badge : Window
    {
        readonly Run name = new() { FontSize = 14 };
        readonly Run detail = new() { FontSize = 12 };
        readonly Run use = new() { FontSize = 12 };
        readonly Border mark = new() { Width = 3, Margin = new Thickness(0, 0, 9, 0) };

        public string Device { get; }
        public bool Dragging { get; private set; }
        public event Action<Badge, int, int>? Dropped;

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
            ToolTip = "Drag to move; it snaps to edges and corners";
            detail.Foreground = Format.Res("Dim");
            var line = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            line.Inlines.Add(name);
            line.Inlines.Add(detail);
            line.Inlines.Add(use);
            var row = new DockPanel();
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);
            row.Children.Add(line);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x0B, 0x0C, 0x0B)),
                BorderBrush = Format.Res("Edge"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 12, 5),
                Child = row,
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
                if (GetWindowRect(h, out var r)) Dropped?.Invoke(this, r.Left, r.Top);
                e.Handled = true;
            };
        }

        public void Set(string gpu, string rest, GpuVendor vendor, string useText)
        {
            name.Text = gpu;
            detail.Text = rest;
            use.Text = useText;
            mark.Background = Format.Res(vendor switch
            {
                GpuVendor.Nvidia => "NvidiaGreen",
                GpuVendor.Amd => "AmdBrand",
                GpuVendor.Intel => "IntelBrand",
                _ => "Line",
            });
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
