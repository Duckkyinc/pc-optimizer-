using System.Windows;
using System.Windows.Controls;
using Overdrive.Core;

namespace Overdrive.Pages;

/// <summary>Shows NVIDIA tools on NVIDIA PCs and AMD tools on AMD PCs, never both.</summary>
public class GpuPage : TweakListPage
{
    public GpuPage() : base(
        SysInfo.ShowAmd ? "AMD Radeon" : "NVIDIA",
        SysInfo.ShowAmd
            ? "Radeon driver settings for max FPS and low input delay. All undoable."
            : "Driver settings for max FPS and low input delay. All undoable.",
        SysInfo.ShowAmd ? Catalog.Amd : Catalog.Nvidia,
        top: BuildTop(),
        bottom: SysInfo.ShowAmd ? AmdChecklist() : NvidiaExtras())
    { }

    public override void Refresh() { Nvapi.Invalidate(); base.Refresh(); }

    static UIElement BuildTop()
    {
        var gpu = SysInfo.PrimaryGpu;
        var amd = SysInfo.ShowAmd;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        info.Children.Add(UI.Label("YOUR GRAPHICS CARD"));
        info.Children.Add(UI.Text(gpu?.Name ?? "Unknown GPU", 20, "Text", FontWeights.Bold));
        var ver = amd ? gpu?.DriverVersion : gpu?.NvidiaDriverVersion;
        info.Children.Add(UI.Text($"Driver {ver ?? "unknown"}", 13, "Sub"));
        if (!amd)
        {
            var warn = UI.Text("PC Optimizer can't talk to the NVIDIA driver. Install or update the NVIDIA driver, then reopen PC Optimizer.", 12, "Warn");
            warn.Margin = new Thickness(0, 8, 0, 0);
            warn.Visibility = Visibility.Collapsed;
            info.Children.Add(warn);
            Task.Run(() => Nvapi.Available).ContinueWith(t => warn.Dispatcher.BeginInvoke(() => warn.Visibility = t.Result ? Visibility.Collapsed : Visibility.Visible));
        }
        g.Children.Add(info);

        var buttons = amd ? UI.Row(UI.Btn("Open AMD Software", (s, e) => OpenPanel(true), icon: "\uE8A7")) : UI.Row(
            UI.Btn("Apply best NVIDIA settings", async (s, e) => await ApplyBestNvidia((Button)s), accent: true, icon: "\uE945"),
            UI.Btn("Open NVIDIA App", (s, e) => OpenNvidiaApp(), icon: "\uE8A7"),
            UI.Btn("Open NVIDIA Control Panel", (s, e) => OpenPanel(false), icon: "\uE8A7"));
        buttons.Children.Add(
            UI.Btn("Get latest driver", (s, e) => Shell.Open(amd ? "https://www.amd.com/en/support/download/drivers.html" : "https://www.nvidia.com/en-us/drivers/"), accent: amd, icon: "\uE896"));
        ((FrameworkElement)buttons.Children[^1]).Margin = new Thickness(0);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(buttons, 1);
        g.Children.Add(buttons);

        var card = UI.Card(g, background: UI.Hex(amd ? "#1A0F12" : "#0F1A0C"));
        card.BorderBrush = UI.B(amd ? "AmdRed" : "NvGreen");
        card.Margin = new Thickness(0, 0, 0, 20);
        return card;
    }

    static async Task ApplyBestNvidia(Button b)
    {
        var vms = await Task.Run(() => Catalog.Nvidia.Where(t => t.Recommended).Select(t => new TweakVM(t)).ToList());
        await Task.WhenAll(vms.Select(v => v.Ready));
        if (vms.Count == 0 || vms.All(v => !v.Available)) { UI.Info("Couldn't reach the NVIDIA driver. Install or update it, then try again."); return; }
        var todo = vms.Where(v => v.Available && !v.IsOn).ToList();
        if (todo.Count == 0) { UI.Info("Done! Your NVIDIA settings are already set to the best values."); return; }
        if (!UI.Confirm("Apply the best NVIDIA settings for FPS and input delay?\n\n" + string.Join("\n", todo.Select(t => "  • " + t.Title)) +
                        "\n\nThese are the same settings as the NVIDIA App's Graphics tab / NVIDIA Control Panel. You can undo them any time.", "Best NVIDIA settings")) return;
        await UI.Busy(b, async () =>
        {
            foreach (var t in todo) await t.SetAsync(true, quiet: true);
            (Application.Current.MainWindow as MainWindow)?.RefreshAll();
            var failed = todo.Where(t => !t.IsOn).ToList();
            UI.Info("Done! Best NVIDIA settings applied." + (failed.Count > 0 ? "\n\nThese didn't apply:\n" + string.Join("\n", failed.Select(f => "  • " + f.Title)) : "") +
                    "\n\nRestart your game to use them.");
        });
    }

    internal static void OpenNvidiaApp()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var path = new[]
        {
            System.IO.Path.Combine(pf, "NVIDIA Corporation", "NVIDIA App", "CEF", "NVIDIA App.exe"),
            System.IO.Path.Combine(pf, "NVIDIA Corporation", "NVIDIA app", "CEF", "NVIDIA app.exe"),
            System.IO.Path.Combine(pf, "NVIDIA Corporation", "NVIDIA GeForce Experience", "NVIDIA GeForce Experience.exe"),
        }.FirstOrDefault(System.IO.File.Exists);
        try
        {
            if (path != null) Shell.Open(path);
            else if (UI.Confirm("The NVIDIA App isn't installed. It's NVIDIA's official app for driver updates, game settings, Instant Replay and the performance overlay.\n\nOpen the download page?"))
                Shell.Open("https://www.nvidia.com/en-us/software/nvidia-app/");
        }
        catch { UI.Info("Couldn't open the NVIDIA App. Search for 'NVIDIA App' in the Start menu."); }
    }

    static void OpenPanel(bool amd)
    {
        try
        {
            if (amd)
            {
                var path = new[]
                {
                    @"C:\Program Files\AMD\CNext\CNext\RadeonSoftware.exe",
                    @"C:\Program Files\AMD\CNext\CNext\AMDRSServ.exe",
                }.FirstOrDefault(System.IO.File.Exists);
                Shell.Open(path ?? "ms-settings:display-advancedgraphics");
            }
            else Shell.Open(@"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel");
        }
        catch { UI.Info(amd ? "Couldn't find AMD Software. Right-click your desktop and choose 'AMD Software: Adrenalin Edition'." : "Couldn't open NVIDIA Control Panel. Right-click your desktop and choose 'NVIDIA Control Panel'."); }
    }

    static UIElement NvidiaExtras()
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Section("More tips"));
        var app = new StackPanel();
        void A(string t) => app.Children.Add(UI.Tip(t, "\uE73E", "Good"));
        A("Settings > Features > Instant Replay: OFF unless you clip a lot. It records your gameplay in the background all the time and costs a few FPS.");
        A("Game Filters / Photo Mode: OFF. Filters are post-processing on top of the game and can cost 5-15% FPS.");
        A("Overlay: if you don't use it, turn off 'In-game overlay'. Every overlay adds a little load and input lag.");
        A("Graphics > Program settings: don't use 'Optimize' (one-click game settings) for competitive games. It aims for looks, not max FPS.");
        A("Drivers: Game Ready driver, 'Express install'. Update when a new driver lists your game.");
        sp.Children.Add(UI.Collapsible("NVIDIA App settings to change", app, "\uE73E"));
        var tips = new StackPanel();
        tips.Children.Add(UI.Tip("Turn on NVIDIA Reflex (On + Boost) in every game that has it. It lowers input lag more than any driver setting."));
        tips.Children.Add(UI.Tip("DLSS 'Quality' or 'Balanced' gives you a big FPS gain on RTX cards with very little visual loss. Frame Generation adds more FPS but also a little input lag, so skip it in competitive shooters."));
        tips.Children.Add(UI.Tip("G-SYNC monitor? Turn G-SYNC on, V-Sync ON in the control panel, and cap your FPS 3 below your refresh rate (e.g. 141 on 144Hz). That's the smoothest, lowest-lag setup there is."));
        tips.Children.Add(UI.Tip("Keep your driver updated. NVIDIA's 'Game Ready' drivers often fix performance in new games."));
        sp.Children.Add(UI.Collapsible("In-game tips (Reflex, DLSS, G-SYNC)", tips, "\uE82F"));
        return sp;
    }

    static UIElement AmdChecklist()
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.SectionInfo("Best AMD Software settings", "The switches above are set by PC Optimizer. These last few only exist inside AMD Software › Gaming › Graphics."));
        var tips = new StackPanel();
        void Add(string s) => tips.Children.Add(UI.Tip(s, "\uE73E", "Good"));
        void Head(string h) { var t = UI.Text(h, 12, "Dim", FontWeights.Bold); t.Margin = new Thickness(0, tips.Children.Count == 0 ? 0 : 10, 0, 8); tips.Children.Add(t); }
        Head("INPUT DELAY");
        Add("Radeon Anti-Lag: ON. AMD's input lag reducer. Games that support Anti-Lag 2 (CS2, Fortnite and others) have it in their own settings. Use that one there.");
        Add("Radeon Boost: OFF for competitive shooters. It lowers your resolution when you move the mouse, which looks blurry.");
        Add("HYPR-RX: OFF for competitive games (it turns on Boost). Use your own settings instead.");
        Add("Wait for Vertical Refresh: 'Always off' (or 'Off, unless application specifies').");
        Add("Enhanced Sync: OFF. It can cause stutter on some systems.");
        Add("Frame Rate Target Control: OFF, or set it 3 below your refresh rate if you use FreeSync.");
        Head("FPS");
        Add("Radeon Chill: OFF. It caps FPS to save power.");
        Add("Surface Format Optimization: ON.");
        Add("FSR in-game 'Quality' = a big FPS boost. Radeon Super Resolution (RSR) and AFMF (Fluid Motion Frames) add FPS but also lag, so keep them for single-player games.");
        Add("Morphological Anti-Aliasing: OFF. 10-bit Pixel Format: OFF.");
        Head("DISPLAY + SYSTEM");
        Add("AMD FreeSync: ON (if your monitor supports it), and cap FPS 3 below your refresh rate (e.g. 141 on 144Hz).");
        Add("Smart Access Memory: turn it on in your motherboard BIOS (it's called 'Resizable BAR' / 'Above 4G decoding'). Gives free FPS in many games on RX 6000+ cards with Ryzen 5000+ or Intel 12th gen+ CPUs.");
        Add("Radeon Image Sharpening: optional. Use it if you play at a lower resolution or with FSR; it costs almost no FPS.");
        sp.Children.Add(UI.Collapsible("Show the checklist (about 1 minute)", tips, "\uE73E", open: true));
        return sp;
    }
}
