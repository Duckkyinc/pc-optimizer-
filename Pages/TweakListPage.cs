using System.Windows;
using System.Windows.Controls;
using Overdrive.Core;

namespace Overdrive.Pages;

public class TweakListPage : UserControl, IRefreshable
{
    protected readonly List<TweakVM> Items;
    protected readonly StackPanel Body = new();
    readonly TextBlock _count = UI.Text("", 13, "Sub");
    ItemsControl _list;

    public TweakListPage(string title, string subtitle, IEnumerable<Tweak> tweaks, UIElement top = null, UIElement bottom = null)
    {
        Items = tweaks.Select(t => new TweakVM(t)).ToList();
        TweakVM.AnyChanged += () => Dispatcher.BeginInvoke(UpdateCount);

        if (title != null) Body.Children.Add(UI.Header(title, subtitle));
        if (top != null) Body.Children.Add(top);

        if (Items.Count > 0)
        {
            // toolbar: count on the left; search, view switch, undo, recommended on the right
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = false };
            _count.VerticalAlignment = VerticalAlignment.Center;
            bar.Children.Add(_count);

            var rec = UI.Btn("Turn on recommended", async (s, e) => await SetAll(true), accent: true, icon: "");
            rec.Margin = new Thickness(0);
            var undo = UI.Btn("Undo all", async (s, e) => await SetAll(false), icon: "");
            var view = UI.ViewSwitch();

            var search = new Grid { Width = 200, Height = 38, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox { Style = UI.Res<Style>("SearchInput") };
            var ph = new TextBlock { Text = "Search", FontSize = 13, Foreground = UI.B("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(38, 0, 0, 0), IsHitTestVisible = false };
            search.Children.Add(box);
            search.Children.Add(new TextBlock { Text = "", FontFamily = UI.IconFont, FontSize = 13, Foreground = UI.B("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), IsHitTestVisible = false });
            search.Children.Add(ph);
            box.TextChanged += (_, _) =>
            {
                var q = box.Text.Trim();
                ph.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _list.ItemsSource = q.Length == 0 ? Items
                    : Items.Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            };

            foreach (var el in new FrameworkElement[] { rec, undo, view, search }) { DockPanel.SetDock(el, Dock.Right); el.VerticalAlignment = VerticalAlignment.Center; bar.Children.Add(el); }
            Body.Children.Add(bar);

            _list = UI.TweakList(Items); // follows the Grid / List switch
            Body.Children.Add(_list);
        }
        if (bottom != null) Body.Children.Add(bottom);
        Content = UI.PageScroll(Body);
        UpdateCount();
        _ = Task.WhenAll(Items.Select(i => i.Ready)).ContinueWith(_ => Dispatcher.BeginInvoke(UpdateCount));
    }

    async Task SetAll(bool on)
    {
        var targets = on ? Items.Where(i => i.Recommended && !i.Advanced && i.Available && !i.IsOn)
                         : Items.Where(i => i.IsOn);
        foreach (var t in targets.ToList()) await t.SetAsync(on, quiet: true);
        var failed = Items.Where(i => i.Status?.StartsWith("Failed") == true).ToList();
        if (failed.Count > 0)
            UI.Info("Some switches couldn't be changed:\n\n" + string.Join("\n", failed.Select(f => $"• {f.Title}: {f.Status[8..]}")));
        if (on && Items.Any(i => i.IsOn && i.NeedsReboot))
            UI.Info("Done! Some changes finish after you restart your PC.");
    }

    void UpdateCount()
    {
        var avail = Items.Count(i => i.Available);
        _count.Text = $"{Items.Count(i => i.IsOn)} of {avail} turned on";
    }

    public virtual async void Refresh()
    {
        await Task.WhenAll(Items.Select(i => i.RefreshAsync()));
        UpdateCount();
    }
}
