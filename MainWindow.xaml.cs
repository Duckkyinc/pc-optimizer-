using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Overdrive.Core;
using Overdrive.Pages;

namespace Overdrive;

public partial class MainWindow : Window
{
    readonly Dictionary<string, Func<UserControl>> _factories;
    readonly Dictionary<string, UserControl> _pages = new();
    readonly Dictionary<string, DateTime> _refreshed = new();

    public MainWindow()
    {
        _factories = new()
        {
            ["NavDashboard"] = () => new DashboardPage(this),
            ["NavPerf"] = () => new PerformancePage(),
            ["NavBench"] = () => new BenchmarkPage(),
            ["NavLab"] = () => new LabPage(),
            ["NavHealth"] = () => new HealthPage(),
            ["NavApps"] = () => new AppsPage(),
            ["NavGames"] = () => new GamesPage(),
            ["NavAim"] = () => new AimPage(),
            ["NavWindows"] = () => new TweakListPage("Windows", "Windows settings that boost FPS and cut input delay. All undoable.", Catalog.Windows),
            ["NavGpu"] = () => new GpuPage(),
            ["NavNetwork"] = () => new NetworkPage(),
            ["NavCleanup"] = () => new CleanupPage(),
            ["NavSettings"] = () => new SettingsPage(this),
        };

        InitializeComponent();
        Title = $"PC Optimizer {App.VersionText}";

        // GPU tab only shows the brand you actually have
        switch (SysInfo.GpuVendor)
        {
            case "NVIDIA": NavGpu.Content = "NVIDIA"; break;
            case "AMD": NavGpu.Content = "AMD Radeon"; break;
            default: NavGpu.Visibility = Visibility.Collapsed; break;
        }

        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
        };

        SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (!RegisterHotKey(h, 0x4F44, 0x0002 | 0x0004 | 0x4000, 0x4F)) // Ctrl+Shift+O, no-repeat
                Store.Log("Overlay hotkey Ctrl+Shift+O is in use by another app");
            System.Windows.Interop.HwndSource.FromHwnd(h)?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == 0x0312 && wp.ToInt32() == 0x4F44) { LiveMonitor.SetOverlay(!Store.Settings.FpsOverlay); handled = true; }
                return IntPtr.Zero;
            });
        };

        BoostMode.StatusChanged += UpdateBoost;
        UpdateBoost(BoostMode.Status);
        Show("NavDashboard");

        // build every other tab quietly in the background, one per idle moment, so the first click is instant
        Loaded += (_, _) =>
        {
            var queue = new Queue<string>(_factories.Keys.Where(k => k != "NavDashboard" && (k != "NavGpu" || NavGpu.Visibility == Visibility.Visible)));
            var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(350) };
            t.Tick += (_, _) =>
            {
                if (queue.Count == 0) { t.Stop(); return; }
                var k = queue.Dequeue();
                if (!_pages.ContainsKey(k))
                {
                    try { _pages[k] = _factories[k](); _refreshed[k] = DateTime.Now; }
                    catch (Exception ex) { Store.Log($"Prebuild {k} failed: {ex.Message}"); }
                }
            };
            t.Start();
        };
    }

    public void UpdateBoost(string status)
    {
        if (!Store.Settings.AutoBoost)
        {
            BoostText.Text = "Off. Turn it on in Settings.";
            BoostDot.Fill = (Brush)FindResource("Dim");
        }
        else if (BoostMode.ActiveGame != null)
        {
            BoostText.Text = status;
            BoostDot.Fill = (Brush)FindResource("Good");
        }
        else
        {
            BoostText.Text = "Waiting for a game to start...";
            BoostDot.Fill = (Brush)FindResource("Accent");
        }
    }

    public void Navigate(string navName)
    {
        if (FindName(navName) is RadioButton rb) rb.IsChecked = true;
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_factories == null || Page == null) return;
        Show(((FrameworkElement)sender).Name);
    }

    void Show(string key)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { ShowInner(key); }
        finally { if (sw.ElapsedMilliseconds > 150) Store.Log($"Tab {key} took {sw.ElapsedMilliseconds} ms to open"); }
    }

    void ShowInner(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = _factories[key]();
            _pages[key] = page;
            _refreshed[key] = DateTime.Now;
        }
        else if (page is IRefreshable r && (!_refreshed.TryGetValue(key, out var at) || (DateTime.Now - at).TotalSeconds > 20))
        {
            _refreshed[key] = DateTime.Now;
            // refresh after the tab is on screen, so switching never waits on it
            Dispatcher.BeginInvoke(r.Refresh, System.Windows.Threading.DispatcherPriority.Background);
        }
        Page.Content = page;
    }

    /// <summary>Re-check every switch on every page (after Apply all / Revert all).</summary>
    public void RefreshAll()
    {
        foreach (var p in _pages.Values.OfType<IRefreshable>()) p.Refresh();
        foreach (var k in _refreshed.Keys.ToList()) _refreshed[k] = DateTime.Now;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!Tray.Exiting && Store.Settings.CloseToTray)
        {
            e.Cancel = true;      // keep running in the tray so Game Boost still works
            Hide();
            Tray.HiddenToTray();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!Tray.Exiting) Tray.Exit(); // closing for real (tray mode off): quit the app
    }
}
