using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Overdrive.Core;

namespace Overdrive.Pages;

/// <summary>eDPI calculator: DPI x in-game sensitivity, cm/360 and a comparison with typical pro players.</summary>
public class AimPage : UserControl, IRefreshable
{
    // Yaw = degrees turned per mouse count at sensitivity 1. Pro ranges are approximate, from public pro-settings lists.
    record AimGame(string Id, string Name, double? Yaw, double ProLow, double ProHigh, double ProTypical, double DefaultSens, string Note);

    // Yaw values match mouse-sensitivity.com / common converters. For Fortnite, eDPI is DPI x (percent / 100), like pro-settings sites use.
    static readonly AimGame[] GamesList =
    {
        new("valorant", "VALORANT", 0.07, 200, 400, 275, 0.34, "Most VALORANT pros use 800 DPI with 0.25-0.45 sensitivity (avg 0.34)."),
        new("cs2", "Counter-Strike 2", 0.022, 600, 1200, 800, 1.0, "CS2 pros: median 800 eDPI, e.g. 400 DPI x 2 or 800 DPI x 1."),
        new("apex", "Apex Legends", 0.022, 800, 1600, 1200, 1.5, "Apex pros mostly use 800 DPI with 1.0-2.0 sensitivity (avg about 1.5)."),
        new("ow2", "Overwatch", 0.0066, 3000, 6000, 4000, 5.0, "Overwatch pros average about 35 cm/360 (800 DPI x ~5). Tanks often go higher."),
        new("cod", "CoD / Warzone", 0.0066, 3000, 6000, 4500, 5.6, "Use the 'Mouse sensitivity' value with 'Legacy' sensitivity mode (the default). Few CoD pros play mouse, so this range is approximate."),
        new("fortnite", "Fortnite", 0.5555, 45, 72, 52, 6.4, "Enter your X-axis sensitivity as the % number (e.g. 6.4). Most Fortnite pros use 800 DPI with 6-9% (Bugha and Peterbot: 6.4%)."),
        new("r6", "Rainbow Six Siege", 0.00572958, 3200, 4800, 4000, 10, "Use your horizontal sensitivity with the default 0.02 multiplier. Pros mostly use 400 DPI x ~12 or 800 DPI x ~5."),
        new("rivals", "Marvel Rivals", 0.0175, 1150, 2100, 1500, 1.9, "Rivals is new, so this range comes from top players and guides (about 25-45 cm/360), not years of pro data."),
        new("other", "Other game", null, 0, 0, 0, 1, "For other games, eDPI is only useful to compare with players of the same game."),
    };

    /// <summary>Fortnite shows sensitivity as a percent (6.4 = 0.064). Accept either form.</summary>
    double Scale(double sens) => _game.Id == "fortnite" && sens >= 1 ? 0.01 : 1;
    double DisplayScale => _game.Id == "fortnite" ? 0.01 : 1;

    readonly TextBox _dpi = new() { Style = UI.Res<Style>("Input"), Width = 160 };
    readonly TextBox _sens = new() { Style = UI.Res<Style>("Input"), Width = 160 };
    readonly TextBlock _edpi = new() { FontSize = 44, FontWeight = FontWeights.Black, Foreground = UI.B("AccentGrad"), Text = "--" };
    readonly TextBlock _cm = UI.Text("--", 26, "Text", FontWeights.Bold, false);
    readonly TextBlock _in = UI.Text("--", 26, "Text", FontWeights.Bold, false);
    readonly TextBlock _verdict = UI.Text("", 14, "Text", FontWeights.SemiBold);
    readonly TextBlock _note = UI.Text("", 12, "Sub");
    readonly TextBlock _sensLabel = UI.Label("IN-GAME SENSITIVITY");
    readonly Canvas _bar = new() { Height = 46, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
    readonly TextBlock _suggest = UI.Text("", 14, "Text", FontWeights.SemiBold);
    readonly TextBlock _suggestSub = UI.Text("", 12, "Sub");
    Button _useBtn;
    double _target;      // eDPI you dragged to (0 = none)
    double _suggestSens;
    readonly StackPanel _compare = new();
    readonly TextBox _newDpi = new() { Style = UI.Res<Style>("Input"), Width = 110 };
    readonly UniformGrid _convGrid = new() { Columns = 4 };
    readonly TextBlock _convFrom = UI.Text("", 15, "Text", FontWeights.SemiBold, false);
    AimGame _game;

    public AimPage()
    {
        _game = GamesList.FirstOrDefault(g => g.Id == Store.Settings.AimGame) ?? GamesList[0];
        var body = new StackPanel();
        body.Children.Add(UI.Header("Aim", "Find your real sensitivity and match it across games."));

        // ---------- tabs ----------
        var calcView = new StackPanel();
        var convView = new StackPanel { Visibility = Visibility.Collapsed };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        foreach (var (label, view) in new[] { ("Sensitivity", calcView), ("Convert to other games", convView) })
        {
            var rb = new RadioButton { Style = UI.Res<Style>("Chip"), Content = label, GroupName = "aimtab", IsChecked = view == calcView };
            rb.Checked += (_, _) => { calcView.Visibility = view == calcView ? Visibility.Visible : Visibility.Collapsed; convView.Visibility = view == convView ? Visibility.Visible : Visibility.Collapsed; };
            tabs.Children.Add(rb);
        }
        body.Children.Add(tabs);
        body.Children.Add(calcView);
        body.Children.Add(convView);

        // ---------- main card: inputs + results ----------
        var inputs = new WrapPanel();
        inputs.Children.Add(Field("GAME", GamePicker()));
        inputs.Children.Add(Field("MOUSE DPI", _dpi, "Found in your mouse's software (often 400, 800 or 1600)."));
        var sv = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        sv.Children.Add(_sensLabel); sv.Children.Add(_sens);
        _sens.ToolTip = "The sensitivity number in the game's settings.";
        inputs.Children.Add(sv);

        var res = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        res.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        res.ColumnDefinitions.Add(new ColumnDefinition());
        res.ColumnDefinitions.Add(new ColumnDefinition());
        res.Children.Add(Metric("eDPI", _edpi, 0));
        res.Children.Add(Metric("CM / 360°", _cm, 1));
        res.Children.Add(Metric("INCHES / 360°", _in, 2));
        var main = new StackPanel();
        main.Children.Add(inputs);
        main.Children.Add(new Border { Height = 1, Background = UI.B("Line"), Margin = new Thickness(0, 18, 0, 0) });
        main.Children.Add(res);
        calcView.Children.Add(UI.Card(main, new Thickness(0, 0, 0, 16)));

        // ---------- pro comparison + suggestion ----------
        var ch = new StackPanel { Orientation = Orientation.Horizontal };
        ch.Children.Add(UI.Text("Compared to pros", 15, "Text", FontWeights.SemiBold, false));
        ch.Children.Add(UI.InfoDot("Drag on the bar to pick a target eDPI and get the matching sensitivity."));
        _compare.Children.Add(ch);
        _bar.Margin = new Thickness(0, 10, 0, 6);
        _bar.SizeChanged += (_, _) => DrawBar();
        _bar.MouseLeftButtonDown += (_, e) => { _bar.CaptureMouse(); DragTo(e.GetPosition(_bar).X); };
        _bar.MouseMove += (_, e) => { if (_bar.IsMouseCaptured) DragTo(e.GetPosition(_bar).X); };
        _bar.MouseLeftButtonUp += (_, _) => _bar.ReleaseMouseCapture();
        _compare.Children.Add(_bar);
        _compare.Children.Add(_verdict);

        var sg = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        sg.ColumnDefinitions.Add(new ColumnDefinition());
        sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; st.Children.Add(_suggest); st.Children.Add(_suggestSub);
        sg.Children.Add(st);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var avgBtn = UI.Btn("Pro average", (s, e) => { if (_game.ProHigh > 0) { _target = _game.ProTypical; UpdateSuggestion(); DrawBar(); } });
        _useBtn = UI.Btn("Use this", (s, e) =>
        {
            if (_suggestSens > 0) { _sens.Text = FormatSens(_suggestSens); _target = 0; UpdateSuggestion(); }
        }, accent: true, icon: "\uE73E");
        _useBtn.Margin = new Thickness(0);
        btns.Children.Add(avgBtn); btns.Children.Add(_useBtn);
        Grid.SetColumn(btns, 1); sg.Children.Add(btns);
        _compare.Children.Add(sg);
        calcView.Children.Add(UI.Card(_compare, new Thickness(0, 0, 0, 16)));

        // ---------- tips (folded) ----------
        var tips = new StackPanel();
        tips.Children.Add(UI.Tip("Most tactical-shooter pros play slow (25-50 cm/360) and aim with their arm."));
        tips.Children.Add(UI.Tip("Changing DPI? Keep the same eDPI: double the DPI, halve the sensitivity."));
        tips.Children.Add(UI.Tip("Turn off mouse acceleration in the Windows tab."));
        tips.Children.Add(UI.Tip("Pick a sensitivity and stick with it for a few weeks."));
        calcView.Children.Add(UI.Collapsible("Tips", tips, "\uE82F"));

        // ---------- converter ----------
        var conv = new StackPanel();
        var ct = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        ct.Children.Add(_convFrom);
        ct.Children.Add(UI.InfoDot("Every game below turns the same distance per mouse movement (same cm/360). Click a value to copy it."));
        conv.Children.Add(ct);
        conv.Children.Add(_convGrid);
        var dpiRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var nd = UI.Label("DIFFERENT DPI? (OPTIONAL)"); nd.VerticalAlignment = VerticalAlignment.Center; nd.Margin = new Thickness(0, 0, 12, 0);
        dpiRow.Children.Add(nd); dpiRow.Children.Add(_newDpi);
        _newDpi.TextChanged += (_, _) => Convert();
        conv.Children.Add(dpiRow);
        convView.Children.Add(UI.Card(conv));

        Content = UI.PageScroll(body);

        _dpi.Text = Store.Settings.AimDpi.ToString("0.###", CultureInfo.CurrentCulture);
        _sens.Text = Store.Settings.AimSens.ToString("0.####", CultureInfo.CurrentCulture);
        _dpi.TextChanged += (_, _) => Calc(save: true);
        _sens.TextChanged += (_, _) => Calc(save: true);
        Calc();
    }

    static StackPanel Field(string label, FrameworkElement input, string tip = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        sp.Children.Add(UI.Label(label));
        if (tip != null) input.ToolTip = tip;
        sp.Children.Add(input);
        return sp;
    }

    Button _pickBtn;
    Button GamePicker()
    {
        var text = new TextBlock { Text = _game.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var chev = new TextBlock { Text = "\uE70D", FontFamily = UI.IconFont, FontSize = 10, Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = UI.B("Sub") };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(chev, 1); g.Children.Add(text); g.Children.Add(chev);
        _pickBtn = new Button { Style = UI.Res<Style>("GhostButton"), Content = g, Width = 210, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _pickBtn.Click += (_, _) =>
        {
            var list = new StackPanel { Margin = new Thickness(6) };
            Popup pop = null;
            foreach (var game in GamesList)
            {
                var row = new Button { Style = UI.Res<Style>("MenuRow"), Content = new TextBlock { Text = game.Name, FontWeight = game == _game ? FontWeights.SemiBold : FontWeights.Normal, Foreground = game == _game ? UI.B("Accent") : UI.B("Text") } };
                row.Click += (_, _) =>
                {
                    pop.IsOpen = false;
                    _game = game; _target = 0; text.Text = game.Name;
                    Store.Settings.AimGame = game.Id; Store.SaveSettings();
                    Calc();
                };
                list.Children.Add(row);
            }
            pop = new Popup
            {
                PlacementTarget = _pickBtn, Placement = PlacementMode.Bottom, VerticalOffset = 6, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
                Child = new Border { Background = UI.Hex("#161B26"), BorderBrush = UI.B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), MinWidth = 210, Child = list }
            };
            pop.IsOpen = true;
        };
        return _pickBtn;
    }

    static Border Metric(string label, TextBlock value, int col, string sub = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Label(label));
        sp.Children.Add(value);
        if (!string.IsNullOrEmpty(sub)) sp.Children.Add(UI.Text(sub, 11, "Dim"));
        var b = new Border { Child = sp, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(b, col);
        return b;
    }

    static bool TryNum(string s, out double v) =>
        double.TryParse(s?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 && v < 1_000_000;

    double _eDpi;

    void Calc(bool save = false)
    {
        _sensLabel.Text = "SENSITIVITY";
        if (!TryNum(_dpi.Text, out var dpi) || !TryNum(_sens.Text, out var sens))
        {
            _edpi.Text = "--"; _cm.Text = "--"; _in.Text = "--"; _eDpi = 0;
            _verdict.Text = "Enter your mouse DPI and in-game sensitivity.";
            _verdict.Foreground = UI.B("Sub");
            UpdateSuggestion();
            DrawBar();
            return;
        }
        if (save) { Store.Settings.AimDpi = dpi; Store.Settings.AimSens = sens; Store.SaveSettings(); }

        _eDpi = dpi * sens * Scale(sens);
        _edpi.Text = _eDpi >= 100 ? _eDpi.ToString("0") : _eDpi.ToString("0.#");

        if (_game.Yaw is double yaw)
        {
            double inches = 360.0 / (_eDpi * yaw);
            _cm.Text = $"{inches * 2.54:0.#} cm";
            _in.Text = $"{inches:0.#} in";
        }
        else { _cm.Text = "n/a"; _in.Text = "n/a"; }

        if (_game.ProHigh > 0)
        {
            string how = _eDpi < _game.ProLow ? "Slower than most pros"
                       : _eDpi > _game.ProHigh ? "Faster than most pros"
                       : Math.Abs(_eDpi - _game.ProTypical) / _game.ProTypical < 0.15 ? "Right where most pros are"
                       : "Inside the usual pro range";
            string why = _eDpi < _game.ProLow ? "Very precise, but big turns need a lot of mouse space."
                       : _eDpi > _game.ProHigh ? "Fast turns, but small precise adjustments are harder. Try lowering it a bit if your aim feels shaky."
                       : "A good, balanced sensitivity for this game.";
            _verdict.Text = $"{how}. {why}";
            _verdict.Foreground = UI.B(_eDpi < _game.ProLow || _eDpi > _game.ProHigh ? "Warn" : "Good");
        }
        else
        {
            _verdict.Text = "Compare this eDPI with other players of the same game. Each game uses its own sensitivity scale.";
            _verdict.Foreground = UI.B("Sub");
        }
        _note.Text = _game.Note + (_game.ProHigh > 0 ? $"  Typical pro eDPI: {_game.ProLow:0}-{_game.ProHigh:0} (approximate)." : "");
        _sens.ToolTip = _game.Note;
        UpdateSuggestion();
        DrawBar();
        Convert();
    }

    double BarMax => _game.ProHigh * 1.6;

    void DragTo(double x)
    {
        double w = _bar.ActualWidth;
        if (w < 50 || _game.ProHigh <= 0) return;
        double v = Math.Clamp(x / w, 0, 1) * BarMax;
        double snap = _game.ProHigh >= 2000 ? 50 : _game.ProHigh >= 500 ? 10 : _game.ProHigh >= 200 ? 5 : 1; // nice round eDPI numbers
        _target = Math.Max(snap, Math.Round(v / snap) * snap);
        UpdateSuggestion();
        DrawBar();
    }

    static string FormatSens(double v) => v >= 10 ? v.ToString("0.#", CultureInfo.CurrentCulture) : v >= 1 ? v.ToString("0.##", CultureInfo.CurrentCulture) : v.ToString("0.###", CultureInfo.CurrentCulture);

    void UpdateSuggestion()
    {
        bool haveDpi = TryNum(_dpi.Text, out var dpi);
        if (_game.ProHigh <= 0)
        {
            _suggest.Text = "Pick a game above to compare with pros and get a suggested sensitivity.";
            _suggestSub.Text = ""; _useBtn.IsEnabled = false; _suggestSens = 0;
            return;
        }
        if (_target <= 0 || !haveDpi)
        {
            _suggest.Text = haveDpi ? "Drag on the bar (or click 'Pro average') to see the sensitivity you'd need." : "Enter your mouse DPI first.";
            _suggestSub.Text = ""; _useBtn.IsEnabled = false; _suggestSens = 0;
            return;
        }
        _suggestSens = _target / dpi / DisplayScale;
        string cm = _game.Yaw is double yaw ? $"  ·  {360.0 / (_target * yaw) * 2.54:0.#} cm/360" : "";
        _suggest.Text = $"Set your {_game.Name} sensitivity to {FormatSens(_suggestSens)}{(_game.Id == "fortnite" ? "%" : "")}";
        string where = _target < _game.ProLow ? "slower than most pros" : _target > _game.ProHigh ? "faster than most pros" : "inside the pro range";
        _suggestSub.Text = $"That's {_target:0} eDPI at {dpi:0} DPI{cm}, {where}. Change it in small steps.";
        _useBtn.IsEnabled = true;
    }

    void DrawBar()
    {
        _bar.Children.Clear();
        double w = _bar.ActualWidth;
        if (w < 50 || _game.ProHigh <= 0) { _bar.Height = _game.ProHigh <= 0 ? 0 : 46; return; }
        _bar.Height = 46;
        double max = BarMax;
        double X(double v) => Math.Clamp(v / max, 0, 1) * w;

        var track = new Border { Width = w, Height = 10, CornerRadius = new CornerRadius(5), Background = UI.Hex("#1E2433") };
        Canvas.SetTop(track, 14); _bar.Children.Add(track);
        var range = new Border { Width = Math.Max(4, X(_game.ProHigh) - X(_game.ProLow)), Height = 10, CornerRadius = new CornerRadius(5), Background = UI.Hex("#1F4A3A") };
        Canvas.SetLeft(range, X(_game.ProLow)); Canvas.SetTop(range, 14); _bar.Children.Add(range);

        void Tick(double v, string text, string color, double top)
        {
            var t = UI.Text(text, 10, color, FontWeights.SemiBold, false);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, Math.Clamp(X(v) - t.DesiredSize.Width / 2, 0, w - t.DesiredSize.Width)); Canvas.SetTop(t, top);
            _bar.Children.Add(t);
        }
        Tick(_game.ProLow, $"{_game.ProLow:0}", "Dim", 28);
        Tick(_game.ProHigh, $"{_game.ProHigh:0}", "Dim", 28);
        Tick(_game.ProTypical, "pro average", "Good", 28);
        var pa = new Border { Width = 2, Height = 14, Background = UI.B("Good") };
        Canvas.SetLeft(pa, X(_game.ProTypical) - 1); Canvas.SetTop(pa, 12); _bar.Children.Add(pa);

        if (_target > 0)
        {
            var ring = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = UI.Hex("#0A0C11"), BorderBrush = UI.B("Good"), BorderThickness = new Thickness(3) };
            Canvas.SetLeft(ring, X(_target) - 10); Canvas.SetTop(ring, 9); _bar.Children.Add(ring);
            Tick(_target, $"target {_target:0}", "Good", -4);
        }
        if (_eDpi > 0)
        {
            var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = UI.B("AccentGrad"), BorderBrush = Brushes.White, BorderThickness = new Thickness(2) };
            Canvas.SetLeft(dot, X(_eDpi) - 9); Canvas.SetTop(dot, 10); _bar.Children.Add(dot);
            Tick(_eDpi, "you", "Accent", -4);
        }
    }

    /// <summary>Degrees turned per mouse count for one unit of the number shown in the game's menu.</summary>
    static double YawPerUnit(AimGame g, double sens) => g.Id == "fortnite" ? (sens >= 1 ? 0.005555 : 0.5555) : g.Yaw ?? 0;

    void Convert()
    {
        _convGrid.Children.Clear();
        _convFrom.Text = TryNum(_sens.Text, out var fs) && TryNum(_dpi.Text, out var fd) ? $"From {_game.Name}: {_sens.Text.Trim()}{(_game.Id == "fortnite" ? "%" : "")} at {fd:0} DPI" : "Your sensitivity in other games";
        if (!TryNum(_dpi.Text, out var dpi) || !TryNum(_sens.Text, out var sens) || _game.Yaw == null)
        {
            _convGrid.Columns = 1;
            _convGrid.Children.Add(UI.Text(_game.Yaw == null ? "Pick a game above (not 'Other game') to convert your sensitivity." : "Enter your DPI and sensitivity above.", 12.5, "Sub"));
            return;
        }
        _convGrid.Columns = 4;
        double toDpi = TryNum(_newDpi.Text, out var nd2) ? nd2 : dpi;
        double degPerInch = dpi * sens * YawPerUnit(_game, sens);   // degrees turned per inch of mouse movement
        foreach (var g in GamesList.Where(g => g.Yaw != null && g != _game))
        {
            double unit = g.Id == "fortnite" ? 0.005555 : g.Yaw.Value;
            double target = degPerInch / (toDpi * unit);
            var sp = new StackPanel();
            sp.Children.Add(UI.Text(g.Name, 11.5, "Sub", FontWeights.SemiBold, false));
            sp.Children.Add(UI.Text(FormatSens(target) + (g.Id == "fortnite" ? "%" : ""), 20, "Text", FontWeights.Bold, false));
            sp.Children.Add(UI.Text($"at {toDpi:0} DPI", 10.5, "Dim", wrap: false));
            var b = new Border { Child = sp, Background = UI.B("CardHover"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 10, 10), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Click to copy" };
            string copy = FormatSens(target);
            b.MouseLeftButtonUp += (_, _) => { try { Clipboard.SetText(copy); } catch { } };
            _convGrid.Children.Add(b);
        }
        _convGrid.Children.Add(new Border
        {
            Child = UI.Text($"{360.0 / degPerInch * 2.54:0.#} cm/360 everywhere", 12, "Good", FontWeights.SemiBold), Padding = new Thickness(14, 10, 14, 10), VerticalAlignment = VerticalAlignment.Center
        });
    }

    public void Refresh() { }
}
