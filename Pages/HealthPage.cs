using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Overdrive.Core;

namespace Overdrive.Pages;

public class HealthPage : UserControl
{
    static HealthCheck.Result _last;
    static DateTime _lastAt;

    readonly TextBlock _score = UI.Text("--", 56, "Text", FontWeights.Black, false);
    readonly TextBlock _scoreSub = UI.Text("Run a check to see how healthy your PC is.", 13, "Sub");
    readonly StackPanel _items = new();
    readonly StackPanel _crashes = new();
    Button _run;

    public HealthPage()
    {
        var body = new StackPanel();
        body.Children.Add(UI.Header("PC Health", "Crashes, drive health, RAM speed, drivers and more in one check."));

        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sc = new StackPanel { Margin = new Thickness(0, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center };
        sc.Children.Add(UI.Label("HEALTH SCORE"));
        sc.Children.Add(_score);
        hero.Children.Add(sc);
        _scoreSub.VerticalAlignment = VerticalAlignment.Center; _scoreSub.MaxWidth = 460;
        Grid.SetColumn(_scoreSub, 1); hero.Children.Add(_scoreSub);
        _run = UI.Btn("Run health check", async (s, e) => await Run(), accent: true, icon: "");
        _run.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_run, 2); hero.Children.Add(_run);
        var heroCard = UI.Card(hero, background: UI.B("HeroGrad")); heroCard.Margin = new Thickness(0, 0, 0, 16);
        body.Children.Add(heroCard);

        body.Children.Add(_items);
        body.Children.Add(UI.SectionInfo("Crash Finder", "Reads Windows' own crash records from the last 30 days and explains what crashed, how often, and how to fix it."));
        body.Children.Add(_crashes);
        Content = UI.PageScroll(body);

        if (_last != null) Show(_last); else Loaded += async (_, _) => { if (_last == null) await Run(); };
    }

    async Task Run()
    {
        if (!_run.IsEnabled) return;
        _run.IsEnabled = false; _run.Content = "Checking...";
        _scoreSub.Text = "Checking crashes, drives, memory, drivers, display and Windows...";
        try
        {
            _last = await Task.Run(HealthCheck.Run);
            _lastAt = DateTime.Now;
            Show(_last);
        }
        catch (Exception ex) { _scoreSub.Text = "The check couldn't finish: " + ex.Message; Store.Log("Health check: " + ex); }
        finally { _run.IsEnabled = true; _run.Content = "Run again"; }
    }

    void Show(HealthCheck.Result r)
    {
        _score.Text = r.Score.ToString();
        _score.Foreground = UI.Hex(r.Score >= 85 ? "#2EE59D" : r.Score >= 65 ? "#F59E0B" : "#F43F5E");
        int bad = r.Items.Count(i => i.Level == HealthCheck.Level.Bad), warn = r.Items.Count(i => i.Level == HealthCheck.Level.Warn);
        _scoreSub.Text = (bad + warn == 0 ? "Everything looks healthy." : $"{bad} problem{(bad == 1 ? "" : "s")} and {warn} thing{(warn == 1 ? "" : "s")} worth fixing.") + $"  Checked {_lastAt:h:mm tt}.";

        _items.Children.Clear();
        foreach (var i in r.Items) _items.Children.Add(Row(i.Level, i.Area, i.Title, i.Detail, i.Fix, i.Action));

        _crashes.Children.Clear();
        var c = r.Crashes;
        if (c == null) { _crashes.Children.Add(UI.Text("Couldn't read the crash logs.", 13, "Sub")); return; }
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        chips.Children.Add(LabPage.StatBox("APP / GAME CRASHES", c.AppCrashes.ToString(), c.AppCrashes > 0 ? "#F59E0B" : "#2EE59D"));
        chips.Children.Add(LabPage.StatBox("BLUE SCREENS", c.BlueScreens.ToString(), c.BlueScreens > 0 ? "#F43F5E" : "#2EE59D"));
        chips.Children.Add(LabPage.StatBox("GPU DRIVER RESETS", c.GpuResets.ToString(), c.GpuResets > 0 ? "#F43F5E" : "#2EE59D"));
        chips.Children.Add(LabPage.StatBox("SUDDEN SHUTDOWNS", c.Shutdowns.ToString(), c.Shutdowns > 0 ? "#F59E0B" : "#2EE59D"));
        chips.Children.Add(LabPage.StatBox("HARDWARE ERRORS", c.HardwareErrors.ToString(), c.HardwareErrors > 0 ? "#F43F5E" : "#2EE59D"));
        _crashes.Children.Add(chips);
        if (c.Groups.Count == 0) { _crashes.Children.Add(UI.Tip("No crashes in the last 30 days.", "", "Good")); return; }
        foreach (var g in c.Groups.Take(15))
        {
            var lv = g.Kind is CrashFinder.Kind.BlueScreen or CrashFinder.Kind.Hardware or CrashFinder.Kind.GpuReset ? HealthCheck.Level.Bad : HealthCheck.Level.Warn;
            string title = $"{g.Name}{(g.IsGame ? "  (game)" : "")}: {(g.Kind == CrashFinder.Kind.Hang ? "froze" : "crashed")} {g.Count} time{(g.Count == 1 ? "" : "s")}";
            if (g.Kind is not (CrashFinder.Kind.App or CrashFinder.Kind.Hang)) title = $"{g.Name}: {g.Count} time{(g.Count == 1 ? "" : "s")}";
            _crashes.Children.Add(Row(lv, $"LAST {g.Last:MMM d}", title, g.Cause, g.Fix, g.Action));
        }
    }

    UIElement Row(HealthCheck.Level lv, string area, string title, string detail, string fix, string action)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string color = lv == HealthCheck.Level.Good ? "#2EE59D" : lv == HealthCheck.Level.Warn ? "#F59E0B" : "#F43F5E";
        string glyph = lv == HealthCheck.Level.Good ? "" : lv == HealthCheck.Level.Warn ? "" : "";
        g.Children.Add(new TextBlock { Text = glyph, FontFamily = UI.IconFont, FontSize = 16, Foreground = UI.Hex(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var t = new StackPanel();
        var tr = new StackPanel { Orientation = Orientation.Horizontal };
        tr.Children.Add(UI.Text(title, 14, "Text", FontWeights.SemiBold));
        var tag = UI.Text(area.ToUpperInvariant(), 9.5, "Dim", FontWeights.Bold, false); tag.Margin = new Thickness(10, 3, 0, 0);
        tr.Children.Add(tag);
        t.Children.Add(tr);
        if (!string.IsNullOrEmpty(detail)) t.Children.Add(UI.Text(detail, 12.5, "Sub"));
        if (!string.IsNullOrEmpty(fix)) { var f = UI.Text("Fix: " + fix, 12.5, "Text"); f.Margin = new Thickness(0, 4, 0, 0); t.Children.Add(f); }
        Grid.SetColumn(t, 1); g.Children.Add(t);
        var btn = ActionButton(action);
        if (btn != null) { btn.VerticalAlignment = VerticalAlignment.Center; btn.Margin = new Thickness(14, 0, 0, 0); Grid.SetColumn(btn, 2); g.Children.Add(btn); }
        var card = UI.Card(g, new Thickness(0, 0, 0, 10));
        card.Padding = new Thickness(18, 14, 18, 14);
        return card;
    }

    Button ActionButton(string action)
    {
        var main = Application.Current.MainWindow as MainWindow;
        switch (action)
        {
            case "gpu": return SysInfo.ShowNvidia || SysInfo.ShowAmd ? UI.Btn("Open GPU tab", (s, e) => main?.Navigate("NavGpu")) : null;
            case "cleanup": return UI.Btn("Open Cleanup", (s, e) => main?.Navigate("NavCleanup"));
            case "windows": return UI.Btn("Open Windows tab", (s, e) => main?.Navigate("NavWindows"));
            case "memtest": return UI.Btn("Memory test", (s, e) =>
            {
                if (UI.Confirm("Windows Memory Diagnostic will ask to restart your PC and test the RAM (about 15-20 minutes). Open it?"))
                    try { Process.Start(new ProcessStartInfo("mdsched.exe") { UseShellExecute = true }); } catch { }
            });
            case "vcredist": return UI.Btn("Download fix", (s, e) => { try { Process.Start(new ProcessStartInfo("https://aka.ms/vs/17/release/vc_redist.x64.exe") { UseShellExecute = true }); } catch { } });
            case "refresh":
            {
                Button b = null;
                b = UI.Btn("Fix it", (s, e) =>
                {
                    var d = Display.GetPrimary();
                    bool ok = d != null && Display.SetRefresh(d.MaxHz);
                    b.Content = ok ? $"Set to {d.MaxHz} Hz" : "Couldn't change it"; b.IsEnabled = false;
                }, accent: true);
                return b;
            }
        }
        return null;
    }
}
