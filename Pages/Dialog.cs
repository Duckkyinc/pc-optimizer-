using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Overdrive.Pages;

/// <summary>Dark, rounded, animated replacement for the standard Windows message box.</summary>
public static class Dialog
{
    public static MessageBoxResult Show(string text, string title = "PC Optimizer", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information)
    {
        var app = Application.Current;
        if (app == null) return MessageBoxResult.None;
        if (!app.Dispatcher.CheckAccess()) return app.Dispatcher.Invoke(() => Show(text, title, buttons, image));

        // pick the look
        bool success = image == MessageBoxImage.Information && buttons == MessageBoxButton.OK &&
                       new[] { "Done", "Boosted", "Freed", "Undid", "Restore point created", "PC Optimizer is updated", "DNS cache cleared" }.Any(w => text.StartsWith(w, StringComparison.OrdinalIgnoreCase));
        (string glyph, string color) = image switch
        {
            MessageBoxImage.Warning => ("", "#FFB547"),
            MessageBoxImage.Error => ("", "#FF5470"),
            MessageBoxImage.Question => ("", "#00E5FF"),
            _ => success ? ("", "#2EE59D") : ("", "#00E5FF"),
        };

        var result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;

        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Title = title, Topmost = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, UseLayoutRounding = true,
        };
        TextOptions.SetTextFormattingMode(win, TextFormattingMode.Ideal);
        var owner = app.MainWindow;
        if (owner != null && owner.IsVisible && owner != win) win.Owner = owner;
        else win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ---- content
        var root = new Grid { Margin = new Thickness(24) }; // room for the shadow
        var card = new Border
        {
            Background = UI.Hex("#151A25"), BorderBrush = UI.Hex("#2A3142"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24, 22, 24, 20), MinWidth = 380, MaxWidth = 520,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
        };
        var sp = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(38, ((Color)ColorConverter.ConvertFromString(color)).R, ((Color)ColorConverter.ConvertFromString(color)).G, ((Color)ColorConverter.ConvertFromString(color)).B)),
            Child = new TextBlock { Text = glyph, FontFamily = UI.IconFont, FontSize = 18, Foreground = UI.Hex(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        head.Children.Add(icon);
        var headText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headText.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = UI.B("Text"), FontFamily = UI.Res<FontFamily>("Ui") });
        Grid.SetColumn(headText, 1);
        head.Children.Add(headText);
        sp.Children.Add(head);

        var body = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20, Foreground = UI.B("Sub"),
            FontFamily = UI.Res<FontFamily>("Ui"), Margin = new Thickness(54, 6, 0, 0),
        };
        sp.Children.Add(new ScrollViewer { Content = body, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        // ---- buttons
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        Button Add(string label, MessageBoxResult r, bool primary)
        {
            var b = new Button
            {
                Content = label, Style = UI.Res<Style>(primary ? "AccentButton" : "GhostButton"), MinWidth = 96,
                Margin = new Thickness(10, 0, 0, 0), IsDefault = primary,
            };
            b.Click += (_, _) => { result = r; Close(win); };
            row.Children.Add(b);
            return b;
        }
        switch (buttons)
        {
            case MessageBoxButton.YesNo: Add("No", MessageBoxResult.No, false); Add("Yes", MessageBoxResult.Yes, true); break;
            case MessageBoxButton.YesNoCancel: Add("Cancel", MessageBoxResult.Cancel, false); Add("No", MessageBoxResult.No, false); Add("Yes", MessageBoxResult.Yes, true); break;
            case MessageBoxButton.OKCancel: Add("Cancel", MessageBoxResult.Cancel, false); Add("OK", MessageBoxResult.OK, true); break;
            default: Add("OK", MessageBoxResult.OK, true); break;
        }
        sp.Children.Add(row);
        card.Child = sp;
        root.Children.Add(card);
        win.Content = root;

        // drag by the card, Esc = cancel/no
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { win.DragMove(); } catch { } };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(win); };

        // open animation: fade + slight scale
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.94, 0.94);
        card.RenderTransform = scale;
        win.Opacity = 0;
        win.Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        };

        try { win.ShowDialog(); }
        catch { return MessageBox.Show(text, title, buttons, image); } // fallback if no UI is available yet
        return result;
    }

    static void Close(Window w)
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        a.Completed += (_, _) => { try { w.Close(); } catch { } };
        w.BeginAnimation(UIElement.OpacityProperty, a);
    }
}
