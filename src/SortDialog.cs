using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VramMeter;

// The plan for sorting apps between GPUs, each move ticked; nothing changes until Move.
public sealed class SortDialog : Window
{
    readonly List<(CheckBox Box, SortStep Step)> rows = new();
    readonly CheckBox auto;

    public List<SortStep> Chosen => rows.Where(r => r.Box.IsChecked == true).Select(r => r.Step).ToList();
    public bool AutoSort => auto.IsChecked == true;

    SortDialog(Window owner, List<SortStep> steps, Gpu low, Gpu high, bool autoSort)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 760;
        MaxHeight = owner.ActualHeight - 40;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x12));
        BorderBrush = Format.Res("Edge");
        BorderThickness = new Thickness(1);
        UseLayoutRounding = true;
        var plain = (FontFamily)FindResource("Plain");

        string Gpu(GpuChoice c) => c switch
        {
            GpuChoice.PowerSaving => low.ShortName.ToUpperInvariant(),
            GpuChoice.HighPerformance => high.ShortName.ToUpperInvariant(),
            _ => "AUTO",
        };

        var list = new StackPanel();
        AppKind? group = null;
        foreach (var s in steps)
        {
            if (s.Kind != group)
            {
                group = s.Kind;
                list.Children.Add(new TextBlock
                {
                    Text = s.Kind switch
                    {
                        AppKind.Game => "GAMES  →  " + Gpu(GpuChoice.HighPerformance),
                        AppKind.Capture => "CAPTURE AND OVERLAYS  →  " + Gpu(GpuChoice.HighPerformance) + ", WITH THE GAMES",
                        _ => "EVERYTHING ELSE  →  " + Gpu(GpuChoice.PowerSaving),
                    },
                    FontSize = 13, Foreground = Format.Res("Dim"), Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 16, 0, 6),
                });
            }
            var label = new TextBlock { FontSize = 15 };
            label.Inlines.Add(new System.Windows.Documents.Run(s.App.Title.ToUpperInvariant()));
            label.Inlines.Add(new System.Windows.Documents.Run($"   {System.IO.Path.GetFileName(s.App.Path)}   now {Gpu(s.From).ToLowerInvariant()}")
            {
                FontFamily = plain, FontSize = 11.5, Foreground = Format.Res("Dim"),
            });
            var box = new CheckBox { Content = label, IsChecked = true, Margin = new Thickness(0, 5, 0, 5), ToolTip = s.Why };
            rows.Add((box, s));
            list.Children.Add(box);
        }
        if (steps.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "EVERY APP IS ALREADY ON ITS GPU.", FontSize = 15, Foreground = Format.Res("Focus"),
            });
        }

        auto = new CheckBox
        {
            IsChecked = autoSort, Margin = new Thickness(0, 20, 0, 0),
            Content = new TextBlock
            {
                Text = "KEEP SORTING NEW APPS AS THEY START USING THE GPU (APPS YOU SET YOURSELF ARE LEFT ALONE)",
                FontSize = 13, TextWrapping = TextWrapping.Wrap,
            },
        };

        var cancel = new Button { Content = "CANCEL", Style = (Style)FindResource("HudButton"), IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
        var ok = new Button
        {
            Content = steps.Count == 0 ? "SAVE" : "MOVE", Style = (Style)FindResource("HudButton"),
            Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(10, 0, 0, 0),
            Foreground = Format.Res("Focus"), BorderBrush = Format.Res("Focus"),
        };
        ok.Click += (_, _) => DialogResult = true;

        var head = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "SORT APPS BETWEEN GPUS", FontSize = 20 },
                new TextBlock
                {
                    Text = $"Games stay on the {high.Name}; everything else moves to the {low.Name}, leaving the big card's memory to the games. " +
                           "Each app switches the next time it starts, and keeps it: this is Windows' own Graphics setting.",
                    FontFamily = plain, FontSize = 13, Foreground = Format.Res("Dim"), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 18),
                },
            },
        };
        var foot = new StackPanel
        {
            Children =
            {
                auto,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 22, 0, 0), Children = { cancel, ok },
                },
            },
        };
        var body = new DockPanel { Margin = new Thickness(26, 22, 26, 22), LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        body.Children.Add(head);
        body.Children.Add(foot);
        body.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Content = body;

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) => cancel.Focus();
    }

    public static SortDialog? Ask(Window owner, List<SortStep> steps, Gpu low, Gpu high, bool autoSort)
    {
        var d = new SortDialog(owner, steps, low, high, autoSort);
        return d.ShowDialog() == true ? d : null;
    }
}
