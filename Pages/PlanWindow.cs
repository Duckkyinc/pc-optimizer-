using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Overdrive.Core;

namespace Overdrive.Pages;

/// <summary>"Best settings for my PC": shows the tailored plan with a reason for every choice, then applies it.</summary>
public class PlanWindow : Window
{
    readonly StackPanel _list = new();
    readonly Button _apply;
    readonly CheckBox _restore;
    readonly List<(CheckBox Box, TweakVM Vm)> _rows = new();
    PcPlan.Plan _plan;
    public bool Applied { get; private set; }

    public static async Task ShowFor(Window owner, Action after)
    {
        var w = new PlanWindow();
        if (owner != null && owner.IsVisible) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Loaded += async (_, _) => await w.Fill();
        w.ShowDialog();
        if (w.Applied) after?.Invoke();
        await Task.CompletedTask;
    }

    PlanWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Width = 640; SizeToContent = SizeToContent.Height; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true; Title = "Best settings for my PC";
        FontFamily = UI.Res<FontFamily>("Ui");

        var sp = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Margin = new Thickness(0, 0, 14, 0), Background = UI.Hex("#1A3A44"),
            Child = new TextBlock { Text = "", FontFamily = UI.IconFont, FontSize = 19, Foreground = UI.B("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var ht = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        ht.Children.Add(UI.Text("Best settings for your PC", 18, "Text", FontWeights.Bold, false));
        _summary = UI.Text("Checking your PC...", 12.5, "Sub");
        ht.Children.Add(_summary);
        Grid.SetColumn(ht, 1); head.Children.Add(ht);
        sp.Children.Add(head);

        sp.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 16, 0, 0), Focusable = false });

        var foot = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _restore = new CheckBox { Style = UI.Res<Style>("Check"), Content = UI.Text("Create a restore point first", 12.5, "Sub"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        foot.Children.Add(_restore);
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = UI.Btn("Cancel", (s, e) => Close());
        _apply = UI.Btn("Apply", async (s, e) => await Apply(), accent: true, icon: "");
        _apply.Margin = new Thickness(8, 0, 0, 0); _apply.IsEnabled = false;
        btns.Children.Add(cancel); btns.Children.Add(_apply);
        Grid.SetColumn(btns, 1); foot.Children.Add(btns);
        sp.Children.Add(foot);

        Content = new Grid
        {
            Margin = new Thickness(24),
            Children =
            {
                new Border
                {
                    Background = UI.Hex("#151A25"), BorderBrush = UI.Hex("#2A3142"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(24, 22, 24, 20), Child = sp,
                    Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
                }
            }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    readonly TextBlock _summary;

    async Task Fill()
    {
        _plan = await Task.Run(PcPlan.Build);
        _summary.Text = _plan.Summary;
        var vms = _plan.Items.Select(i => (Item: i, Vm: new TweakVM(i.Tweak))).ToList();
        foreach (var v in vms) { try { await v.Vm.Ready; } catch { } }

        var change = vms.Where(v => v.Item.Include && v.Vm.Available && !v.Vm.IsOn).ToList();
        var already = vms.Where(v => v.Item.Include && v.Vm.Available && v.Vm.IsOn).ToList();
        var skipped = vms.Where(v => !v.Item.Include && v.Vm.Available).ToList();

        _list.Children.Add(Group(change.Count > 0 ? $"WILL TURN ON ({change.Count})" : "NOTHING TO CHANGE"));
        foreach (var (item, vm) in change)
        {
            var box = new CheckBox { Style = UI.Res<Style>("Check"), IsChecked = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
            _rows.Add((box, vm));
            _list.Children.Add(Row(box, null, vm.Title + (vm.NeedsReboot ? "  ·  restart" : ""), item.Reason));
        }
        if (change.Count == 0) _list.Children.Add(UI.Text("Your PC already has the best settings for its specs.", 13, "Sub"));
        if (_plan.LowEndBoost && !LowEnd.Enabled)
        {
            var box = new CheckBox { Style = UI.Res<Style>("Check"), IsChecked = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0), Tag = "lowend" };
            _lowEndBox = box;
            _list.Children.Add(Row(box, null, "Low-end PC boost (during games)", $"Detected {LowEnd.Why()}. Pauses background Windows work and frees RAM while you play."));
        }
        if (skipped.Count > 0)
        {
            var sk = new StackPanel();
            foreach (var (item, vm) in skipped) sk.Children.Add(Row(null, "", vm.Title, item.Reason));
            _list.Children.Add(UI.Collapsible($"Skipped for your PC ({skipped.Count})", sk, ""));
        }
        if (already.Count > 0)
        {
            var al = new StackPanel();
            foreach (var (item, vm) in already) al.Children.Add(Row(null, "", vm.Title, item.Reason));
            _list.Children.Add(UI.Collapsible($"Already set ({already.Count})", al, ""));
        }
        _apply.IsEnabled = change.Count > 0 || _lowEndBox != null;
    }
    CheckBox _lowEndBox;

    static TextBlock Group(string t) { var l = UI.Label(t); l.Margin = new Thickness(0, 4, 0, 8); return l; }

    static UIElement Row(CheckBox box, string glyph, string title, string reason)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        if (box != null) g.Children.Add(box);
        else g.Children.Add(new TextBlock { Text = glyph, FontFamily = UI.IconFont, FontSize = 12, Foreground = UI.B("Sub"), Margin = new Thickness(2, 3, 12, 0) });
        var t = new StackPanel();
        t.Children.Add(UI.Text(title, 13.5, "Text", FontWeights.SemiBold));
        t.Children.Add(UI.Text(reason, 12, "Sub"));
        Grid.SetColumn(t, 1); g.Children.Add(t);
        return g;
    }

    async Task Apply()
    {
        var todo = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Vm).ToList();
        await UI.Busy(_apply, async () =>
        {
            if (_restore.IsChecked == true && todo.Count > 0)
            {
                try { await Task.Run(() => RestorePoint.Create("PC Optimizer - before Best settings")); }
                catch (Exception ex) { if (!UI.Confirm("Couldn't create a restore point:\n" + ex.Message + "\n\nContinue anyway? PC Optimizer still saves a backup of every change.")) return; }
            }
            foreach (var vm in todo) { try { await vm.SetAsync(true, quiet: true); } catch { } }
            if (_lowEndBox?.IsChecked == true) { Store.Settings.LowEndMode = true; Store.SaveSettings(); }
            Applied = true;
            var failed = todo.Where(v => !v.IsOn).ToList();
            bool reboot = todo.Any(v => v.IsOn && v.NeedsReboot);
            Close();
            UI.Info($"Done! {todo.Count - failed.Count} settings applied for your PC." +
                    (failed.Count > 0 ? "\n\nThese didn't work on this PC:\n" + string.Join("\n", failed.Select(f => "  - " + f.Title)) : "") +
                    (reboot ? "\n\nRestart your PC to finish some of them." : "") +
                    "\n\nTip: run the Benchmark before and after to see the difference.", "Best settings for my PC");
        });
    }
}
