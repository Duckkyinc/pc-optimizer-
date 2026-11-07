using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Overdrive.Core;

namespace Overdrive.Pages;

public interface IRefreshable { void Refresh(); }

/// <summary>Small helpers so pages can be built in code with the shared theme.</summary>
public static class UI
{
    /// <summary>Windows 11 icon font first, Windows 10 icon font as fallback.</summary>
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static T Res<T>(string key) => (T)Application.Current.FindResource(key);
    public static Brush B(string key) => Res<Brush>(key);
    public static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

    public static ScrollViewer PageScroll(UIElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = new Border { Padding = new Thickness(40, 34, 40, 30), Child = content, MaxWidth = 1200, HorizontalAlignment = HorizontalAlignment.Stretch },
        Focusable = false,
    };

    public static StackPanel Header(string title, string subtitle)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 60, 22) };
        sp.Children.Add(new TextBlock { Text = title, Style = Res<Style>("H1") });
        if (subtitle != null)
            sp.Children.Add(new TextBlock { Text = subtitle, Style = Res<Style>("SubText"), FontSize = 13.5, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left });
        return sp;
    }

    /// <summary>Small (i) icon that shows the full explanation on hover, so pages stay clean.</summary>
    public static Border InfoDot(string text)
    {
        var tip = new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 } };
        return new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Hex("#1A2030"), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Help, ToolTip = tip,
            Child = new TextBlock { Text = "\uE946", FontFamily = IconFont, FontSize = 10, Foreground = B("Sub"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    /// <summary>Section title with an optional (i) hover explanation.</summary>
    public static StackPanel SectionInfo(string text, string info)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 12) };
        var t = new TextBlock { Text = text, Style = Res<Style>("H2"), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(t);
        if (!string.IsNullOrEmpty(info)) sp.Children.Add(InfoDot(info));
        return sp;
    }

    /// <summary>Collapsed card with a clickable title. Keeps long tip lists out of the way until wanted.</summary>
    public static Border Collapsible(string title, UIElement content, string icon = "\uE82F", bool open = false)
    {
        var chevron = new TextBlock { Text = open ? "\uE70E" : "\uE70D", FontFamily = IconFont, FontSize = 11, Foreground = B("Sub"), VerticalAlignment = VerticalAlignment.Center };
        var head = new Grid { Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = icon, FontFamily = IconFont, FontSize = 14, Foreground = B("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var t = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = B("Text"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(t, 1); Grid.SetColumn(chevron, 2);
        head.Children.Add(t); head.Children.Add(chevron);
        var body = new Border { Child = content, Margin = new Thickness(0, 14, 0, 0), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        var sp = new StackPanel(); sp.Children.Add(head); sp.Children.Add(body);
        var card = Card(sp);
        card.Padding = new Thickness(18, 14, 18, 14);
        card.Margin = new Thickness(0, 0, 0, 12);
        head.MouseLeftButtonUp += (_, _) =>
        {
            bool show = body.Visibility != Visibility.Visible;
            body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = show ? "\uE70E" : "\uE70D";
        };
        return card;
    }

    public static Border Card(UIElement child, Thickness? margin = null, Brush background = null)
    {
        var b = new Border { Style = Res<Style>("CardBorder"), Child = child };
        if (margin.HasValue) b.Margin = margin.Value;
        if (background != null) b.Background = background;
        return b;
    }

    public static TextBlock Text(string text, double size = 13, string brush = "Text", FontWeight? weight = null, bool wrap = true)
        => new() { Text = text, FontSize = size, Foreground = B(brush), FontWeight = weight ?? FontWeights.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };

    public static TextBlock Label(string text) => new() { Text = text, Style = Res<Style>("Label"), Margin = new Thickness(0, 0, 0, 6) };

    public static TextBlock Section(string text) => new() { Text = text, Style = Res<Style>("H2"), Margin = new Thickness(0, 18, 0, 12) };

    public static Button Btn(string text, RoutedEventHandler click, bool accent = false, string icon = null, string style = null)
    {
        object content = text;
        if (icon != null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = new TextBlock { Text = icon, FontFamily = UI.IconFont, FontSize = 13, Margin = new Thickness(0, 1, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            var tx = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = accent ? FontWeights.Bold : FontWeights.SemiBold };
            var fg = accent ? Hex("#06121A") : B("Text");
            ic.Foreground = fg; tx.Foreground = fg;
            sp.Children.Add(ic); sp.Children.Add(tx);
            content = sp;
        }
        var b = new Button { Content = content, Style = Res<Style>(style ?? (accent ? "AccentButton" : "GhostButton")), Margin = new Thickness(0, 0, 10, 0) };
        if (click != null) b.Click += click;
        return b;
    }

    public static Border Badge(string text, string fg, string bg) => new()
    {
        CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 6, 0),
        Background = Hex(bg), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Hex(fg) }
    };

    public static event Action ViewChanged;
    public static void SetView(string v) { Store.Settings.TweakView = v; Store.SaveSettings(); ViewChanged?.Invoke(); }

    /// <summary>Tweaks as compact tiles (grid) or full cards (list), following the user's choice.</summary>
    public static ItemsControl TweakList(IEnumerable<TweakVM> items)
    {
        var ic = new ItemsControl { ItemsSource = items.ToList(), Focusable = false };
        void Apply()
        {
            bool grid = Store.Settings.TweakView != "list";
            ic.ItemTemplate = Res<DataTemplate>(grid ? "TweakTile" : "TweakCard");
            if (grid)
            {
                var f = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.UniformGrid));
                f.SetValue(System.Windows.Controls.Primitives.UniformGrid.ColumnsProperty, Columns(ic.ActualWidth));
                ic.ItemsPanel = new ItemsPanelTemplate(f);
                ic.Margin = new Thickness(0, 0, -10, 0); // tiles have a right margin
            }
            else
            {
                ic.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));
                ic.Margin = new Thickness(0);
            }
        }
        int lastCols = -1;
        ic.SizeChanged += (_, e) =>
        {
            if (Store.Settings.TweakView == "list") return;
            var c = Columns(e.NewSize.Width);
            if (c != lastCols) { lastCols = c; Apply(); }
        };
        ViewChanged += () => ic.Dispatcher.BeginInvoke(() => { lastCols = -1; Apply(); });
        Apply();
        return ic;
    }

    static int Columns(double width) => width <= 0 ? 3 : Math.Clamp((int)(width / 280), 2, 4);

    /// <summary>Small Grid | List switch.</summary>
    public static StackPanel ViewSwitch()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        RadioButton Opt(string id, string icon, string tip)
        {
            var rb = new RadioButton
            {
                Style = Res<Style>("Chip"), GroupName = "view-" + Guid.NewGuid(), IsChecked = (Store.Settings.TweakView ?? "grid") == id, ToolTip = tip,
                Content = new TextBlock { Text = icon, FontFamily = IconFont, FontSize = 13 }, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(10, 7, 10, 7),
            };
            rb.Checked += (_, _) => { if (Store.Settings.TweakView != id) SetView(id); };
            ViewChanged += () => rb.Dispatcher.BeginInvoke(() => rb.IsChecked = (Store.Settings.TweakView ?? "grid") == id);
            return rb;
        }
        sp.Children.Add(Opt("grid", "\uF0E2", "Grid view"));
        sp.Children.Add(Opt("list", "\uE8FD", "List view (full descriptions)"));
        return sp;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static WrapPanel Wrap(params UIElement[] children)
    {
        var wp = new WrapPanel();
        foreach (var c in children) wp.Children.Add(c);
        return wp;
    }

    /// <summary>A bullet tip line.</summary>
    public static Grid Tip(string text, string bullet = "", string color = "Accent")
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var b = new TextBlock { Text = bullet, FontFamily = UI.IconFont, FontSize = 11, Foreground = B(color), Margin = new Thickness(0, 3, 0, 0) };
        var t = Text(text, 13, "Sub");
        t.LineHeight = 19;
        Grid.SetColumn(t, 1);
        g.Children.Add(b); g.Children.Add(t);
        return g;
    }

    public static void Info(string message, string title = "PC Optimizer") =>
        Overdrive.Pages.Dialog.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message, string title = "PC Optimizer") =>
        Overdrive.Pages.Dialog.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Run work in the background with a button disabled while it runs.</summary>
    public static async Task Busy(Button b, Func<Task> work)
    {
        b.IsEnabled = false;
        try { await work(); }
        catch (Exception ex) { Store.Log("Action failed: " + ex); Overdrive.Pages.Dialog.Show(ex.Message, "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { b.IsEnabled = true; }
    }
}
