using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Overdrive.Core;

namespace Overdrive.Pages;

/// <summary>Styled tray menu that pops up above the clock when the tray icon is right-clicked.</summary>
public class TrayMenu : Window
{
    static TrayMenu _open;
    readonly MainWindow _main;
    readonly TextBlock _status = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _cpu = Stat(), _ram = Stat();
    readonly Border _card;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly SysInfo.CpuSampler _sampler = new();
    bool _closing;

    public static void Toggle(MainWindow main)
    {
        if (_open != null) { _open.FadeClose(); return; }
        _open = new TrayMenu(main);
        _open.Show();
    }

    TrayMenu(MainWindow main)
    {
        _main = main;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; Left = -10000; Top = -10000; Opacity = 0;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        FontFamily = UI.Res<FontFamily>("Ui");

        var stack = new StackPanel { Width = 312 };
        stack.Children.Add(Header());
        stack.Children.Add(Divider());
        stack.Children.Add(SectionLabel("QUICK TOGGLES"));
        stack.Children.Add(ToggleRow("", "Game Boost", "Auto-boosts any game you open",
            () => Store.Settings.AutoBoost, v => { Store.Settings.AutoBoost = v; Store.SaveSettings(); BoostMode.Recheck(); _main.UpdateBoost(BoostMode.Status); }));
        stack.Children.Add(ToggleRow("", "In-game overlay", "FPS counter  ·  Ctrl+Shift+O",
            () => Store.Settings.FpsOverlay, v => LiveMonitor.SetOverlay(v)));
        stack.Children.Add(ToggleRow("", "Pause Wi-Fi scans in games", "Stops lag spikes from background scans",
            () => Store.Settings.WifiScanPauseWhileGaming, v => { Store.Settings.WifiScanPauseWhileGaming = v; Store.SaveSettings(); BoostMode.SettingChanged(); }));
        stack.Children.Add(ToggleRow("", "Streamer mode", "Hides network names and details",
            () => Store.Settings.StreamerMode, v => { Store.Settings.StreamerMode = v; Store.SaveSettings(); }));
        stack.Children.Add(Divider());
        stack.Children.Add(SectionLabel("QUICK ACTIONS"));

        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(10, 0, 10, 6) };
        grid.Children.Add(Tile("\uEC4A", "Benchmark", () => Open("NavBench")));
        grid.Children.Add(Tile("\uE701", "Speed test", () => Open("NavNetwork")));
        grid.Children.Add(Tile("", "Apps", () => Open("NavApps")));
        grid.Children.Add(Tile("", "Clean junk", CleanJunk));
        grid.Children.Add(Tile("\uE964", "Free RAM", FreeRam));
        if (SysInfo.ShowNvidia) grid.Children.Add(Tile("", "NVIDIA App", () => { FadeClose(); GpuPage.OpenNvidiaApp(); }));
        else grid.Children.Add(Tile("", "Games", () => Open("NavGames")));
        stack.Children.Add(grid);

        stack.Children.Add(Divider());
        var foot = new Grid { Margin = new Thickness(12, 8, 12, 12) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var open = UI.Btn("Open PC Optimizer", (s, e) => { FadeClose(); Tray.ShowMain(_main); }, accent: true);
        open.HorizontalAlignment = HorizontalAlignment.Stretch;
        var settings = IconBtn("", "Settings", () => Open("NavSettings"));
        var quit = IconBtn("", "Quit PC Optimizer (stops Game Boost)", () => { FadeClose(); Tray.Exit(); }, danger: true);
        Grid.SetColumn(settings, 1); Grid.SetColumn(quit, 2);
        settings.Margin = new Thickness(8, 0, 0, 0); quit.Margin = new Thickness(6, 0, 0, 0);
        foot.Children.Add(open); foot.Children.Add(settings); foot.Children.Add(quit);
        stack.Children.Add(foot);

        _card = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(14), Background = UI.B("Bg"),
            BorderBrush = UI.B("Line"), BorderThickness = new Thickness(1), Margin = new Thickness(16),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black },
            RenderTransform = new TranslateTransform(0, 10)
        };
        Content = _card;

        Deactivated += (_, _) => FadeClose();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) FadeClose(); };
        Closed += (_, _) => { _tick.Stop(); if (_open == this) _open = null; };
        Loaded += (_, _) => { Place(); Animate(true); Activate(); };
        _tick.Tick += (_, _) => Refresh();
        Refresh(); _tick.Start();
    }

    // ---------- layout pieces ----------
    UIElement Header()
    {
        var g = new Grid { Margin = new Thickness(14, 14, 14, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logo = new Image { Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/logo.png")); } catch { }
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "PC Optimizer", FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = UI.B("Text") });
        var st = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        st.Children.Add(_dot); st.Children.Add(_status);
        titles.Children.Add(st);
        var stats = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stats.Children.Add(Chip("CPU", _cpu)); stats.Children.Add(Chip("RAM", _ram));
        Grid.SetColumn(titles, 1); Grid.SetColumn(stats, 2);
        g.Children.Add(logo); g.Children.Add(titles); g.Children.Add(stats);
        return g;
    }

    static TextBlock Stat() => new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = UI.B("Text"), HorizontalAlignment = HorizontalAlignment.Center };

    static Border Chip(string label, TextBlock value)
    {
        var sp = new StackPanel();
        sp.Children.Add(value);
        sp.Children.Add(new TextBlock { Text = label, FontSize = 9.5, Foreground = UI.B("Sub"), HorizontalAlignment = HorizontalAlignment.Center });
        return new Border { Child = sp, Background = UI.B("Card"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 0), MinWidth = 46 };
    }

    static Border Divider() => new() { Height = 1, Background = UI.B("Line"), Margin = new Thickness(0, 2, 0, 2) };

    static TextBlock SectionLabel(string t) => new()
    { Text = t, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = UI.B("Sub"), Margin = new Thickness(16, 10, 0, 6) };

    static Border Hoverable(UIElement child, Thickness margin, Thickness padding)
    {
        var b = new Border { Child = child, Background = Brushes.Transparent, CornerRadius = new CornerRadius(9), Margin = margin, Padding = padding, Cursor = Cursors.Hand };
        b.MouseEnter += (_, _) => b.Background = UI.B("CardHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }

    static TextBlock Glyph(string glyph, double size = 16, string brush = "Accent") => new()
    { Text = glyph, FontFamily = UI.IconFont, FontSize = size, Foreground = UI.B(brush), VerticalAlignment = VerticalAlignment.Center };

    UIElement ToggleRow(string icon, string title, string hint, Func<bool> get, Action<bool> set)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        txt.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = UI.B("Text") });
        txt.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = UI.B("Sub"), TextTrimming = TextTrimming.CharacterEllipsis });
        var sw = new CheckBox { Style = UI.Res<Style>("Switch"), IsChecked = get(), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        sw.Click += (_, _) => { set(sw.IsChecked == true); Refresh(); };
        Grid.SetColumn(txt, 1); Grid.SetColumn(sw, 2);
        g.Children.Add(Glyph(icon)); g.Children.Add(txt); g.Children.Add(sw);
        var row = Hoverable(g, new Thickness(6, 0, 6, 0), new Thickness(10, 7, 10, 7));
        row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject d && IsInside(d, sw)) return; sw.IsChecked = !(sw.IsChecked == true); set(sw.IsChecked == true); Refresh(); };
        return row;
    }

    static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d)) if (d == parent) return true;
        return false;
    }

    UIElement Tile(string icon, string label, Action click)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var ic = Glyph(icon, 19); ic.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(ic);
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = UI.B("Text"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        var b = new Border { Child = sp, Background = UI.B("Card"), BorderBrush = UI.B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(4), Padding = new Thickness(4, 11, 4, 10), Cursor = Cursors.Hand };
        b.MouseEnter += (_, _) => { b.Background = UI.B("CardHover"); b.BorderBrush = UI.B("Accent"); };
        b.MouseLeave += (_, _) => { b.Background = UI.B("Card"); b.BorderBrush = UI.B("Line"); };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    Border IconBtn(string glyph, string tip, Action click, bool danger = false)
    {
        var b = new Border { Width = 38, Height = 36, CornerRadius = new CornerRadius(9), Background = UI.B("Card"), BorderBrush = UI.B("Line"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = tip };
        var ic = Glyph(glyph, 14, "Sub"); ic.HorizontalAlignment = HorizontalAlignment.Center;
        b.Child = ic;
        var hover = danger ? UI.Hex("#F43F5E") : UI.B("Accent");
        b.MouseEnter += (_, _) => { b.BorderBrush = hover; ic.Foreground = hover; };
        b.MouseLeave += (_, _) => { b.BorderBrush = UI.B("Line"); ic.Foreground = UI.B("Sub"); };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    // ---------- behaviour ----------
    void Refresh()
    {
        var game = BoostMode.ActiveGame;
        if (game != null) { _status.Text = "Boosting " + game.Name; _status.Foreground = UI.B("Accent"); _dot.Fill = UI.Hex("#22C55E"); }
        else if (Store.Settings.AutoBoost) { _status.Text = "Game Boost ready"; _status.Foreground = UI.B("Sub"); _dot.Fill = UI.B("Accent"); }
        else { _status.Text = "Game Boost off"; _status.Foreground = UI.B("Sub"); _dot.Fill = UI.B("Sub"); }
        try { _cpu.Text = $"{SysInfo.CpuUsage(_sampler):0}%"; } catch { _cpu.Text = "—"; }
        try { _ram.Text = $"{SysInfo.RamUsage().Percent:0}%"; } catch { _ram.Text = "—"; }
    }

    void Open(string nav) { FadeClose(); Tray.ShowMain(_main); _main.Navigate(nav); }

    void FreeRam()
    {
        FadeClose();
        Task.Run(() =>
        {
            try { double mb = LowEnd.FreeStandbyRam(); Tray.Notify("RAM freed", mb > 1 ? $"{mb:0} MB of cached memory released for your games." : "Your RAM was already free."); } catch { }
        });
    }

    void FlushDns()
    {
        FadeClose();
        Task.Run(() => { try { NetLab.FlushDns(); Tray.Notify("DNS cache flushed", "Websites and game servers will be looked up fresh."); } catch { } });
    }

    void CleanJunk()
    {
        FadeClose();
        Tray.Notify("Cleaning junk files...", "Temp files, used update downloads and crash dumps.");
        Task.Run(() =>
        {
            long freed = 0;
            foreach (var t in Cleaner.Targets().Where(t => t.Selected)) { try { freed += Cleaner.Clean(t); } catch { } }
            Tray.Notify("Cleanup done", $"Freed {Cleaner.Format(freed)}.");
        });
    }

    void Place()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var p = System.Windows.Forms.Cursor.Position;
        var wa = System.Windows.Forms.Screen.FromPoint(p).WorkingArea;
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        double w = ActualWidth, h = ActualHeight;
        double left = p.X / sx - w + 16, top = p.Y / sy - h + 16;   // 16 = shadow margin
        left = Math.Max(wa.Left / sx - 8, Math.Min(left, wa.Right / sx - w + 8));
        top = Math.Max(wa.Top / sy - 8, Math.Min(top, wa.Bottom / sy - h + 8));
        Left = left; Top = top;
    }

    void Animate(bool show)
    {
        var dur = TimeSpan.FromMilliseconds(show ? 160 : 110);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, dur) { EasingFunction = ease });
        ((TranslateTransform)_card.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(show ? 0 : 8, dur) { EasingFunction = ease });
    }

    void FadeClose()
    {
        if (_closing) return;
        _closing = true; _tick.Stop();
        if (_open == this) _open = null;
        Animate(false);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }
}
