using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Overdrive.Core;

namespace Overdrive.Pages;

public class NetworkPage : TweakListPage
{
    public NetworkPage() : base("Network & Ping",
        "Test your connection, find the closest server and fix what slows it down.",
        Catalog.Network, top: PingLab(), bottom: Tools())
    { }

    static UIElement PingLab()
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Section("Speed test"));
        sp.Children.Add(SpeedTestCard());
        sp.Children.Add(UI.Section("Ping Lab"));

        // ---------- region finder ----------
        var regionCard = new StackPanel();
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 14) };
        var results = new UniformGrid { Columns = 3 };
        var best = UI.Text("", 13, "Good", FontWeights.SemiBold);
        Button run = null;
        run = UI.Btn("Find my best region", async (s, e) => await UI.Busy(run, async () =>
        {
            results.Children.Clear();
            best.Text = "Testing... (about 15 seconds)";
            var cells = NetLab.Regions.Select(r => (r, cell: RegionCell(r.Name, r.Where))).ToList();
            foreach (var c in cells) results.Children.Add(c.cell.Root);
            var tasks = cells.Select(async c =>
            {
                var m = await NetLab.MeasureTcp(c.r.Host);
                c.cell.Set(m);
                return (c.r, m);
            }).ToList();
            var all = await Task.WhenAll(tasks);
            var winner = all.Where(a => a.m.AvgMs.HasValue).OrderBy(a => a.m.AvgMs).FirstOrDefault();
            best.Text = winner.r != null
                ? $"Closest region: {winner.r.Name} ({winner.r.Where}) at about {winner.m.AvgMs:0} ms. Pick this region in your game's matchmaking settings."
                : "Couldn't reach any region. Check your internet connection.";
        }), accent: true, icon: "");
        DockPanel.SetDock(run, Dock.Right); run.Margin = new Thickness(0);
        head.Children.Add(run);
        var ht = new StackPanel();
        ht.Children.Add(UI.Text("Server region finder", 15, "Text", FontWeights.SemiBold));
        ht.Children.Add(UI.Text("Your ping to the servers most games use.", 12, "Sub"));
        head.Children.Add(ht);
        regionCard.Children.Add(head);
        regionCard.Children.Add(results);
        best.Margin = new Thickness(0, 6, 0, 0);
        regionCard.Children.Add(best);
        sp.Children.Add(UI.Card(regionCard));

        // ---------- connection quality ----------
        var q = new StackPanel();
        var qHead = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var qOut = new StackPanel();
        var bar = new ProgressBar { Style = UI.Res<Style>("Meter"), Maximum = 40, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        Button qRun = null;
        qRun = UI.Btn("Test stability", async (s, e) => await UI.Busy(qRun, async () =>
        {
            qOut.Children.Clear();
            bar.Visibility = Visibility.Visible; bar.Value = 0;
            var m = await NetLab.MeasureIcmp("1.1.1.1", 40, new Progress<int>(v => bar.Value = v));
            bar.Visibility = Visibility.Collapsed;
            double loss = 100.0 * m.Lost / m.Sent;
            qOut.Children.Add(UI.Wrap(
                Metric("PING", m.AvgMs.HasValue ? $"{m.AvgMs:0} ms" : "—"),
                Metric("JITTER", $"{m.JitterMs:0.0} ms"),
                Metric("PACKET LOSS", $"{loss:0}%")));
            string verdict =
                !m.AvgMs.HasValue ? "No response. Your internet may be down or your network blocks ping."
                : loss >= 2 ? "Packet loss detected. This causes rubber-banding and shots that don't register. If you're on Wi-Fi, a cable will most likely fix it. If you're wired, restart your router or call your ISP."
                : m.JitterMs > 8 ? "Your ping is unstable (high jitter). That's usually Wi-Fi, or someone else using the internet at the same time. Try a cable, or pause downloads and streams."
                : "Your connection is solid. Any lag in games is distance to the server, so pick the closest region above.";
            var v = UI.Text(verdict, 13, loss >= 2 || m.JitterMs > 8 ? "Warn" : "Good");
            v.Margin = new Thickness(0, 10, 0, 0);
            qOut.Children.Add(v);
            if (NetLab.OnWifi()) qOut.Children.Add(UI.Tip("You're on Wi-Fi right now. An Ethernet cable is the single biggest ping and stability upgrade you can make.", "", "Warn"));
        }), icon: "");
        DockPanel.SetDock(qRun, Dock.Right); qRun.Margin = new Thickness(0);
        qHead.Children.Add(qRun);
        var qt = new StackPanel();
        qt.Children.Add(UI.Text("Connection stability", 15, "Text", FontWeights.SemiBold));
        qt.Children.Add(UI.Text("Checks jitter and packet loss, the real causes of lag spikes.", 12, "Sub"));
        qHead.Children.Add(qt);
        q.Children.Add(qHead);
        q.Children.Add(bar);
        q.Children.Add(qOut);
        sp.Children.Add(UI.Card(q));

        sp.Children.Add(UI.Section("Network tweaks"));
        return sp;
    }


    static UIElement SpeedTestCard()
    {
        var root = new StackPanel();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        g.ColumnDefinitions.Add(new ColumnDefinition());

        // left: animated speedometer + button
        var left = new StackPanel();
        var phase = UI.Label("READY");
        var gauge = new Gauge { Width = 250, Height = 230 };
        var big = new TextBlock(); // kept for text updates below (not displayed)
        var bar = new ProgressBar { Style = UI.Res<Style>("Meter"), Maximum = 1, Margin = new Thickness(0, 4, 0, 12), Width = 230, HorizontalAlignment = HorizontalAlignment.Left };
        left.Children.Add(gauge); left.Children.Add(bar);
        Button run = null;
        left.Children.Add(run = UI.Btn("Start speed test", null, accent: true, icon: "\uE896"));
        run.HorizontalAlignment = HorizontalAlignment.Left;
        g.Children.Add(left);

        // right: live graph
        var right = new StackPanel();
        right.Children.Add(UI.Label("LIVE SPEED"));
        var graph = new Graph { Height = 150, Capacity = 80, Unit = " Mbps", ShowStats = false, SecondsPerPoint = 0.25 };
        right.Children.Add(graph);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);

        var hero = UI.Card(g, background: UI.B("HeroGrad"));
        hero.Padding = new Thickness(24);
        root.Children.Add(hero);

        // results row
        var results = new UniformGrid { Columns = 5 };
        TextBlock R(string label, string sub)
        {
            var v = UI.Text("--", 24, "Text", FontWeights.Bold, false);
            var sp = new StackPanel();
            sp.Children.Add(UI.Label(label)); sp.Children.Add(v); sp.Children.Add(UI.Text(sub, 11, "Dim"));
            results.Children.Add(UI.Card(sp, new Thickness(0, 0, 10, 12)));
            return v;
        }
        var rDown = R("DOWNLOAD", "Mbps");
        var rUp = R("UPLOAD", "Mbps");
        var rPing = R("PING (IDLE)", "ms");
        var rLoaded = R("PING UNDER LOAD", "ms while downloading");
        var rGrade = R("LAG GRADE", "bufferbloat");
        root.Children.Add(results);

        var verdict = UI.Text("Download, upload and ping under load. About 25 seconds.", 13, "Sub");
        verdict.Margin = new Thickness(2, 0, 0, 12);
        root.Children.Add(verdict);

        // Wi-Fi details (no network names or addresses are ever shown)
        var wifiBox = new StackPanel();
        root.Children.Add(wifiBox);

        // history
        var hist = new StackPanel();
        root.Children.Add(hist);

        void ShowWifi()
        {
            Task.Run(NetLab.GetWifi).ContinueWith(t => wifiBox.Dispatcher.BeginInvoke(() =>
            {
                wifiBox.Children.Clear();
                var w = t.Result;
                var c = new StackPanel();
                if (w == null)
                {
                    c.Children.Add(UI.Row(new TextBlock { Text = "\uE839", FontFamily = UI.IconFont, FontSize = 16, Foreground = UI.B("Good"), Margin = new Thickness(0, 0, 10, 0) },
                        UI.Text("You're on a wired (Ethernet) connection. That's the best setup for gaming.", 13, "Good", FontWeights.SemiBold)));
                }
                else
                {
                    var row = new UniformGrid { Columns = 4 };
                    Border F(string l, string v, string color = "Text") { var sp = new StackPanel(); sp.Children.Add(UI.Label(l)); sp.Children.Add(UI.Text(v, 18, color, FontWeights.Bold, false)); return new Border { Child = sp, Margin = new Thickness(0, 0, 12, 0) }; }
                    row.Children.Add(F("WI-FI SIGNAL", w.SignalPct.HasValue ? $"{w.SignalPct}%" : "-", w.SignalPct is < 60 ? "Warn" : "Good"));
                    row.Children.Add(F("BAND", w.Band ?? "-", w.Band?.StartsWith("2.4") == true ? "Warn" : "Text"));
                    row.Children.Add(F("CHANNEL", w.Channel?.ToString() ?? "-"));
                    row.Children.Add(F("LINK SPEED", w.RxMbps.HasValue ? $"{w.RxMbps:0} Mbps" : "-"));
                    c.Children.Add(row);
                    var tips = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                    if (w.SignalPct is < 60) tips.Children.Add(UI.Tip("Weak signal. Move closer to the router, or get it out of cupboards and away from walls. Weak Wi-Fi causes packet loss and ping spikes.", "\uE7BA", "Warn"));
                    if (w.Band?.StartsWith("2.4") == true) tips.Children.Add(UI.Tip("You're on 2.4 GHz, which is slower and crowded. Connect to your router's 5 GHz (or 6 GHz) network for much better speed and ping.", "\uE7BA", "Warn"));
                    if (w.RxMbps is double rx && rx < 300) tips.Children.Add(UI.Tip($"Your Wi-Fi link is only {rx:0} Mbps, which may cap your internet speed. 5 GHz and a stronger signal raise it.", "\uE7BA", "Warn"));
                    tips.Children.Add(UI.Tip("For gaming, an Ethernet cable beats any Wi-Fi: lower ping, no spikes, no packet loss."));
                    c.Children.Add(tips);
                }
                wifiBox.Children.Add(UI.Card(c));
            }));
        }

        void ShowHistory()
        {
            hist.Children.Clear();
            var list = Store.Settings.SpeedHistory.OrderByDescending(h => h.At).Take(10).ToList();
            if (list.Count == 0) return;
            hist.Children.Add(UI.Section("Recent tests"));
            var c = new StackPanel();
            foreach (var h in list)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                foreach (var w in new[] { 1.4, 1, 1, 1, 0.7, 0.8 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
                void Cell(int col, string text, string color = "Text") { var t = UI.Text(text, 13, color, FontWeights.SemiBold, false); Grid.SetColumn(t, col); row.Children.Add(t); }
                Cell(0, h.At.ToString("MMM d, h:mm tt"), "Sub");
                Cell(1, $"↓ {h.Down:0} Mbps");
                Cell(2, $"↑ {h.Up:0} Mbps");
                Cell(3, h.Ping.HasValue ? $"{h.Ping:0} ms" : "-");
                Cell(4, h.Grade ?? "-", h.Grade?.StartsWith("A") == true ? "Good" : "Warn");
                Cell(5, h.Via ?? "", "Dim");
                c.Children.Add(row);
            }
            hist.Children.Add(UI.Card(c));
        }

        run.Click += async (s, e) => { await UI.Busy(run, async () =>
        {
            graph.Clear(); big.Text = "0"; rDown.Text = rUp.Text = rPing.Text = rLoaded.Text = rGrade.Text = "--";
            gauge.ColorA = (Color)ColorConverter.ConvertFromString("#00E5FF"); gauge.ColorB = (Color)ColorConverter.ConvertFromString("#8B5CF6");
            gauge.Phase = "PING"; gauge.Value = 0; gauge.SetRunning(true);
            string lastPhase = null;
            var res = await NetLab.SpeedTest(new Progress<NetLab.SpeedProgress>(p =>
            {
                if (p.Phase != lastPhase)
                {
                    lastPhase = p.Phase;
                    if (p.Phase == "Upload")
                    {
                        graph.Line = (Color)ColorConverter.ConvertFromString("#B69CFF"); graph.Clear();
                        gauge.ColorA = (Color)ColorConverter.ConvertFromString("#B69CFF"); gauge.ColorB = (Color)ColorConverter.ConvertFromString("#FF8AD8");
                        gauge.Value = 0; // needle drops back before the upload test
                    }
                    if (p.Phase == "Download") { graph.Line = (Color)ColorConverter.ConvertFromString("#00E5FF"); graph.Clear(); }
                }
                phase.Text = p.Phase.ToUpperInvariant() + (p.Phase == "Ping" ? "" : "ING...");
                gauge.Phase = p.Phase == "Ping" ? "PING" : p.Phase.ToUpperInvariant();
                if (p.Phase != "Ping") gauge.Value = p.CurrentMbps;
                bar.Value = p.Fraction;
                if (p.Phase == "Ping") { if (p.LivePingMs.HasValue) { rPing.Text = $"{p.LivePingMs:0}"; } big.Text = "--"; }
                else { big.Text = $"{p.CurrentMbps:0}"; graph.Push(p.CurrentMbps); }
                if (p.LivePingMs.HasValue && p.Phase == "Download") rLoaded.Text = $"{p.LivePingMs:0}";
            }));

            phase.Text = "DONE";
            big.Text = $"{res.DownMbps:0}";
            gauge.SetRunning(false);
            gauge.ColorA = (Color)ColorConverter.ConvertFromString("#00E5FF"); gauge.ColorB = (Color)ColorConverter.ConvertFromString("#8B5CF6");
            gauge.Phase = "DOWNLOAD"; gauge.Value = res.DownMbps; // settle on your download speed
            bar.Value = 1;
            rDown.Text = $"{res.DownMbps:0}";
            rUp.Text = $"{res.UpMbps:0}";
            rPing.Text = res.PingMs.HasValue ? $"{res.PingMs:0}" : "--";
            var loaded = new[] { res.LoadedDownPingMs, res.LoadedUpPingMs }.Where(x => x.HasValue).Select(x => x.Value).DefaultIfEmpty().Max();
            rLoaded.Text = loaded > 0 ? $"{loaded:0}" : "--";
            var grade = NetLab.BloatGrade(res.PingMs, loaded > 0 ? loaded : null);
            rGrade.Text = grade;
            rGrade.Foreground = UI.B(grade.StartsWith("A") ? "Good" : grade == "B" ? "Accent" : "Warn");

            if (res.DownMbps <= 0) { verdict.Text = "Couldn't reach the speed test server. Check your connection."; verdict.Foreground = UI.B("Warn"); return; }
            var gbMin = res.DownMbps / 8 / 1000 * 60;
            string bloat = grade.StartsWith("A") ? "Your ping stays stable even when the connection is busy. Great for gaming."
                : grade == "B" ? "Your ping rises a bit when the connection is busy. Fine, but downloads during a match can cause small lag spikes."
                : "Your ping shoots up when the connection is busy (bufferbloat). Anyone downloading or streaming in your house will cause lag spikes. Fix it by turning on 'SQM', 'Smart QoS' or 'Gaming mode' in your router settings.";
            verdict.Text = $"{bloat} A 50 GB game takes about {Math.Max(1, 50 / Math.Max(gbMin, 0.001)):0} minutes to download at this speed.";
            verdict.Foreground = UI.B("Text");

            var wifi = await Task.Run(NetLab.GetWifi);
            Store.Settings.SpeedHistory.Add(new SpeedRecord { At = res.At, Down = res.DownMbps, Up = res.UpMbps, Ping = res.PingMs, Grade = grade, Via = wifi == null ? "Ethernet" : $"Wi-Fi {wifi.Band}" });
            if (Store.Settings.SpeedHistory.Count > 30) Store.Settings.SpeedHistory.RemoveAt(0);
            Store.SaveSettings();
            ShowHistory();
            ShowWifi();
        }); gauge.SetRunning(false); };

        ShowWifi();
        ShowHistory();
        return root;
    }

    static Border Metric(string label, string value)
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Label(label));
        sp.Children.Add(UI.Text(value, 22, "Text", FontWeights.Bold));
        return new Border { Background = UI.Hex("#0F131B"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 10, 0), Child = sp, MinWidth = 140 };
    }

    class Cell
    {
        public Border Root;
        public TextBlock Ms;
        public void Set(NetLab.Measurement m) => Root.Dispatcher.Invoke(() =>
        {
            if (!m.AvgMs.HasValue) { Ms.Text = "timeout"; Ms.Foreground = UI.B("Dim"); return; }
            var v = m.AvgMs.Value;
            Ms.Text = $"{v:0} ms";
            Ms.Foreground = UI.B(v < 50 ? "Good" : v < 100 ? "Accent" : v < 160 ? "Warn" : "Bad");
        });
    }

    static Cell RegionCell(string name, string where)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel();
        sp.Children.Add(UI.Text(name, 13, "Text", FontWeights.SemiBold));
        sp.Children.Add(UI.Text(where, 11, "Dim"));
        g.Children.Add(sp);
        var ms = UI.Text("...", 16, "Sub", FontWeights.Bold, wrap: false);
        ms.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(ms, 1); g.Children.Add(ms);
        var root = new Border { Background = UI.Hex("#0F131B"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 8, 8), Child = g };
        return new Cell { Root = root, Ms = ms };
    }

    static UIElement Tools()
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Section("More"));
        var c = new StackPanel();
        c.Children.Add(UI.Text("Use these if games can't connect or the internet acts weird.", 13, "Sub"));
        Button flush = null, reset = null;
        flush = UI.Btn("Flush DNS", async (s, e) => await UI.Busy(flush, async () => { await Task.Run(NetLab.FlushDns); UI.Info("DNS cache cleared."); }), icon: "");
        reset = UI.Btn("Reset network stack", async (s, e) => await UI.Busy(reset, async () =>
        {
            if (!UI.Confirm("This resets Windows networking (Winsock + TCP/IP) to factory settings. VPN adapters and static IPs may need to be set up again. Continue?")) return;
            var msg = await Task.Run(NetLab.ResetNetworkStack);
            UI.Info(msg);
        }), icon: "");
        var row = UI.Row(flush, reset); row.Margin = new Thickness(0, 12, 0, 0);
        c.Children.Add(row);
        sp.Children.Add(UI.Collapsible("Fix-it tools (Flush DNS, reset network)", c, "\uE90F"));
        return sp;
    }
}
