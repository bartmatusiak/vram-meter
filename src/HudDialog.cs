using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VramMeter;

// A confirm box in the meter's look. Cancel has the focus and Enter does nothing, so a stray
// keypress can't confirm; only a click on the action (or its Alt key) does.
public sealed class HudDialog : Window
{
    HudDialog(Window owner, string title, string message, string action, bool danger)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x12));
        BorderBrush = Format.Res("Edge");
        BorderThickness = new Thickness(1);
        UseLayoutRounding = true;

        var cancel = new Button { Content = "CANCEL", Style = (Style)FindResource("HudButton"), IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
        var ok = new Button { Content = action, Style = (Style)FindResource("HudButton"), Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(10, 0, 0, 0) };
        if (danger)
        {
            ok.Foreground = Format.Res("Warn");
            ok.BorderBrush = Format.Res("Warn");
        }
        ok.Click += (_, _) => DialogResult = true;

        Content = new StackPanel
        {
            Margin = new Thickness(26, 22, 26, 22),
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 22),
                    Foreground = Format.Res("Dim"), FontFamily = (FontFamily)FindResource("Plain"),
                },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) => cancel.Focus();
    }

    public static bool Ask(Window owner, string title, string message, string action, bool danger = false)
        => new HudDialog(owner, title, message, action, danger).ShowDialog() == true;
}
