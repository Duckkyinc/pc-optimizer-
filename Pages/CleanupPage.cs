using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Overdrive.Core;

namespace Overdrive.Pages;

public class CleanupPage : UserControl, IRefreshable
{
    readonly List<CleanTarget> _targets = Cleaner.Targets();
    readonly StackPanel _startupList = new();
    readonly TextBlock _total = UI.Text("", 13, "Sub");

    public CleanupPage()
    {
        var body = new StackPanel();
        body.Children.Add(UI.Header("Cleanup", "Free up space and stop apps from starting with Windows."));

        // ---------- junk ----------
        body.Children.Add(UI.Section("Junk files"));
        var list = new StackPanel();
        foreach (var t in _targets.Where(t => !(t.Name.StartsWith("NVIDIA") && !SysInfo.ShowNvidia) && !(t.Name.StartsWith("AMD") && !SysInfo.ShowAmd)))
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cb = new CheckBox { Style = UI.Res<Style>("Check"), DataContext = t };
            cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(CleanTarget.Selected)) { Mode = BindingMode.TwoWay });
            var txt = new StackPanel();
            txt.Children.Add(UI.Text(t.Name, 14, "Text", FontWeights.SemiBold));
            txt.Children.Add(UI.Text(t.Description, 12, "Sub"));
            cb.Content = txt;
            g.Children.Add(cb);
            var size = UI.Text("", 13, "Accent", FontWeights.SemiBold, false);
            size.DataContext = t;
            size.SetBinding(TextBlock.TextProperty, new Binding(nameof(CleanTarget.SizeText)));
            size.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(size, 1);
            g.Children.Add(size);
            list.Children.Add(g);
        }
        Button scan = null, clean = null;
        scan = UI.Btn("Scan", async (s, e) => await UI.Busy(scan, Scan), icon: "");
        clean = UI.Btn("Clean selected", async (s, e) => await UI.Busy(clean, Clean), accent: true, icon: "");
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _total.VerticalAlignment = VerticalAlignment.Center;
        var btns = UI.Row(scan, clean); DockPanel.SetDock(btns, Dock.Right);
        row.Children.Add(btns); row.Children.Add(_total);
        list.Children.Add(row);
        body.Children.Add(UI.Card(list));

        // ---------- drives ----------
        body.Children.Add(UI.Section("Optimize drives"));
        var dc = new StackPanel();
        dc.Children.Add(UI.Text("TRIM for SSDs, defrag for hard drives. Safe, takes 1-5 minutes.", 13, "Sub"));
        var dStatus = UI.Text("", 12, "Dim"); dStatus.Margin = new Thickness(0, 10, 0, 0);
        Button opt = null;
        opt = UI.Btn("Optimize all drives", async (s, e) => await UI.Busy(opt, async () =>
        {
            dStatus.Text = "Optimizing... (you can keep using your PC)";
            var r = await Task.Run(() => Shell.Run("defrag", "/C /O /H", 30 * 60 * 1000));
            dStatus.Text = r.Code == 0 ? "Done. All drives optimized." : "Windows' optimizer reported a problem. Details are in the log.";
            Store.Log("defrag /C /O: " + r.Output);
            if (r.Code == 0) UI.Info("Done! Your drives are optimized.");
        }), icon: "\uEDA2");
        var dr = UI.Row(opt); dr.Margin = new Thickness(0, 12, 0, 0);
        dc.Children.Add(dr);
        dc.Children.Add(dStatus);
        body.Children.Add(UI.Card(dc));

        // ---------- startup ----------
        body.Children.Add(UI.Section("Startup apps"));
        var sc = new StackPanel();
        sc.Children.Add(UI.Text("Turn off apps you don't need at startup. Nothing is deleted.", 13, "Sub"));
        _startupList.Margin = new Thickness(0, 14, 0, 0);
        sc.Children.Add(_startupList);
        body.Children.Add(UI.Card(sc));

        Content = UI.PageScroll(body);
        Loaded += async (_, _) => { LoadStartup(); await Scan(); };
    }

    async Task Scan()
    {
        _total.Text = "Scanning...";
        foreach (var t in _targets) t.Bytes = -1;
        long sum = 0;
        foreach (var t in _targets)
        {
            var b = await Task.Run(() => Cleaner.Measure(t));
            t.Bytes = b; sum += b;
        }
        _total.Text = $"{Cleaner.Format(sum)} can be cleaned";
    }

    async Task Clean()
    {
        var sel = _targets.Where(t => t.Selected).ToList();
        if (sel.Count == 0) { UI.Info("Tick at least one item first."); return; }
        if (sel.Any(t => t.IsRecycleBin) && !UI.Confirm("This will permanently empty your Recycle Bin. Continue?")) return;
        long freed = 0;
        foreach (var t in sel) freed += await Task.Run(() => Cleaner.Clean(t));
        await Scan();
        UI.Info($"Freed {Cleaner.Format(freed)}. Files that were in use were skipped.");
    }

    void LoadStartup()
    {
        _startupList.Children.Clear();
        List<StartupItem> items;
        try { items = StartupApps.List(); } catch (Exception ex) { _startupList.Children.Add(UI.Text("Couldn't read startup apps: " + ex.Message, 12, "Warn")); return; }
        if (items.Count == 0) { _startupList.Children.Add(UI.Text("No startup apps found. Nice and clean!", 13, "Good")); return; }
        foreach (var i in items)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            sp.Children.Add(UI.Text(i.Name, 14, "Text", FontWeights.SemiBold));
            var cmd = UI.Text(i.Command, 11, "Dim", wrap: false); cmd.TextTrimming = TextTrimming.CharacterEllipsis; cmd.MaxWidth = 700; cmd.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(cmd);
            g.Children.Add(sp);
            var sw = new CheckBox { Style = UI.Res<Style>("Switch"), DataContext = i, VerticalAlignment = VerticalAlignment.Center };
            sw.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(StartupItem.Enabled)) { Mode = BindingMode.TwoWay });
            Grid.SetColumn(sw, 1); g.Children.Add(sw);
            _startupList.Children.Add(g);
        }
    }

    public void Refresh() => LoadStartup();
}
