using System.Windows;
using WinForms = System.Windows.Forms;

namespace Overdrive.Core;

/// <summary>System tray icon: double-click to open, right-click for quick toggles.</summary>
public static class Tray
{
    static WinForms.NotifyIcon _icon;
    public static bool Exiting { get; private set; }
    static int _hiddenTipCount;

    public static void Create(MainWindow main)
    {
        System.Drawing.Icon ico;
        try
        {
            using var s = Application.GetResourceStream(new Uri("pack://application:,,,/overdrive.ico")).Stream;
            ico = new System.Drawing.Icon(s, new System.Drawing.Size(32, 32));
        }
        catch { ico = System.Drawing.SystemIcons.Application; }

        _icon = new WinForms.NotifyIcon { Icon = ico, Text = "PC Optimizer", Visible = true };
        _icon.DoubleClick += (_, _) => ShowMain(main);
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right) Pages.TrayMenu.Toggle(main);
            else if (e.Button == WinForms.MouseButtons.Left) ShowMain(main);
        };
        BoostMode.StatusChanged += s => { if (_icon != null) _icon.Text = Trim("PC Optimizer - " + (BoostMode.ActiveGame != null ? s : Store.Settings.AutoBoost ? "Game Boost ready" : "idle")); };
    }

    static string Trim(string s) => s.Length > 63 ? s[..63] : s; // tray tooltip limit

    /// <summary>Small Windows notification from the tray icon. Safe to call from any thread.</summary>
    public static void Notify(string title, string text)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => _icon?.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.Info));
    }

    public static void ShowMain(Window w)
    {
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true; w.Topmost = false; // bring to front
    }

    public static void HiddenToTray()
    {
        if (_icon == null) return;
        if (_hiddenTipCount++ > 0 && Store.Settings.TrayTipShown) return; // remind once per launch
        Store.Settings.TrayTipShown = true; Store.SaveSettings();
        _icon.ShowBalloonTip(4000, "PC Optimizer is still running",
            "It's in the system tray (^ next to the clock) so Game Boost keeps working. Right-click the icon for quick toggles, or choose Quit to close it completely.", WinForms.ToolTipIcon.Info);
    }

    public static void Exit()
    {
        Exiting = true;
        Dispose();
        Application.Current.Shutdown();
    }

    public static void Dispose()
    {
        if (_icon == null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}
