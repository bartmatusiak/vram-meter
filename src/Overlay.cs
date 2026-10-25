// The overlay: a small badge in the top-left corner of each display saying which GPU and cable it
// is (so with one monitor on two inputs you can tell which you're looking at), its resolution and
// refresh rate, and how busy its GPU and the CPU are. Click-through, never takes focus.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VramMeter;

public sealed class Overlay : IDisposable
{
    readonly Dictionary<string, Badge> badges = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Load load = new();
    List<Gpu> gpus = new();
    int tick;

    public Overlay()
    {
        timer.Tick += (_, _) => Update();
    }

    public bool Shown => timer.IsEnabled;

    public void Show()
    {
        if (timer.IsEnabled) return;
        timer.Start();
        Update();
    }

    public void Hide()
    {
        timer.Stop();
        foreach (var b in badges.Values) b.Close();
        badges.Clear();
    }

    void Update()
    {
        if (gpus.Count == 0 || tick++ % 30 == 0) gpus = Dxgi.Gpus();
        var busy = load.Gpus();
        var displays = Displays.ActiveDisplays();
        foreach (var gone in badges.Keys.Except(displays.Select(d => d.Device)).ToList())
        {
            badges[gone].Close();
            badges.Remove(gone);
        }
        foreach (var d in displays)
        {
            if (!badges.TryGetValue(d.Device, out var badge))
            {
                badges[d.Device] = badge = new Badge();
                badge.Show();
            }
            var gpu = gpus.FirstOrDefault(g => g.Luid == d.Luid);
            badge.Set(
                $"{(gpu?.ShortName ?? "GPU").ToUpperInvariant()}  ·  {d.Cable}",
                gpu?.Vendor ?? GpuVendor.Other,
                $"{d.Width}×{d.Height}  @ {Math.Round(d.Hz)} HZ",
                $"GPU {busy.GetValueOrDefault(d.Luid):0}%   CPU {load.Cpu:0}%");
            badge.PlaceTopLeft(d.X, d.Y);
        }
    }

    public void Dispose()
    {
        Hide();
        load.Dispose();
    }

    sealed class Badge : Window
    {
        readonly TextBlock title = new() { FontSize = 15 };
        readonly TextBlock mode = new() { FontSize = 12 };
        readonly TextBlock use = new() { FontSize = 12 };
        readonly Border mark = new() { Width = 4, Margin = new Thickness(0, 0, 10, 0) };

        public Badge()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            Left = -10000;
            mode.Foreground = use.Foreground = Format.Res("Dim");
            var text = new StackPanel { Children = { title, mode, use } };
            var row = new DockPanel();
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);
            row.Children.Add(text);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x0B, 0x0C, 0x0B)),
                BorderBrush = Format.Res("Edge"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 14, 8),
                Child = row,
            };
            SourceInitialized += (_, _) =>
            {
                // Click-through, out of Alt+Tab, never activated.
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLongPtr(h, -20, (IntPtr)((long)GetWindowLongPtr(h, -20) | 0x20 | 0x80000 | 0x80 | 0x08000000));
            };
        }

        public void Set(string heading, GpuVendor vendor, string modeText, string useText)
        {
            title.Text = heading;
            mode.Text = modeText;
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
        public void PlaceTopLeft(int x, int y)
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1), x + 24, y + 24, 0, 0, 0x0001 | 0x0010 | 0x0040);
        }

        [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
