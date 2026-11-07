using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Overdrive.Core;

namespace Overdrive.Pages;

public class GamesPage : UserControl, IRefreshable
{
    readonly Dictionary<string, string> _found = new();
    WrapPanel _tiles;
    GameDef _open;
    TweakListPage _detail;

    public GamesPage() { ShowGrid(); }

    public void Refresh() { if (_open != null) _detail?.Refresh(); }

    // ================================================================ grid
    static bool _scanned;
    bool _showOthers;

    void ShowGrid()
    {
        _open = null; _detail = null;
        var body = new StackPanel();
        body.Children.Add(UI.Header("Games", "Your installed games. Pick one for its boosts and best settings."));
        if (!_scanned)
        {
            body.Children.Add(UI.Card(UI.Row(
                new TextBlock { Text = "\uE895", FontFamily = UI.IconFont, FontSize = 16, Foreground = UI.B("Accent"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center },
                UI.Text("Scanning your PC for games...", 14, "Text", FontWeights.SemiBold))));
            Content = UI.PageScroll(body);
            _ = ScanThenShow();
            return;
        }

        var installed = Games.All.Where(g => _found.ContainsKey(g.Id)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var others = Games.Builtin.Where(g => !_found.ContainsKey(g.Id)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // ---- toolbar
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = false };
        var title = UI.Row(UI.Text("Your games", 18, "Text", FontWeights.SemiBold, false),
                           UI.Badge(installed.Count.ToString(), "#00E5FF", "#10283A"));
        ((FrameworkElement)title.Children[1]).Margin = new Thickness(10, 2, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(title);
        Button rescan = null;
        rescan = UI.Btn("Rescan", async (s, e) => await UI.Busy(rescan, async () => { _scanned = false; ShowGrid(); await Task.CompletedTask; }), icon: "\uE72C");
        rescan.Margin = new Thickness(0);
        DockPanel.SetDock(rescan, Dock.Right);
        bar.Children.Add(rescan);
        body.Children.Add(bar);

        _tiles = new WrapPanel();
        if (installed.Count == 0)
            body.Children.Add(UI.Card(UI.Text("No games found yet. Click 'Add any game' and pick its .exe, or start the game and click Rescan.", 13, "Sub")));
        foreach (var g in installed) _tiles.Children.Add(Tile(g, true));
        _tiles.Children.Add(AddTile());
        body.Children.Add(_tiles);

        // ---- supported but not installed (collapsed)
        if (others.Count > 0)
        {
            var toggle = UI.Btn(_showOthers ? $"Hide other supported games" : $"Show {others.Count} other supported games", (s, e) => { _showOthers = !_showOthers; ShowGrid(); },
                icon: _showOthers ? "\uE70E" : "\uE70D");
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            toggle.Margin = new Thickness(0, 14, 0, 14);
            body.Children.Add(toggle);
            if (_showOthers)
            {
                body.Children.Add(UI.Text("Not found on this PC. If one of these IS installed, open it and click 'Locate .exe'.", 12, "Dim"));
                var wp = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Opacity = 0.55 };
                foreach (var g in others) wp.Children.Add(Tile(g, false));
                body.Children.Add(wp);
            }
        }
        Content = UI.PageScroll(body);
    }

    async Task ScanThenShow()
    {
        var builtin = Games.Builtin.Concat(Games.All.Where(g => g.IsCustom)).ToList();
        var results = await Task.Run(() =>
        {
            var r = new Dictionary<string, string>();
            foreach (var g in builtin) { try { var p = Games.FindExe(g); if (p != null) r[g.Id] = p; } catch { } }
            try { Games.Discovered = Games.DiscoverLibrary(r.Values.ToList()); } catch (Exception ex) { Store.Log("Library scan failed: " + ex.Message); }
            foreach (var g in Games.Discovered) { try { var p = Games.FindExe(g); if (p != null) r[g.Id] = p; } catch { } }
            return r;
        });
        _found.Clear();
        foreach (var kv in results) _found[kv.Key] = kv.Value;
        _scanned = true;
        if (_open == null) ShowGrid();
    }

    Border Tile(GameDef g, bool installed)
    {
        var grad = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(g.ColorA), (Color)ColorConverter.ConvertFromString(g.ColorB), 45);
        var root = new Grid();

        // darkening overlay so the name stays readable over artwork (hidden until art loads)
        var shade = new Border
        {
            CornerRadius = new CornerRadius(12), Visibility = Visibility.Collapsed,
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.25),
                new GradientStop(Color.FromArgb(215, 6, 8, 12), 1.0),
            }, 90),
        };
        root.Children.Add(shade);

        var grid = new Grid { Margin = new Thickness(16, 14, 16, 14) };
        var letters = new TextBlock
        {
            Text = g.Short, FontSize = 46, FontWeight = FontWeights.Black, Foreground = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, -4, 0)
        };
        grid.Children.Add(letters);
        var icon = new Image { Width = 50, Height = 50, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        var name = new TextBlock { Text = g.Name, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        sp.Children.Add(name);
        sp.Children.Add(new TextBlock { Text = g.IsDiscovered ? "From your " + g.Genre + " library" : g.Genre, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) });
        grid.Children.Add(sp);
        var badge = UI.Badge(g.IsDiscovered ? g.Genre.ToUpperInvariant() : "INSTALLED", "#FFFFFF", "#66000000");
        badge.Name = "badge";
        badge.HorizontalAlignment = HorizontalAlignment.Left; badge.VerticalAlignment = VerticalAlignment.Top;
        badge.Visibility = g.IsDiscovered ? Visibility.Visible : Visibility.Collapsed;
        grid.Children.Add(badge);
        root.Children.Add(grid);

        var tile = new Border
        {
            Width = 218, Height = 128, CornerRadius = new CornerRadius(14), Background = grad,
            Margin = new Thickness(0, 0, 14, 14), Cursor = Cursors.Hand, Child = root, Tag = g.Id,
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2),
        };
        tile.MouseEnter += (_, _) => tile.BorderBrush = Brushes.White;
        tile.MouseLeave += (_, _) => tile.BorderBrush = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, _) => ShowGame(g);

        // artwork loads in the background so the page stays fast
        _found.TryGetValue(g.Id, out var exe);
        if (installed)
            Task.Run(() => { try { return GameArt.Get(g, exe); } catch { return null; } }).ContinueWith(t =>
            {
                var art = t.Result;
                if (art == null) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (art.Banner != null)
                    {
                        tile.Background = new ImageBrush(art.Banner) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
                        shade.Visibility = Visibility.Visible;
                        letters.Visibility = Visibility.Collapsed;
                    }
                    else if (art.Icon != null)
                    {
                        icon.Source = art.Icon;
                        icon.Visibility = Visibility.Visible;
                        letters.Visibility = Visibility.Collapsed;
                    }
                });
            });
        return tile;
    }

    Border AddTile()
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = "", FontFamily = UI.IconFont, FontSize = 22, Foreground = UI.B("Accent"), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = "Add any game", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(UI.Text("Pick its .exe", 11, "Dim"));
        ((TextBlock)sp.Children[2]).HorizontalAlignment = HorizontalAlignment.Center;
        var tile = new Border
        {
            Width = 218, Height = 128, CornerRadius = new CornerRadius(14), Background = UI.B("Card"), BorderBrush = UI.B("Line"),
            BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 14, 14), Cursor = Cursors.Hand, Child = sp,
        };
        tile.MouseEnter += (_, _) => tile.BorderBrush = UI.B("Accent");
        tile.MouseLeave += (_, _) => tile.BorderBrush = UI.B("Line");
        tile.MouseLeftButtonUp += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Game (*.exe)|*.exe", Title = "Pick the game's .exe file" };
            if (dlg.ShowDialog() == true)
            {
                var id = Games.AddCustom(dlg.FileName);
                _found[id] = dlg.FileName;
                ShowGrid();
                var g = Games.All.FirstOrDefault(x => x.Id == id);
                if (g != null) ShowGame(g);
            }
        };
        return tile;
    }

    // ================================================================ detail
    void ShowGame(GameDef g)
    {
        _open = g;
        string Exe() => _found.TryGetValue(g.Id, out var p) ? p : null;

        var top = new StackPanel();

        // back
        var back = UI.Btn("All games", (s, e) => ShowGrid(), icon: "");
        back.HorizontalAlignment = HorizontalAlignment.Left; back.Margin = new Thickness(0, 0, 0, 18);

        // banner
        var grad = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(g.ColorA), (Color)ColorConverter.ConvertFromString(g.ColorB), 30);
        var bannerGrid = new Grid();
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = g.Name, FontSize = 30, FontWeight = FontWeights.Black, Foreground = Brushes.White });
        var pathText = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        info.Children.Add(pathText);
        if (g.AntiCheat != null)
            info.Children.Add(new TextBlock { Text = $"Anti-cheat: {g.AntiCheat}. PC Optimizer never touches the game, its files or its memory, so these boosts are anti-cheat safe.", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        bannerGrid.Children.Add(info);

        Button launch = null;
        launch = UI.Btn("BOOST & PLAY", async (s, e) => await UI.Busy(launch, async () =>
        {
            Games.Launch(g, Exe());
            if (!Store.Settings.AutoBoost && UI.Confirm("Turn on Game Boost too?\n\nWhile any game is running, PC Optimizer will use max power and a 0.5ms timer, and close the apps you pick in Settings. Everything goes back to normal when you quit."))
            {
                Store.Settings.AutoBoost = true; Store.SaveSettings();
                (Application.Current.MainWindow as MainWindow)?.UpdateBoost(BoostMode.Status);
            }
            await Task.Delay(500);
        }), accent: true, icon: "");
        launch.Padding = new Thickness(24, 12, 24, 12);
        var btns = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        btns.Children.Add(launch);
        var locate = UI.Btn("Locate .exe", (s, e) =>
        {
            var dlg = new OpenFileDialog { Filter = "Game (*.exe)|*.exe", Title = $"Find {g.Name}'s .exe" };
            if (dlg.ShowDialog() == true)
            {
                Store.Settings.GameExePaths[g.Id] = dlg.FileName; Store.SaveSettings();
                _found[g.Id] = dlg.FileName;
                ShowGame(g);
            }
        }, icon: "");
        locate.Margin = new Thickness(0, 8, 0, 0);
        btns.Children.Add(locate);
        if (g.IsCustom)
        {
            var rm = UI.Btn("Remove game", (s, e) =>
            {
                if (UI.Confirm($"Remove {g.Name} from PC Optimizer? (Turn off its boosts first if you want them undone.)")) { Games.RemoveCustom(g.Id); ShowGrid(); }
            }, style: "DangerButton");
            rm.Margin = new Thickness(0, 8, 0, 0);
            btns.Children.Add(rm);
        }
        Grid.SetColumn(btns, 1);
        bannerGrid.Children.Add(btns);
        var banner = new Border { CornerRadius = new CornerRadius(16), Background = grad, Padding = new Thickness(28, 24, 28, 24), Child = bannerGrid, Margin = new Thickness(0, 0, 0, 20) };
        var bannerIcon = new Image { Width = 72, Height = 72, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        bannerGrid.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
        foreach (UIElement c in bannerGrid.Children) Grid.SetColumn(c, Grid.GetColumn(c) + 1);
        bannerGrid.Children.Add(bannerIcon);
        Task.Run(() => { try { return GameArt.Get(g, Exe()); } catch { return null; } }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            var art = t.Result;
            if (art?.Banner != null)
            {
                banner.Background = new ImageBrush(art.Banner) { Stretch = Stretch.UniformToFill };
                // dark gradient over the art (negative margin covers the padding) so text stays readable
                var holder = new Grid();
                banner.Child = null;
                holder.Children.Add(new Border
                {
                    Margin = new Thickness(-28, -24, -28, -24), CornerRadius = new CornerRadius(16),
                    Background = new LinearGradientBrush(Color.FromArgb(240, 8, 10, 14), Color.FromArgb(90, 8, 10, 14), 0),
                });
                holder.Children.Add(bannerGrid);
                banner.Child = holder;
            }
            else if (art?.Icon != null) { bannerIcon.Source = art.Icon; bannerIcon.Visibility = Visibility.Visible; }
        }));

        void UpdatePath()
        {
            var exe = Exe();
            pathText.Text = exe != null && g.AllowFolder && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "Installed (Microsoft Store app)"
                : exe != null && g.Id == "minecraft" && exe.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase) ? "Found Minecraft's Java: " + exe
                : exe != null ? "Found: " + exe
                : g.Id == "minecraft" ? "Not found. Start Minecraft once (any launcher) and click Rescan, or click Locate .exe and pick javaw.exe."
                : "Not found yet. If it's installed, click 'Locate .exe' (or start the game and come back).";
        }
        UpdatePath();

        top.Children.Add(back);
        top.Children.Add(banner);
        var cfg = GameConfig.For(g.Id);
        if (cfg != null) top.Children.Add(ConfigCard(g, cfg));
        top.Children.Add(UI.Section("Boosts for " + g.Name));

        // tips
        var tipsBox = new StackPanel();
        tipsBox.Children.Add(UI.Section("Best in-game settings"));
        var tips = new StackPanel();
        foreach (var t in Games.TipsFor(g)) tips.Children.Add(UI.Tip(t));
        tipsBox.Children.Add(UI.Card(tips));

        _detail = new TweakListPage(null, null, Games.TweaksFor(g, Exe), top, tipsBox);
        // TweakListPage adds an empty header for null title; strip it
        Content = _detail;

        // re-detect in the background in case the game just got installed or started
        _ = Task.Run(() => { try { return Games.FindExe(g); } catch { return null; } }).ContinueWith(t =>
        {
            if (t.Result != null && Exe() != t.Result)
            {
                _found[g.Id] = t.Result;
                Dispatcher.BeginInvoke(() => { if (_open == g) { UpdatePath(); _detail.Refresh(); } });
            }
        });
    }

    /// <summary>Card that writes low-end graphics settings into the game's own settings file (biggest FPS gain on weak PCs).</summary>
    static UIElement ConfigCard(GameDef g, GameConfig.Profile cfg)
    {
        var sp = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tt = new StackPanel();
        var tr = new StackPanel { Orientation = Orientation.Horizontal };
        tr.Children.Add(UI.Text("Max FPS game settings", 15, "Text", FontWeights.SemiBold, false));
        var badge = UI.Badge(LowEnd.IsLowEndPc ? "BIGGEST BOOST FOR YOUR PC" : "BIGGEST FPS BOOST", "#2EE59D", "#123326"); badge.Margin = new Thickness(10, 0, 0, 0); badge.VerticalAlignment = VerticalAlignment.Center;
        tr.Children.Add(badge);
        tr.Children.Add(UI.InfoDot(cfg.Note + " A backup of your settings is saved, so you can restore them any time."));
        tt.Children.Add(tr);
        var status = UI.Text("", 12, "Sub");
        tt.Children.Add(status);
        head.Children.Add(tt);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button apply = null, restore = null;
        void Update()
        {
            bool exists = GameConfig.Exists(cfg), backup = GameConfig.HasBackup(cfg);
            status.Text = !exists ? $"Start {g.Name} once so it creates its settings file, then come back."
                        : backup ? "Low-end settings applied. Restore to go back to your old settings."
                        : "Sets every graphics option for max FPS. Close the game first.";
            apply.IsEnabled = exists;
            restore.Visibility = backup ? Visibility.Visible : Visibility.Collapsed;
        }
        apply = UI.Btn("Apply", (s, e) =>
        {
            if (Games.IsRunning(g)) { UI.Info($"Close {g.Name} first. It overwrites its settings file when it exits.", "Max FPS game settings"); return; }
            try { int n = GameConfig.Apply(cfg); UI.Info(n > 0 ? $"Done! {n} settings changed. Start {g.Name} to see the difference." : "Your settings were already set for max FPS.", "Max FPS game settings"); }
            catch (Exception ex) { UI.Info("Couldn't change the settings file: " + ex.Message, "Max FPS game settings"); }
            Update();
        }, accent: true, icon: "\uE945");
        restore = UI.Btn("Restore", (s, e) =>
        {
            if (Games.IsRunning(g)) { UI.Info($"Close {g.Name} first.", "Max FPS game settings"); return; }
            try { GameConfig.Restore(cfg); } catch (Exception ex) { UI.Info("Couldn't restore: " + ex.Message, "Max FPS game settings"); }
            Update();
        });
        restore.Margin = new Thickness(8, 0, 0, 0);
        btns.Children.Add(apply); btns.Children.Add(restore);
        Grid.SetColumn(btns, 1); head.Children.Add(btns);
        sp.Children.Add(head);
        Update();
        return UI.Card(sp, new Thickness(0, 14, 0, 0));
    }
}
