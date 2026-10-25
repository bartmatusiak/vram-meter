using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VramMeter;

public sealed class Assignment
{
    public required AppGroup App { get; init; }
    public required AppKind Kind { get; init; }
    public required GpuChoice From { get; init; }
    public GpuChoice To { get; set; }
    public bool Changes => To != From;
}

// Which GPU runs what: a column per GPU, each app in the one it will run on. Sorting fills them
// (games on the fast GPU, the rest on the other); clicking an app moves it across. Nothing changes
// until Apply.
public sealed class SortDialog : Window
{
    readonly List<Assignment> items;
    readonly Gpu low, high;
    readonly StackPanel highList = new(), lowList = new();
    readonly TextBlock summary;
    readonly CheckBox auto;

    public List<Assignment> Changed => items.Where(i => i.Changes).ToList();
    public bool AutoSort => auto.IsChecked == true;

    SortDialog(Window owner, List<Assignment> items, Gpu low, Gpu high, bool autoSort)
    {
        this.items = items;
        this.low = low;
        this.high = high;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Width = 900;
        Height = System.Math.Min(780, owner.ActualHeight - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x12));
        BorderBrush = Format.Res("Edge");
        BorderThickness = new Thickness(1);
        UseLayoutRounding = true;
        var plain = (FontFamily)FindResource("Plain");

        var head = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "WHICH GPU RUNS WHAT", FontSize = 22 },
                new TextBlock
                {
                    Text = "Games stay on the fast card; everything else goes to the other GPU, leaving the card's memory to the games. " +
                           "Click an app to move it across. Capture and overlay tools stay with the games.",
                    FontFamily = plain, FontSize = 13, Foreground = Format.Res("Dim"), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 20),
                },
            },
        };

        var lanes = new Grid();
        lanes.ColumnDefinitions.Add(new ColumnDefinition());
        lanes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        lanes.ColumnDefinitions.Add(new ColumnDefinition());
        lanes.Children.Add(Lane(high, "GAMES", highList, 0));
        lanes.Children.Add(Lane(low, "EVERYTHING ELSE", lowList, 2));

        summary = new TextBlock { FontSize = 13, Foreground = Format.Res("Dim"), Margin = new Thickness(0, 16, 0, 0) };
        auto = new CheckBox
        {
            IsChecked = autoSort, Margin = new Thickness(0, 14, 0, 0),
            Content = Label("SORT NEW APPS THE SAME WAY AS THEY START USING THE GPU (APPS YOU'VE PLACED ARE LEFT ALONE)"),
        };

        var cancel = new Button { Content = "CANCEL", Style = (Style)FindResource("HudButton"), IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
        var ok = new Button
        {
            Content = "APPLY", Style = (Style)FindResource("HudButton"), Padding = new Thickness(16, 5, 16, 5),
            Margin = new Thickness(10, 0, 0, 0), Foreground = Format.Res("Focus"), BorderBrush = Format.Res("Focus"),
        };
        ok.Click += (_, _) => DialogResult = true;

        var foot = new StackPanel
        {
            Children =
            {
                summary, auto,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 20, 0, 0), Children = { cancel, ok },
                },
            },
        };

        var body = new DockPanel { Margin = new Thickness(28, 24, 28, 22) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        body.Children.Add(head);
        body.Children.Add(foot);
        body.Children.Add(lanes);
        Content = body;

        Fill();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) => cancel.Focus();
    }

    static TextBlock Label(string text) => new() { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };

    // A GPU's column: its name over a framed list, like the meter's frame.
    static FrameworkElement Lane(Gpu gpu, string role, StackPanel list, int column)
    {
        var lane = new DockPanel();
        Grid.SetColumn(lane, column);
        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        title.Children.Add(new TextBlock { Text = gpu.ShortName.ToUpperInvariant(), FontSize = 20 });
        title.Children.Add(new TextBlock { Text = role, FontSize = 11, Foreground = Format.Res("Dim"), Margin = new Thickness(0, 2, 0, 0) });
        DockPanel.SetDock(title, Dock.Top);
        lane.Children.Add(title);
        lane.Children.Add(new Border
        {
            BorderBrush = Format.Res("Line"), BorderThickness = new Thickness(2, 0, 2, 2), Padding = new Thickness(6, 2, 6, 6),
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false },
        });
        return lane;
    }

    void Fill()
    {
        highList.Children.Clear();
        lowList.Children.Clear();
        foreach (var i in items.OrderBy(i => i.Kind).ThenBy(i => i.App.Title))
            (i.To == GpuChoice.PowerSaving ? lowList : highList).Children.Add(Card(i));
        if (highList.Children.Count == 0) highList.Children.Add(Empty());
        if (lowList.Children.Count == 0) lowList.Children.Add(Empty());

        int n = items.Count(i => i.Changes);
        summary.Text = n == 0 ? "NOTHING TO MOVE: EVERY APP IS ALREADY ON ITS GPU."
            : $"{n} APP{(n == 1 ? "" : "S")} WILL MOVE. RESTART YOUR PC AFTER APPLYING TO SWITCH THEM ALL.";
        summary.Foreground = Format.Res(n == 0 ? "Dim" : "Focus");
    }

    static TextBlock Empty() => new() { Text = "NO APPS", FontSize = 13, Foreground = Format.Res("Dim"), Margin = new Thickness(8, 10, 0, 0) };

    // One app: its name, its .exe and what it is; a green tick and MOVES when it changes GPU.
    FrameworkElement Card(Assignment i)
    {
        string tag = i.Kind switch { AppKind.Game => "GAME", AppKind.Capture => "CAPTURE / OVERLAY", _ => "" };
        var name = new TextBlock { FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines.Add(new System.Windows.Documents.Run(i.App.Title.ToUpperInvariant()));
        if (tag.Length > 0)
            name.Inlines.Add(new System.Windows.Documents.Run("   " + tag) { FontSize = 11, Foreground = Format.Res(i.Kind == AppKind.Game ? "Focus" : "Dim") });
        var sub = new TextBlock
        {
            Text = System.IO.Path.GetFileName(i.App.Path) + (i.Changes ? "   ·   moves here" : ""),
            FontFamily = (FontFamily)FindResource("Plain"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
            Foreground = Format.Res(i.Changes ? "Focus" : "Dim"),
        };
        var mark = new System.Windows.Shapes.Rectangle
        {
            Width = 3, Margin = new Thickness(0, 0, 10, 0),
            Fill = i.Changes ? Format.Res("Focus") : Brushes.Transparent,
        };
        var content = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left);
        content.Children.Add(mark);
        content.Children.Add(new StackPanel { Children = { name, sub } });

        var card = new Button
        {
            Style = (Style)FindResource("TextButton"), Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(4, 8, 4, 2), ToolTip = $"Move to the {(i.To == GpuChoice.PowerSaving ? high.ShortName : low.ShortName)}",
        };
        card.Click += (_, _) =>
        {
            i.To = i.To == GpuChoice.PowerSaving ? GpuChoice.HighPerformance : GpuChoice.PowerSaving;
            Fill();
        };
        return card;
    }

    public static SortDialog? Ask(Window owner, List<Assignment> items, Gpu low, Gpu high, bool autoSort)
    {
        var d = new SortDialog(owner, items, low, high, autoSort);
        return d.ShowDialog() == true ? d : null;
    }
}
