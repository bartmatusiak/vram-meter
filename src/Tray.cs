// The taskbar tray icon: a small copy of the meter that fills as the GPU's memory does, its
// figures on hover, the window on a click, and the menu on a right-click.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace VramMeter;

public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly Forms.ToolStripMenuItem startup;
    readonly string promotedFlag = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRAM Meter", "tray-promoted");
    IntPtr hicon;
    int ticks;

    public event Action? Open;
    public event Action? Exit;
    public event Action? SwitchDisplays;
    public event Action? ToggleOverlay;
    public event Action? ToggleDriverCheck;
    public Func<bool>? DriverCheckOn;
    public event Action? ToggleUpdateCheck;
    public Func<bool>? UpdateCheckOn;

    // A newer VRAM Meter's version to offer, or null (set by App), and installing it.
    public Func<string?>? UpdateLabel;
    public event Action? InstallUpdate;

    // Whether the overlay is on, for its check mark (set by App).
    public Func<bool>? OverlayShown;

    // The display switch's label for now, or null to hide it (set by App).
    public Func<string?>? DisplaysLabel;

    public Tray()
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        var open = new Forms.ToolStripMenuItem("Open VRAM Meter", null, (_, _) => Open?.Invoke());
        open.Font = new Font(open.Font, FontStyle.Bold);
        startup = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            try { Settings.StartsWithWindows = !Settings.StartsWithWindows; }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { Notify("Couldn't change Start with Windows: " + e.Message); }
            startup!.Checked = Settings.StartsWithWindows;
        }) { Checked = Settings.StartsWithWindows };
        var displays = new Forms.ToolStripMenuItem("", null, (_, _) => SwitchDisplays?.Invoke());
        var overlay = new Forms.ToolStripMenuItem("Overlay on each display (Alt+O)", null, (_, _) => ToggleOverlay?.Invoke());
        var update = new Forms.ToolStripMenuItem("", null, (_, _) => InstallUpdate?.Invoke());
        update.Font = new Font(update.Font, FontStyle.Bold);
        menu.Items.Add(update);
        menu.Items.Add(open);
        menu.Items.Add(displays);
        menu.Items.Add(overlay);
        menu.Items.Add(startup);
        var drivers = new Forms.ToolStripMenuItem("Check NVIDIA for driver updates", null, (_, _) => ToggleDriverCheck?.Invoke());
        menu.Items.Add(drivers);
        menu.Opening += (_, _) => drivers.Checked = DriverCheckOn?.Invoke() ?? false;
        var updates = new Forms.ToolStripMenuItem("Check for VRAM Meter updates", null, (_, _) => ToggleUpdateCheck?.Invoke());
        menu.Items.Add(updates);
        menu.Opening += (_, _) =>
        {
            updates.Checked = UpdateCheckOn?.Invoke() ?? false;
            var version = UpdateLabel?.Invoke();
            update.Visible = version != null;
            if (version != null) update.Text = $"Update to {version}";
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit?.Invoke());
        menu.Opening += (_, _) =>
        {
            startup.Checked = Settings.StartsWithWindows;
            overlay.Checked = OverlayShown?.Invoke() ?? false;
            var label = DisplaysLabel?.Invoke();
            displays.Visible = label != null;
            if (label != null) displays.Text = "Display: " + System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label.ToLowerInvariant()).Replace("Rtx", "RTX");
        };

        icon = new Forms.NotifyIcon { Text = "VRAM Meter", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Open?.Invoke(); };
        Draw(0, 0, 1);
    }

    public void Hint()
    {
        icon.ShowBalloonTip(6000, "VRAM Meter is still running",
            "It's in the tray. Click it to open the meter; right-click to exit.", Forms.ToolTipIcon.None);
    }

    public void Notify(string text) => icon.ShowBalloonTip(8000, "VRAM Meter", text, Forms.ToolTipIcon.None);

    public void Update(long focus, long other, long total, string? focusTitle)
    {
        Draw(focus, other, total);
        const double GB = 1024.0 * 1024 * 1024;
        string text = $"VRAM {(focus + other) / GB:0.0} / {total / GB:0.0} GB";
        if (focusTitle != null) text += $"\n{focusTitle}: {focus / GB:0.0} GB";
        icon.Text = text.Length > 127 ? text[..127] : text;
        // Windows 11 tucks new tray icons into the overflow; show this one on the taskbar, once.
        if (++ticks == 3) PromoteOnce();
    }

    // The meter in 16-32 px: a dark tile, the frame, and ticks for the focus app (teal) and the rest (white).
    void Draw(long focus, long other, long total)
    {
        int size = Forms.SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size / 32f;
            using (var tile = Rounded(1 * s, 1 * s, 30 * s, 7 * s))
            {
                using var fill = new SolidBrush(Color.FromArgb(4, 16, 14));
                using var edge = new Pen(Color.FromArgb(44, 66, 62), Math.Max(1, s));
                g.FillPath(fill, tile);
                g.DrawPath(edge, tile);
            }
            g.SmoothingMode = SmoothingMode.None;
            int left = (int)Math.Round(7 * s), right = (int)Math.Round(25 * s);
            int top = (int)Math.Round(10 * s), height = (int)Math.Round(10 * s);
            int step = Math.Max(2, (int)Math.Round(2 * s)), tick = Math.Max(1, (int)Math.Round(s));
            double f1 = total > 0 ? (double)focus / total : 0, f2 = total > 0 ? (double)(focus + other) / total : 0;
            using var green = new SolidBrush(Color.FromArgb(4, 212, 174));
            using var white = new SolidBrush(Color.FromArgb(227, 232, 232));
            for (int x = left; x < right; x += step)
            {
                double f = (double)(x - left) / (right - left);
                if (f >= f2) break;
                g.FillRectangle(f < f1 ? green : white, x, top, tick, height);
            }
            using var frame = new Pen(Color.FromArgb(227, 232, 232), Math.Max(1, 1.6f * s));
            g.DrawLines(frame, new[] { new PointF(5 * s, 8 * s), new PointF(5 * s, 23 * s), new PointF(27 * s, 23 * s), new PointF(27 * s, 8 * s) });
        }
        IntPtr next = bmp.GetHicon();
        icon.Icon = Icon.FromHandle(next);
        if (hicon != IntPtr.Zero) DestroyIcon(hicon);
        hicon = next;
    }

    static GraphicsPath Rounded(float x, float y, float w, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        p.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        p.AddArc(x + w - 2 * r, y + w - 2 * r, 2 * r, 2 * r, 0, 90);
        p.AddArc(x, y + w - 2 * r, 2 * r, 2 * r, 90, 90);
        p.CloseFigure();
        return p;
    }

    void PromoteOnce()
    {
        if (File.Exists(promotedFlag)) return;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name, writable: true);
                if (k?.GetValue("ExecutablePath") is string path &&
                    string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                {
                    k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    Directory.CreateDirectory(Path.GetDirectoryName(promotedFlag)!);
                    File.WriteAllText(promotedFlag, DateTime.Now.ToString("s"));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        if (hicon != IntPtr.Zero) DestroyIcon(hicon);
        hicon = IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);
}
