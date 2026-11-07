using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Overdrive.Core;

namespace Overdrive.Pages;

/// <summary>
/// Live line graph: smooth curve, gradient fill, average line, min/max labels,
/// optional target line, a dot on the newest value and a hover readout.
/// </summary>
public class Graph : FrameworkElement
{
    readonly List<double> _values = new();
    double _shownMax = 1;          // eased so the scale doesn't jump around
    Point? _hover;

    public int Capacity { get; set; } = 90;
    public double? FixedMax { get; set; }
    public double? Target { get; set; }          // e.g. your monitor's refresh rate on the FPS graph
    public string TargetLabel { get; set; }
    public Color Line { get; set; } = (Color)ColorConverter.ConvertFromString("#00E5FF");
    public Color Line2 { get; set; } = (Color)ColorConverter.ConvertFromString("#8B5CF6");
    public bool ShowGrid { get; set; } = true;
    public bool ShowStats { get; set; } = true;  // avg line + min/max labels
    public bool Smooth { get; set; } = true;
    public string Unit { get; set; } = "";
    public string Format { get; set; } = "0";
    public double SecondsPerPoint { get; set; } = 1;

    public Graph()
    {
        ClipToBounds = true;
        MouseMove += (_, e) => { _hover = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
        Cursor = Cursors.Cross;
    }

    public void Push(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return;
        _values.Add(v);
        if (_values.Count > Capacity) _values.RemoveAt(0);
        InvalidateVisual();
    }

    public void SetAll(IEnumerable<double> values)
    {
        _values.Clear();
        _values.AddRange(values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).TakeLast(Capacity));
        InvalidateVisual();
    }

    public void Clear() { _values.Clear(); InvalidateVisual(); }
    public IReadOnlyList<double> Values => _values;

    static readonly Typeface Font = new("Segoe UI");
    static readonly Typeface FontBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    FormattedText Text(string s, double size, Color c, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? FontBold : Font, size, new SolidColorBrush(c), 1.0);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (double.IsNaN(w) || double.IsNaN(h) || w < 4 || h < 4 || Capacity < 2) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable for hover

        // ---- scale (eased auto-scale)
        double dataMax = _values.Count == 0 ? 1 : _values.Max();
        if (Target.HasValue) dataMax = Math.Max(dataMax, Target.Value);
        double wanted = FixedMax ?? NiceCeil(Math.Max(1, dataMax * 1.15));
        _shownMax = FixedMax ?? (_shownMax <= 1 ? wanted : _shownMax + (wanted - _shownMax) * 0.35);
        double max = Math.Max(_shownMax, 0.0001);
        double top = ShowGrid ? 6 : 2, bottom = h - 2;
        double Y(double v) => bottom - Math.Clamp(v / max, 0, 1) * (bottom - top);

        // ---- grid + scale labels
        if (ShowGrid)
        {
            var gp = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1); gp.Freeze();
            for (int i = 0; i <= 4; i++)
            {
                double v = max * i / 4, y = Math.Round(Y(v)) + .5;
                dc.DrawLine(gp, new Point(0, y), new Point(w, y));
                if (i > 0) dc.DrawText(Text(v.ToString(Format) + Unit, 9.5, Color.FromArgb(95, 255, 255, 255)), new Point(4, y + 1));
            }
        }

        // ---- target line
        if (Target.HasValue && Target.Value > 0)
        {
            double ty = Math.Round(Y(Target.Value)) + .5;
            var tp = new Pen(new SolidColorBrush(Color.FromArgb(150, 46, 229, 157)), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
            dc.DrawLine(tp, new Point(0, ty), new Point(w, ty));
            var lbl = Text(TargetLabel ?? $"{Target.Value:0}", 9.5, Color.FromArgb(200, 46, 229, 157), true);
            dc.DrawText(lbl, new Point(w - lbl.Width - 6, ty - lbl.Height - 1));
        }

        if (_values.Count < 2) { DrawEmpty(dc, w, h); return; }

        double step = w / (Capacity - 1);
        double x0 = w - (_values.Count - 1) * step;
        var pts = _values.Select((v, i) => new Point(x0 + i * step, Y(v))).ToList();

        // ---- curve + fill
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var lc = line.Open())
        using (var fc = fill.Open())
        {
            lc.BeginFigure(pts[0], false, false);
            fc.BeginFigure(new Point(pts[0].X, bottom), true, true);
            fc.LineTo(pts[0], false, false);
            for (int i = 1; i < pts.Count; i++)
            {
                if (Smooth && pts.Count > 2)
                {
                    // Catmull-Rom -> Bezier for a smooth line that still passes through every sample
                    var p0 = pts[Math.Max(0, i - 2)]; var p1 = pts[i - 1]; var p2 = pts[i]; var p3 = pts[Math.Min(pts.Count - 1, i + 1)];
                    var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                    var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                    c1.Y = Math.Clamp(c1.Y, top, bottom); c2.Y = Math.Clamp(c2.Y, top, bottom);
                    lc.BezierTo(c1, c2, p2, true, true);
                    fc.BezierTo(c1, c2, p2, false, true);
                }
                else { lc.LineTo(pts[i], true, true); fc.LineTo(pts[i], false, true); }
            }
            fc.LineTo(new Point(pts[^1].X, bottom), false, false);
        }
        line.Freeze(); fill.Freeze();
        var area = new LinearGradientBrush(Color.FromArgb(110, Line.R, Line.G, Line.B), Color.FromArgb(0, Line2.R, Line2.G, Line2.B), 90);
        dc.DrawGeometry(area, null, fill);
        var glow = new Pen(new SolidColorBrush(Color.FromArgb(45, Line.R, Line.G, Line.B)), 6) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, glow, line);
        dc.DrawGeometry(null, new Pen(new LinearGradientBrush(Line, Line2, 0), 2) { LineJoin = PenLineJoin.Round }, line);

        // ---- stats: average line + min/max
        if (ShowStats)
        {
            double avg = _values.Average(), mn = _values.Min(), mx = _values.Max();
            double ay = Math.Round(Y(avg)) + .5;
            var ap = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
            dc.DrawLine(ap, new Point(0, ay), new Point(w, ay));
            var s = Text($"avg {avg.ToString(Format)}{Unit}   min {mn.ToString(Format)}   max {mx.ToString(Format)}", 9.5, Color.FromArgb(170, 255, 255, 255));
            var bg = new Rect(w - s.Width - 12, h - s.Height - 6, s.Width + 8, s.Height + 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, 10, 12, 17)), null, bg, 4, 4);
            dc.DrawText(s, new Point(bg.X + 4, bg.Y + 1));
        }

        // ---- newest value dot
        var last = pts[^1];
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, Line2.R, Line2.G, Line2.B)), null, last, 7, 7);
        dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Line2), 2), last, 3.5, 3.5);

        // ---- hover readout
        if (_hover.HasValue)
        {
            int idx = (int)Math.Round((_hover.Value.X - x0) / step);
            if (idx >= 0 && idx < _values.Count)
            {
                var p = pts[idx];
                var hp = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
                dc.DrawLine(hp, new Point(Math.Round(p.X) + .5, top), new Point(Math.Round(p.X) + .5, bottom));
                dc.DrawEllipse(new SolidColorBrush(Line), null, p, 4, 4);
                double ago = (_values.Count - 1 - idx) * SecondsPerPoint;
                var t = Text($"{_values[idx].ToString(Format)}{Unit}", 12, Colors.White, true);
                var t2 = Text(ago < 0.5 ? "now" : $"{ago:0}s ago", 9.5, Color.FromArgb(170, 255, 255, 255));
                double bw = Math.Max(t.Width, t2.Width) + 14, bh = t.Height + t2.Height + 8;
                double bx = Math.Clamp(p.X + 10, 0, w - bw), by = Math.Clamp(p.Y - bh - 8, 0, h - bh);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 19, 23, 33)), new Pen(new SolidColorBrush(Color.FromArgb(80, Line.R, Line.G, Line.B)), 1), new Rect(bx, by, bw, bh), 6, 6);
                dc.DrawText(t, new Point(bx + 7, by + 4));
                dc.DrawText(t2, new Point(bx + 7, by + 4 + t.Height));
            }
        }
    }

    void DrawEmpty(DrawingContext dc, double w, double h)
    {
        if (h < 40) return;
        var t = Text("waiting for data...", 11, Color.FromArgb(90, 255, 255, 255));
        dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
    }

    static double NiceCeil(double v)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (m * mag >= v) return m * mag;
        return 10 * mag;
    }
}

/// <summary>Text that is blurred in Streamer mode until clicked. For IPs, Wi-Fi names and the like.</summary>
public class PrivateText : Border
{
    readonly TextBlock _t;
    bool _revealed;
    public PrivateText(string text, double size = 14)
    {
        _t = UI.Text(text, size, "Text", FontWeights.SemiBold, false);
        Child = _t;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        MouseLeftButtonUp += (_, _) => { _revealed = !_revealed; Update(); };
        Update();
    }
    public string Text { get => _t.Text; set => _t.Text = value; }

    public void Update()
    {
        bool hide = Store.Settings.StreamerMode && !_revealed;
        _t.Effect = hide ? new BlurEffect { Radius = 9 } : null;
        ToolTip = hide ? "Hidden by Streamer mode. Click to show." : (Store.Settings.StreamerMode ? "Click to hide" : null);
    }
}

/// <summary>Animated speedometer: needle and arc glide to the value, glow pulses while a test is running.</summary>
public class Gauge : FrameworkElement
{
    double _target, _shown;
    bool _hooked;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    static readonly double[] MbpsTicks = { 0, 5, 10, 50, 100, 250, 500, 750, 1000 };
    public double[] Ticks { get; set; } = MbpsTicks;
    public string UnitText { get; set; } = "Mbps";

    public double Value { get => _target; set { _target = Math.Max(0, value); Hook(); } }
    public bool Running { get; set; }
    public string Phase { get; set; } = "READY";
    public Color ColorA { get; set; } = (Color)ColorConverter.ConvertFromString("#00E5FF");
    public Color ColorB { get; set; } = (Color)ColorConverter.ConvertFromString("#8B5CF6");

    public void SetRunning(bool on) { Running = on; Hook(); }

    void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object s, EventArgs e)
    {
        // ease toward the target; keep animating while running (for the pulse)
        _shown += (_target - _shown) * 0.12;
        if (Math.Abs(_target - _shown) < 0.05) _shown = _target;
        InvalidateVisual();
        if (!Running && _shown == _target) { CompositionTarget.Rendering -= OnFrame; _hooked = false; }
    }

    // non-linear scale like real speed tests: lots of room for 0-100 Mbps, still reaches 1 Gbps
    double Frac(double v)
    {
        v = Math.Clamp(v, 0, Ticks[^1]);
        for (int i = 1; i < Ticks.Length; i++)
            if (v <= Ticks[i]) return ((i - 1) + (v - Ticks[i - 1]) / (Ticks[i] - Ticks[i - 1])) / (Ticks.Length - 1);
        return 1;
    }

    const double StartDeg = 135, SweepDeg = 270;
    static Point P(Point c, double r, double deg) { var a = deg * Math.PI / 180; return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)); }

    static Geometry Arc(Point c, double r, double fromDeg, double sweep)
    {
        if (sweep <= 0.01) return Geometry.Empty;
        var fig = new PathFigure { StartPoint = P(c, r, fromDeg), IsClosed = false };
        fig.Segments.Add(new ArcSegment(P(c, r, fromDeg + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        var g = new PathGeometry(); g.Figures.Add(fig); g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (double.IsNaN(w) || w < 60 || h < 60) return;
        double r = Math.Min(w, h * 1.15) / 2 - 16;
        if (r < 20) return;
        var c = new Point(w / 2, r + 14);
        double f = Frac(_shown);

        // pulse glow while running
        if (Running)
        {
            double pulse = 0.5 + 0.5 * Math.Sin(_clock.Elapsed.TotalSeconds * 4);
            var glowPen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(25 + 45 * pulse), ColorA.R, ColorA.G, ColorA.B)), 30) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, glowPen, Arc(c, r, StartDeg, SweepDeg * Math.Max(f, 0.02)));
        }

        // track
        var track = new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x33)), 14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(c, r, StartDeg, SweepDeg));
        // value arc
        var valPen = new Pen(new LinearGradientBrush(ColorA, ColorB, 0), 14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, valPen, Arc(c, r, StartDeg, SweepDeg * f));

        // tick labels
        for (int i = 0; i < Ticks.Length; i++)
        {
            double deg = StartDeg + SweepDeg * i / (Ticks.Length - 1);
            var label = UnitText == "Mbps" && Ticks[i] >= 1000 ? "1G" : Ticks[i] >= 1000 ? (Ticks[i] / 1000).ToString("0.#") + "k" : Ticks[i].ToString("0");
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10,
                new SolidColorBrush(Ticks[i] <= _shown ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(110, 255, 255, 255)), 1.0);
            var p = P(c, r - 26, deg);
            dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
        }

        // needle
        double nd = StartDeg + SweepDeg * f;
        var tip = P(c, r - 38, nd);
        var baseL = P(c, 6, nd - 90); var baseR = P(c, 6, nd + 90);
        var needle = new StreamGeometry();
        using (var g = needle.Open()) { g.BeginFigure(baseL, true, true); g.LineTo(tip, true, true); g.LineTo(baseR, true, true); }
        needle.Freeze();
        dc.DrawGeometry(new LinearGradientBrush(Colors.White, ColorA, 90), null, needle);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x13, 0x17, 0x21)), new Pen(new SolidColorBrush(ColorA), 3), c, 9, 9);

        // big number + phase
        var num = new FormattedText(_shown >= 100 ? _shown.ToString("0") : _shown.ToString("0.#"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal), 40, Brushes.White, 1.0);
        dc.DrawText(num, new Point(c.X - num.Width / 2, c.Y + 18));
        var unit = new FormattedText(UnitText + "  ·  " + Phase, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
            new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 1.0);
        dc.DrawText(unit, new Point(c.X - unit.Width / 2, c.Y + 18 + num.Height));
    }
}
