using System.Windows;
using System.Windows.Threading;
using Overdrive.Core;

namespace Overdrive;

public partial class App : Application
{
    static Mutex _single;
    static long _updatePrompted;
    static EventWaitHandle _showSignal, _exitSignal;

    /// <summary>Unique per build, so a new download always replaces an old running copy.</summary>
    public static readonly string BuildId =
        System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
    public static string VersionText => BuildId.Split('+')[0];

    static string RunningFile => System.IO.Path.Combine(Store.Dir, "running.txt");

    static string ReadRunningBuild()
    {
        try { return System.IO.File.ReadAllLines(RunningFile).FirstOrDefault(); } catch { return null; }
    }

    static void WriteRunningBuild()
    {
        try { System.IO.Directory.CreateDirectory(Store.Dir); System.IO.File.WriteAllLines(RunningFile, new[] { BuildId, Environment.ProcessId.ToString() }); } catch { }
    }

    /// <summary>Close every other running copy (used before replacing the installed exe).</summary>
    static void CloseOtherCopies()
    {
        try { using var exit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\PCOptimizer.Exit"); exit.Set(); } catch { }
        // wait only as long as needed (up to 1.5 s) for the old copy to close by itself
        for (int i = 0; i < 15; i++)
        {
            bool others = false;
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try { if (p.Id != Environment.ProcessId && SelfInstall.IsOurProcess(p.ProcessName)) others = true; } catch { }
                finally { p.Dispose(); }
            }
            if (!others) break;
            Thread.Sleep(100);
        }
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId || !SelfInstall.IsOurProcess(p.ProcessName)) continue;
                if (!p.WaitForExit(2500)) { p.Kill(); p.WaitForExit(3000); }
            }
            catch { }
        }
    }

    /// <summary>Ask the old copy to quit; if it's a build from before this existed, close it ourselves.</summary>
    void TakeOver()
    {
        _exitSignal.Set();
        bool got = false;
        try { got = _single.WaitOne(4000); } catch (AbandonedMutexException) { got = true; }
        if (!got)
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (p.Id == Environment.ProcessId || !SelfInstall.IsOurProcess(p.ProcessName)) continue;
                    p.Kill(); p.WaitForExit(3000);
                }
                catch { }
            }
            try { got = _single.WaitOne(3000); } catch (AbandonedMutexException) { got = true; }
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Child process used by Auto-tune: run one benchmark, write the result, quit.
        if (e.Args.Contains("--bench"))
        {
            try { Store.Load(); } catch { }
            try { AutoTune.ChildMain(e.Args); } catch (Exception ex) { try { Store.Log("Bench child: " + ex); } catch { } }
            Environment.Exit(0);
            return;
        }

        // Opened from Downloads (or anywhere)? Install/update into the one fixed place and launch that instead.
        if (!e.Args.Contains("--portable") && SelfInstall.HandleStartup(BuildId, CloseOtherCopies))
        {
            Shutdown();
            return;
        }

        // One copy at a time. If an OLDER build is already running (e.g. hidden in the tray), it gets replaced by this one.
        _single = new Mutex(true, @"Global\PCOptimizer.SingleInstance", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\PCOptimizer.Show");
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\PCOptimizer.Exit");
        if (!first)
        {
            var running = ReadRunningBuild();
            if (running == BuildId)
            {
                _showSignal.Set(); // same version: just bring the open window to the front
                Shutdown();
                return;
            }
            TakeOver();
        }
        WriteRunningBuild();

        DispatcherUnhandledException += OnUiError;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Store.Log("FATAL: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { Store.Log("Task error: " + a.Exception); a.SetObserved(); };

        Store.Load();
        Store.Log("---- PC Optimizer started ----");
        SysInfo.Load();
        System.Threading.Tasks.Task.Run(() => { try { WifiScan.Resume(); } catch { } }); // never leave Wi-Fi scanning off after a crash

        var w = new MainWindow();
        MainWindow = w;
        w.Show();
        Tray.Create(w);
        if (e.Args.Contains("--updated"))
            w.Dispatcher.BeginInvoke(() => Overdrive.Pages.Dialog.Show($"PC Optimizer is updated to version {VersionText}.\n\nFrom now on, open it with the 'PC Optimizer' shortcut on your desktop or in the Start menu. To update again later, just open the new download once.", "Updated", MessageBoxButton.OK, MessageBoxImage.Information),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        new Thread(() =>
        {
            while (true)
            {
                try { _showSignal.WaitOne(); } catch { return; }
                Dispatcher.BeginInvoke(() => Tray.ShowMain(w));
            }
        }) { IsBackground = true, Name = "single-instance" }.Start();
        new Thread(() =>
        {
            try { _exitSignal.WaitOne(); } catch { return; }
            Store.Log("A newer version was opened. Closing this one.");
            Dispatcher.BeginInvoke(() => Tray.Exit());
        }) { IsBackground = true, Name = "update-handoff" }.Start();
        Task.Run(TrafficShaper.Cleanup);
        BoostMode.Start();
        Pages.LiveMonitor.Start();

        // In-app updates from GitHub Releases
        Updater.Found += () => Dispatcher.BeginInvoke(async () =>
        {
            var r = Updater.Available; if (r == null || _updatePrompted == r.Id) return;
            _updatePrompted = r.Id;
            Tray.Notify("Update available", $"PC Optimizer {Updater.Label(r)} is ready to install.");
            var ans = Pages.Dialog.Show($"A new PC Optimizer update is available ({Updater.Label(r)}). You have {VersionText}.\n\nUpdate now? It downloads, installs and restarts the app in about a minute.", "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes) return;
            Tray.Notify("Updating PC Optimizer", "Downloading the new version...");
            try { await Updater.InstallAsync(r, null); }
            catch (Exception ex) { Pages.Dialog.Show("Update failed: " + ex.Message + "\n\nYou can try again in Settings › Updates.", "Update", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
        Updater.StartAutoCheck();
    }

    void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Store.Log("UI error: " + e.Exception);
        Overdrive.Pages.Dialog.Show("Something went wrong, but PC Optimizer kept running.\n\n" + e.Exception.Message +
                        $"\n\nDetails were saved to:\n{Store.LogFile}", "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { }
        try { BoostMode.End(); } catch { }
        try { WifiScan.Resume(); } catch { }
        try { Pages.LiveMonitor.Shutdown(); } catch { }
        try { FpsMonitor.Stop(); } catch { }
        try { HwMonitor.StopNvSmi(); } catch { }
        try { Tray.Dispose(); } catch { }
        base.OnExit(e);
    }
}
