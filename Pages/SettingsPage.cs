using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Overdrive.Core;

namespace Overdrive.Pages;

public class SettingsPage : UserControl, IRefreshable
{
    readonly MainWindow _main;
    readonly StackPanel _kill = new();

    public SettingsPage(MainWindow main)
    {
        _main = main;
        var body = new StackPanel();
        body.Children.Add(UI.Header("Settings", null));

        // ---------- game boost ----------
        body.Children.Add(UI.Section("Game Boost"));
        var gb = new StackPanel();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel();
        t.Children.Add(UI.Text("Auto-boost when a game starts", 15, "Text", FontWeights.SemiBold));
        t.Children.Add(UI.Text("Max power, 0.5 ms timer and closes the apps below while you play. Never touches the game.", 12, "Sub"));
        g.Children.Add(t);
        var sw = new CheckBox { Style = UI.Res<Style>("Switch"), IsChecked = Store.Settings.AutoBoost, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
        sw.Checked += (_, _) => { Store.Settings.AutoBoost = true; Store.SaveSettings(); _main.UpdateBoost(BoostMode.Status); };
        sw.Unchecked += (_, _) => { Store.Settings.AutoBoost = false; Store.SaveSettings(); _main.UpdateBoost(BoostMode.Status); };
        Grid.SetColumn(sw, 1); g.Children.Add(sw);
        gb.Children.Add(g);
        gb.Children.Add(LowEndRow());
        var kl = UI.Label("CLOSE THESE WHILE GAMING");
        kl.Margin = new Thickness(0, 18, 0, 10);
        gb.Children.Add(kl);
        gb.Children.Add(_kill);
        body.Children.Add(UI.Card(gb));

        // ---------- overlay ----------
        body.Children.Add(UI.Section("In-game overlay"));
        var og = new Grid();
        og.ColumnDefinitions.Add(new ColumnDefinition());
        og.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ot = new StackPanel();
        ot.Children.Add(UI.Text("Show the FPS overlay while gaming", 15, "Text", FontWeights.SemiBold));
        ot.Children.Add(UI.Text("FPS, lows and hardware stats in-game. Ctrl+Shift+O to toggle. Turn off for ranked: overlays cost a little FPS.", 12, "Sub"));
        og.Children.Add(ot);
        var osw = new CheckBox { Style = UI.Res<Style>("Switch"), IsChecked = Store.Settings.FpsOverlay, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
        osw.Checked += (_, _) => { if (!Store.Settings.FpsOverlay) LiveMonitor.SetOverlay(true); };
        osw.Unchecked += (_, _) => { if (Store.Settings.FpsOverlay) LiveMonitor.SetOverlay(false); };
        LiveMonitor.OverlayChanged += () => osw.IsChecked = Store.Settings.FpsOverlay;
        Grid.SetColumn(osw, 1); og.Children.Add(osw);
        body.Children.Add(UI.Card(og));

        // ---------- tray ----------
        body.Children.Add(UI.Section("System tray"));
        var tg = new Grid();
        tg.ColumnDefinitions.Add(new ColumnDefinition());
        tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tt = new StackPanel();
        tt.Children.Add(UI.Text("Keep running in the tray when closed", 15, "Text", FontWeights.SemiBold));
        tt.Children.Add(UI.Text("X hides to the tray so Game Boost keeps working. Right-click the tray icon to quit.", 12, "Sub"));
        tg.Children.Add(tt);
        var tsw = new CheckBox { Style = UI.Res<Style>("Switch"), IsChecked = Store.Settings.CloseToTray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
        tsw.Checked += (_, _) => { Store.Settings.CloseToTray = true; Store.SaveSettings(); };
        tsw.Unchecked += (_, _) => { Store.Settings.CloseToTray = false; Store.SaveSettings(); };
        Grid.SetColumn(tsw, 1); tg.Children.Add(tsw);
        body.Children.Add(UI.Card(tg));

        // ---------- privacy ----------
        body.Children.Add(UI.Section("Privacy"));
        body.Children.Add(UI.Card(UI.Text("No IP addresses or Wi-Fi names are ever shown, and nothing is sent anywhere. Safe to stream.", 13, "Sub")));

        // ---------- safety ----------
        body.Children.Add(UI.Section("Safety"));
        var sf = new StackPanel();
        sf.Children.Add(UI.Text("Every original setting is saved, so Undo everything restores your PC exactly.", 13, "Sub"));
        Button rp = null, undo = null;
        rp = UI.Btn("Create restore point", async (s, e) => await UI.Busy(rp, async () =>
        {
            await Task.Run(() => RestorePoint.Create("PC Optimizer - manual restore point"));
            UI.Info("Restore point created. To use it: Start > type 'Create a restore point' > System Restore.");
        }), icon: "");
        undo = UI.Btn("Undo everything PC Optimizer changed", async (s, e) => await UI.Busy(undo, UndoAll), style: "DangerButton", icon: "");
        var row = UI.Row(rp, undo); row.Margin = new Thickness(0, 14, 0, 0);
        sf.Children.Add(row);
        body.Children.Add(UI.Card(sf));

        // ---------- updates ----------
        body.Children.Add(UI.Section("Updates"));
        body.Children.Add(UI.Card(UpdateCard()));

        // ---------- about ----------
        body.Children.Add(UI.Section("About"));
        var ab = new StackPanel();
        ab.Children.Add(UI.Text($"PC Optimizer {App.VersionText}", 15, "Text", FontWeights.SemiBold));
        ab.Children.Add(UI.Text($"Build {App.BuildId.Split('+').LastOrDefault()}", 11, "Dim"));
        ab.Children.Add(UI.Text("No ads, no accounts. The only online calls are speed/ping tests and the update check on GitHub.", 12, "Sub"));
        var safe = UI.Text("Anti-cheat safe: only Windows and driver settings. Never touches games, their memory or files.", 12, "Good");
        safe.Margin = new Thickness(0, 8, 0, 0);
        ab.Children.Add(safe);
        var quit = UI.Btn("Quit PC Optimizer", (s, e) => Tray.Exit(), style: "DangerButton", icon: "\uE7E8");
        var links = UI.Row(quit,
            UI.Btn("Open log", (s, e) => { try { Process.Start("notepad.exe", Store.LogFile); } catch { } }, icon: ""),
            UI.Btn("Open backup folder", (s, e) => Shell.Open(Store.Dir), icon: ""));
        links.Margin = new Thickness(0, 14, 0, 0);
        ab.Children.Add(links);
        body.Children.Add(UI.Card(ab));

        Content = UI.PageScroll(body);
        Refresh();
    }

    public void Refresh()
    {
        _kill.Children.Clear();
        var wrap = new WrapPanel();
        foreach (var (proc, label) in BoostMode.SuggestedKill)
        {
            var cb = new CheckBox { Style = UI.Res<Style>("Check"), Content = UI.Text(label, 13), IsChecked = Store.Settings.BoostKillList.Contains(proc), Margin = new Thickness(0, 0, 24, 12), Width = 240 };
            cb.Checked += (_, _) => { if (!Store.Settings.BoostKillList.Contains(proc)) Store.Settings.BoostKillList.Add(proc); Store.SaveSettings(); };
            cb.Unchecked += (_, _) => { Store.Settings.BoostKillList.Remove(proc); Store.SaveSettings(); };
            wrap.Children.Add(cb);
        }
        _kill.Children.Add(wrap);
        _kill.Children.Add(UI.Text("Don't tick apps you need in-game, like Discord.", 11, "Dim"));
    }

    async Task UndoAll()
    {
        if (!UI.Confirm("Put every setting PC Optimizer changed back to how it was before?")) return;
        var ids = Store.Backups.Keys.ToList();
        var everything = Catalog.All.Concat(Games.All.SelectMany(gm => Games.TweaksFor(gm, () => Games.FindExe(gm)))).ToList();
        int done = 0, failed = 0;
        await Task.Run(() =>
        {
            foreach (var id in ids)
            {
                var tw = everything.FirstOrDefault(x => x.Id == id);
                try
                {
                    // NVIDIA settings that were removed from the app still need a real driver revert
                    var retiredNv = new Dictionary<string, uint> { ["nv.texture"] = Nvapi.QUALITY_ENHANCEMENTS, ["nv.aniso"] = Nvapi.ANISO_OPTS2, ["nv.aagamma"] = Nvapi.AA_GAMMA };
                    if (tw == null && retiredNv.TryGetValue(id, out var sid)) tw = Nvapi.CreateTweak(id, id, "", Impact.Low, sid, 0);
                    if (tw != null) tw.Revert();
                    else if (Store.Backups.TryGetValue(id, out var bk)) // tweak no longer listed: restore raw backup
                    {
                        foreach (var r in bk.Registry) Reg.Restore(r);
                        Store.Backups.Remove(id); Store.SaveBackups();
                    }
                    done++;
                }
                catch (Exception ex) { failed++; Store.Log($"Undo {id} failed: {ex.Message}"); }
            }
        });
        _main.RefreshAll();
        UI.Info($"Undid {done} change{(done == 1 ? "" : "s")}." + (failed > 0 ? $" {failed} couldn't be undone (see the log)." : "") + "\n\nRestart your PC to finish.");
    }

    internal static UIElement LowEndRow()
    {
        var g = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel();
        var tr = new StackPanel { Orientation = Orientation.Horizontal };
        tr.Children.Add(UI.Text("Low-end PC boost", 15, "Text", FontWeights.SemiBold, false));
        if (LowEnd.IsLowEndPc) { var b = UI.Badge("RECOMMENDED FOR YOUR PC", "#2EE59D", "#123326"); b.Margin = new Thickness(10, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Center; tr.Children.Add(b); }
        tr.Children.Add(UI.InfoDot("While a game runs: pauses Windows Update, Search indexing, SysMain and telemetry, lowers the priority of browsers/Discord/launchers, and frees cached RAM when memory is low. Everything goes back when you close the game." + (LowEnd.IsLowEndPc ? $" Detected: {LowEnd.Why()}." : "")));
        t.Children.Add(tr);
        t.Children.Add(UI.Text("Pauses background Windows work and frees RAM during games.", 12, "Sub"));
        g.Children.Add(t);
        var sw = new CheckBox { Style = UI.Res<Style>("Switch"), IsChecked = LowEnd.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
        sw.Checked += (_, _) => { Store.Settings.LowEndMode = true; Store.SaveSettings(); };
        sw.Unchecked += (_, _) => { Store.Settings.LowEndMode = false; Store.SaveSettings(); };
        Grid.SetColumn(sw, 1); g.Children.Add(sw);
        return g;
    }

    static UIElement UpdateCard()
    {
        var sp = new StackPanel();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        var title = UI.Text($"You're on version {App.VersionText}", 15, "Text", FontWeights.SemiBold, false);
        var status = UI.Text("Updates come from GitHub and install in one click.", 12, "Sub");
        left.Children.Add(title); left.Children.Add(status);
        g.Children.Add(left);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button check = null, update = null;
        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        void ShowAvailable(Updater.Release r)
        {
            title.Text = $"Update available: {Updater.Label(r)}";
            title.Foreground = UI.B("Good");
            var notes = (r.Notes ?? "").Trim();
            status.Text = notes.Length == 0 ? $"You're on {App.VersionText}." : (notes.Length > 220 ? notes[..220] + "..." : notes);
            update.Visibility = Visibility.Visible;
        }
        check = UI.Btn("Check for updates", async (s, e) => await UI.Busy(check, async () =>
        {
            try
            {
                var r = await Updater.CheckAsync();
                if (r != null) ShowAvailable(r);
                else { title.Text = $"You're up to date ({App.VersionText})"; status.Text = (Updater.LastSeen ?? "Checked just now.") + "  ·  checked just now"; }
            }
            catch (Exception ex) { status.Text = "Couldn't check: " + ex.Message; Store.Log("Manual update check failed: " + ex); }
        }), icon: "\uE72C");
        update = UI.Btn("Update now", async (s, e) => await UI.Busy(update, async () =>
        {
            var r = Updater.Available; if (r == null) return;
            bar.Visibility = Visibility.Visible;
            status.Text = "Downloading...";
            try
            {
                await Updater.InstallAsync(r, new Progress<double>(v => { bar.Value = v; status.Text = $"Downloading... {v * 100:0}%"; }));
                status.Text = "Installing. PC Optimizer will restart in a moment.";
            }
            catch (Exception ex) { status.Text = "Update failed: " + ex.Message; bar.Visibility = Visibility.Collapsed; }
        }), accent: true, icon: "\uE896");
        update.Visibility = Visibility.Collapsed; update.Margin = new Thickness(8, 0, 0, 0);
        btns.Children.Add(check); btns.Children.Add(update);
        Grid.SetColumn(btns, 1); g.Children.Add(btns);
        sp.Children.Add(g);
        sp.Children.Add(bar);
        var auto = new CheckBox { Style = UI.Res<Style>("Check"), Content = UI.Text("Check for updates automatically", 12.5, "Sub"), IsChecked = Store.Settings.AutoUpdateCheck, Margin = new Thickness(0, 14, 0, 0) };
        auto.Checked += (_, _) => { Store.Settings.AutoUpdateCheck = true; Store.SaveSettings(); };
        auto.Unchecked += (_, _) => { Store.Settings.AutoUpdateCheck = false; Store.SaveSettings(); };
        sp.Children.Add(auto);
        var inst = new CheckBox { Style = UI.Res<Style>("Check"), Content = UI.Text("Install updates automatically (never during a game)", 12.5, "Sub"), IsChecked = Store.Settings.AutoInstallUpdates, Margin = new Thickness(0, 6, 0, 0) };
        inst.Checked += (_, _) => { Store.Settings.AutoInstallUpdates = true; Store.SaveSettings(); };
        inst.Unchecked += (_, _) => { Store.Settings.AutoInstallUpdates = false; Store.SaveSettings(); };
        sp.Children.Add(inst);
        if (Updater.Available != null) ShowAvailable(Updater.Available);
        return sp;
    }
}
