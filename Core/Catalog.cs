using Microsoft.Win32;
using static Overdrive.Core.RegSet;

namespace Overdrive.Core;

public static class Catalog
{
    const string MM = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    const string MMGames = MM + @"\Tasks\Games";
    const string TcpIf = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    public static List<Tweak> Windows { get; } = new();
    public static List<Tweak> Network { get; } = new();
    public static List<Tweak> Nvidia { get; } = new();
    public static List<Tweak> Amd { get; } = new();
    public static IEnumerable<Tweak> All => Windows.Concat(Network).Concat(Nvidia).Concat(Amd);

    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Driver registry keys (0000, 0001...) belonging to AMD Radeon adapters.</summary>
    public static IEnumerable<string> AmdAdapterKeys()
    {
        foreach (var sub in Reg.SubKeys(RegistryHive.LocalMachine, DisplayClass))
        {
            if (!int.TryParse(sub, out _)) continue;
            var path = $@"{DisplayClass}\{sub}";
            string prov, desc;
            try
            {
                prov = Reg.Get(RegistryHive.LocalMachine, path, "ProviderName")?.ToString() ?? "";
                desc = Reg.Get(RegistryHive.LocalMachine, path, "DriverDesc")?.ToString() ?? "";
            }
            catch { continue; } // some subkeys are access-restricted
            if (prov.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    static Catalog()
    {
        // ------------------------------------------------------------ WINDOWS
        Windows.Add(PowerPlan.CreateTweak());

        Windows.Add(Tweak.Registry("win.gamemode", "Game Mode",
            "Tells Windows to prioritize the game you're playing and pause Windows Update installs and driver popups while you play.",
            Impact.Low, new[]
            {
                HKCU(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, def: 1),
                HKCU(@"Software\Microsoft\GameBar", "AllowAutoGameMode", 1),
            }, recommended: true));

        Windows.Add(Tweak.Registry("win.gamedvr", "Turn off background recording (Game DVR)",
            "Xbox Game Bar can quietly record your gameplay in the background so you can 'save the last 30 seconds'. That recording costs FPS. This turns it off. You can still use other recorders like ShadowPlay/OBS.",
            Impact.Medium, new[]
            {
                HKCU(@"System\GameConfigStore", "GameDVR_Enabled", 0, def: 1),
                HKCU(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
                HKCU(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0),
            }, recommended: true));

        Windows.Add(Tweak.Registry("win.hags", "Hardware-accelerated GPU scheduling",
            "Lets your graphics card manage its own memory instead of Windows. Lowers latency a bit on modern GPUs, and NVIDIA DLSS Frame Generation requires it.",
            Impact.Situational, new[]
            {
                HKLM(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2),
            }, reboot: true, warning: "Needs an NVIDIA GTX 10-series / AMD RX 5000 or newer. A few older games stutter more with it on; just turn it back off if so."));

        Windows.Add(Tweak.Registry("win.mmcss", "Give games top multimedia priority",
            "Tells Windows' multimedia scheduler that games get the highest CPU and GPU priority, and reserves less CPU for background tasks.",
            Impact.Low, new[]
            {
                HKLM(MM, "SystemResponsiveness", 10, def: 20),
                HKLM(MMGames, "GPU Priority", 8),
                HKLM(MMGames, "Priority", 6),
                HKLM(MMGames, "Scheduling Category", "High", RegistryValueKind.String),
                HKLM(MMGames, "SFIO Priority", "High", RegistryValueKind.String),
            }, recommended: true));

        Windows.Add(Tweak.Registry("win.foreground", "Favor the game window",
            "Makes Windows give the app you're focused on (your game) longer, more frequent CPU time slices than background apps.",
            Impact.Low, new[]
            {
                HKLM(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26, def: 2),
            }, recommended: true));

        Windows.Add(Tweak.Registry("win.powerthrottle", "Turn off power throttling",
            "Stops Windows from slowing down apps it thinks are in the background. Helps when a game, Discord or OBS gets throttled by mistake, especially on laptops.",
            Impact.Low, new[]
            {
                HKLM(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1),
            }, reboot: true, warning: "Laptop battery won't last as long."));

        Windows.Add(Tweak.Registry("win.mpo", "Fix random black screens (turn off MPO)",
            "Multi-Plane Overlay is a Windows display feature that NVIDIA says can cause monitors to go black for a moment, flicker or stutter, especially with two or more screens. This is NVIDIA's own fix. Only use it if your screens randomly go black or flicker.",
            Impact.Situational,
            () => new[] { RegSet.HKLM(@"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, def: null) },
            reboot: true,
            warning: "Only for random black screens/flicker. Can slightly raise desktop GPU use. Newer Windows 11 builds may ignore it."));

        Windows.Add(Tweak.Registry("win.mouseaccel", "Turn off mouse acceleration",
            "Removes 'Enhance pointer precision'. Your crosshair then moves the same distance every time you move the mouse the same distance, whatever the speed. Every pro player does this. It doesn't add FPS, but it makes your aim more consistent.",
            Impact.Quality, new[]
            {
                HKCU(@"Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String, "1"),
                HKCU(@"Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String, "6"),
                HKCU(@"Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String, "10"),
            }, recommended: true, after: ApplyMouseLive));

        Windows.Add(Tweak.Registry("win.bgapps", "Stop Store apps running in the background",
            "Stops Microsoft Store apps (Weather, News, Phone Link and so on) from waking up in the background and using CPU, RAM and network.",
            Impact.Low, new[]
            {
                HKCU(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1),
                HKCU(@"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0),
            }, warning: "Store apps won't show live notifications until you open them."));

        Windows.Add(Tweak.Registry("win.transparency", "Turn off transparency effects",
            "Turns off the see-through blur in the taskbar, Start menu and windows. A small help on weak GPUs and laptops.",
            Impact.Low, new[]
            {
                HKCU(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, def: 1),
            }));

        Windows.Add(Tweak.Registry("win.visualfx", "Performance visual effects",
            "Sets Windows animations and visual effects to 'Adjust for best performance'. Makes Windows itself feel snappier on older PCs.",
            Impact.Low, new[]
            {
                HKCU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2, def: 0),
            }, warning: "Windows will look plainer. Sign out and back in to see the full change."));

        Windows.Add(Tweak.Registry("win.vbs", "Turn off Memory Integrity (VBS / HVCI)",
            "Memory Integrity is a Windows 11 security feature that runs part of Windows inside a virtual machine. Microsoft itself says it can lower gaming performance, and tests show around 5-10% FPS in some games. This is the biggest single FPS tweak on many Windows 11 PCs.",
            Impact.High, new[]
            {
                HKLM(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0),
                HKLM(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0),
            }, reboot: true, advanced: true,
            warning: "SECURITY TRADE-OFF: this makes your PC easier for malware to attack. Some anti-cheats (like FACEIT) or work/school PCs may require it on. Only do this if you understand that."));


        Windows.Add(Tweak.Registry("win.timerres", "Allow 0.5ms timer resolution for games",
            "Windows 11 normally keeps each app's timer requests to itself. This lets Game Boost raise the system timer to 0.5ms (Windows' default is 15.6ms) while you play, which makes frame pacing smoother and sleep-based FPS caps more accurate. It only runs while a game is open.",
            Impact.Situational, new[]
            {
                HKLM(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1),
            }, recommended: true, reboot: true));

        Windows.Add(Tweak.Registry("win.gpumsi", "GPU MSI interrupt mode",
            "Switches your graphics card to Message Signaled Interrupts, a faster way for the GPU to signal the CPU, instead of old shared interrupt lines. Can lower DPC latency and micro-stutter. Many new drivers already do this; if so, this switch shows as already on.",
            Impact.Low, () => SysInfo.Gpus.Where(g => g.PnpId?.StartsWith("PCI", StringComparison.OrdinalIgnoreCase) == true && (g.IsNvidia || g.IsAmd))
                .Select(g => HKLM($@"SYSTEM\CurrentControlSet\Enum\{g.PnpId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties", "MSISupported", 1)),
            reboot: true, available: () => SysInfo.Gpus.Any(g => g.PnpId?.StartsWith("PCI", StringComparison.OrdinalIgnoreCase) == true && (g.IsNvidia || g.IsAmd))));

        Windows.Add(Tweak.Registry("win.telemetry", "Turn off Windows telemetry",
            "Stops the 'Connected User Experiences and Telemetry' service, which collects usage data and sends it to Microsoft in the background. It frees a little CPU, disk and network, and it's better for privacy.",
            Impact.Low, new[]
            {
                HKLM(@"SYSTEM\CurrentControlSet\Services\DiagTrack", "Start", 4, def: 2),
                HKLM(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1),
            }, reboot: true));

        Windows.Add(Tweak.Registry("win.mitigations", "Turn off Spectre / Meltdown CPU patches",
            "Windows slows the CPU down a little to protect against the Spectre and Meltdown CPU bugs. Turning that off gives back CPU performance: noticeable on older Intel CPUs (around 8th gen and before), very small on new CPUs.",
            Impact.Situational, new[]
            {
                HKLM(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride", 3),
                HKLM(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverrideMask", 3),
            }, reboot: true, advanced: true,
            warning: "SECURITY TRADE-OFF: this reopens real CPU security holes that websites or malware could use. Don't do this on a PC you do banking or work on. Anti-cheats don't care either way."));


        Windows.Add(Tweak.Registry("win.windowedopt", "Optimizations for windowed games",
            "Most people play in borderless/windowed mode now. This Windows 11 feature switches older DirectX 10/11 games to the modern 'flip' presentation model, the same fast path fullscreen uses. Microsoft says it lowers latency, and it also lets G-SYNC/FreeSync work in borderless mode.",
            Impact.Medium, () => new[] { HKCU(@"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", WindowedOptValue(), RegistryValueKind.String) },
            recommended: true, available: () => Environment.OSVersion.Version.Build >= 22000));

        Windows.Add(CommandTweak("win.usbsuspend", "USB power saving: Off",
            "Windows can put USB ports to sleep to save power. That sometimes makes your mouse or keyboard miss inputs or stutter for a moment when they wake back up. This keeps USB ports awake while plugged in.",
            Impact.Situational,
            check: () => PowerValue(UsbSub, UsbSuspend) == 0,
            read: () => (PowerValue(UsbSub, UsbSuspend) ?? 1).ToString(),
            apply: () => { Shell.Run("powercfg", $"/setacvalueindex scheme_current {UsbSub} {UsbSuspend} 0"); Shell.Run("powercfg", "/setactive scheme_current"); },
            revert: prev => { Shell.Run("powercfg", $"/setacvalueindex scheme_current {UsbSub} {UsbSuspend} {prev}"); Shell.Run("powercfg", "/setactive scheme_current"); },
            recommended: true));

        Windows.Add(CommandTweak("win.cstates", "CPU sleep states: Off (lowest input delay)",
            "Stops the CPU from dropping into deep sleep states between frames, so it reacts instantly every time. This gives the lowest and most consistent input delay and DPC latency, and it's what many competitive players use.",
            Impact.Situational,
            check: () => PowerValue(ProcSub, IdleDisable) == 1,
            read: () => (PowerValue(ProcSub, IdleDisable) ?? 0).ToString(),
            apply: () => { Shell.Run("powercfg", $"/setacvalueindex scheme_current {ProcSub} {IdleDisable} 1"); Shell.Run("powercfg", "/setactive scheme_current"); },
            revert: prev => { Shell.Run("powercfg", $"/setacvalueindex scheme_current {ProcSub} {IdleDisable} {prev}"); Shell.Run("powercfg", "/setactive scheme_current"); },
            warning: "Your CPU runs hotter and uses much more power, even at idle. On AMD Ryzen and some Intel CPUs it can LOWER boost clocks and FPS. Use Auto-tune on the Benchmark tab to check. Not for laptops.", advanced: true));

        // ------------------------------------------------------------ NETWORK
        Network.Add(Tweak.Registry("net.throttle", "Turn off network throttling",
            "Windows limits how many network packets it handles while media is playing. This removes that limit, so game traffic isn't slowed down while Spotify or YouTube is open.",
            Impact.Low, new[]
            {
                HKLM(MM, "NetworkThrottlingIndex", 0xFFFFFFFFu, def: 10),
            }, recommended: true));

        Network.Add(Tweak.Registry("net.nagle", "Turn off Nagle's algorithm (TCP no-delay)",
            "Sends small TCP packets right away instead of batching them. Lowers delay in games that use TCP (some MMOs and older games, like WoW or RuneScape). Most modern shooters, including Fortnite, Valorant and CS2, use UDP, so this does nothing for them.",
            Impact.Situational, () => ActiveInterfaces().SelectMany(g => new[]
            {
                HKLM($@"{TcpIf}\{g}", "TcpAckFrequency", 1),
                HKLM($@"{TcpIf}\{g}", "TCPNoDelay", 1),
            }), reboot: true));

        Network.Add(Tweak.Registry("net.deliveryopt", "Stop Windows uploading updates to other PCs",
            "By default Windows can use your upload bandwidth to send updates to strangers on the internet. On slow connections that causes lag spikes. This keeps updates downloading from Microsoft only.",
            Impact.Medium, new[]
            {
                HKLM(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0),
            }, recommended: true));


        Network.Add(Tweak.Registry("net.adapter", "Low-latency network adapter settings",
            "Changes your network card's own settings for gaming: Interrupt Moderation off (packets get handled right away instead of in batches), Flow Control off, and Energy-Efficient Ethernet, Green Ethernet and power saving off (no sleep pauses), Large Send Offload and Receive Segment Coalescing off (stops the card bundling packets together, which can delay small game packets). Only settings your adapter actually has are changed.",
            Impact.Situational, () => NetAdapterSets(), reboot: true, available: () => NetAdapterSets().Any(),
            warning: "Slightly more CPU use from network traffic, and max upload speed can drop a little on very fast (1 Gbps+) connections. Your connection drops for a second while the adapter restarts."));


        Network.Add(Tweak.Registry("net.qos", "Remove Windows bandwidth reservation",
            "Makes sure Windows' QoS Packet Scheduler doesn't hold back any bandwidth for itself, and lets game priority tags (from the per-game 'Network priority' boost) work on home PCs that aren't on a company network.",
            Impact.Low, new[]
            {
                HKLM(@"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", 0),
                HKLM(@"SYSTEM\CurrentControlSet\Services\Tcpip\QoS", "Do not use NLA", "1", RegistryValueKind.String),
            }, recommended: true));


        // ---------- download / upload speed ----------
        Network.Add(CommandTweak("net.autotune", "TCP auto-tuning: Normal (full download speed)",
            "Lets Windows grow its download window to match your connection. Some 'optimizer' apps and old guides set this to Disabled or Restricted, which can cap fast connections at a fraction of their speed. This puts it back to Normal, the fastest setting.",
            Impact.High,
            check: () => Ps("(Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal").Trim().Equals("Normal", StringComparison.OrdinalIgnoreCase),
            read: () => Ps("(Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal").Trim(),
            apply: () => Shell.Run("netsh", "int tcp set global autotuninglevel=normal"),
            revert: prev => Shell.Run("netsh", $"int tcp set global autotuninglevel={NetshLevel(prev)}"),
            recommended: true));

        Network.Add(CommandTweak("net.rss", "Receive Side Scaling: On",
            "Spreads incoming network traffic across several CPU cores instead of one. Needed for full speed on fast (500 Mbps+) connections, and it also smooths out ping while you download.",
            Impact.Medium,
            check: () => Ps("(Get-NetOffloadGlobalSetting).ReceiveSideScaling").Trim().Equals("Enabled", StringComparison.OrdinalIgnoreCase),
            read: () => Ps("(Get-NetOffloadGlobalSetting).ReceiveSideScaling").Trim(),
            apply: () => Shell.Run("netsh", "int tcp set global rss=enabled"),
            revert: prev => Shell.Run("netsh", $"int tcp set global rss={(prev.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? "disabled" : "enabled")}"),
            recommended: true));


        Network.Add(CommandTweak("net.uro", "UDP receive coalescing (URO): Off",
            "Almost every online game (Fortnite, VALORANT, CS2, Apex, CoD...) sends its traffic over UDP. Windows 11 can bundle incoming UDP packets together to save CPU, and that can hold a game update back for a moment. This makes Windows hand each game packet over the moment it arrives.",
            Impact.Situational,
            check: () => UroState() == "disabled",
            read: () => string.IsNullOrEmpty(UroState()) ? "enabled" : UroState(),
            apply: () => Shell.Run("netsh", "int udp set global uro=disabled"),
            revert: prev => Shell.Run("netsh", $"int udp set global uro={(prev == "disabled" ? "disabled" : "enabled")}"),
            recommended: true, available: () => !string.IsNullOrEmpty(UroState())));

        Network.Add(Tweak.Registry("net.wulimit", "Limit Windows Update to 25% of your bandwidth",
            "Windows Update downloads can grab your whole connection in the background, which slows your Steam/Epic downloads and spikes your ping. This keeps background update downloads to 25% of your bandwidth.",
            Impact.Medium, new[]
            {
                HKLM(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOPercentageMaxBackgroundBandwidth", 25),
            }, recommended: true));


        // ---------- Wi-Fi (only shown if this PC has a Wi-Fi adapter) ----------
        Network.Add(new Tweak
        {
            Id = "wifi.scanpause", Title = "Pause Wi-Fi scanning while gaming",
            Description = "About once a minute, Windows scans for other Wi-Fi networks, and each scan causes a ping spike of 100+ ms (a very common cause of 'random' lag on Wi-Fi). With this on, Game Boost pauses scanning only while a game is running and turns it back on when you quit. Nothing changes outside of games.",
            Impact = Impact.High, Recommended = true,
            Warning = "Needs Game Boost on (Settings). While a game is running, Windows won't look for other networks, but you stay connected to yours.",
            IsAvailable = () => WifiInterfaces().Any(),
            IsApplied = () => Store.Settings.WifiScanPauseWhileGaming,
            Apply = () => { Store.Settings.WifiScanPauseWhileGaming = true; Store.SaveSettings(); if (BoostMode.ActiveGame != null) WifiScan.Pause(); },
            Revert = () => { Store.Settings.WifiScanPauseWhileGaming = false; Store.SaveSettings(); WifiScan.Resume(); },
        });

        Network.Add(CommandTweak("wifi.power", "Wi-Fi power saving: Off",
            "Stops your Wi-Fi card from napping between packets to save power. Those naps add delay and cause ping spikes, especially on laptops.",
            Impact.Medium,
            check: () => PowerValue(WifiSub, WifiPower) == 0,
            read: () => (PowerValue(WifiSub, WifiPower) ?? 0).ToString(),
            apply: () => { Shell.Run("powercfg", $"/setacvalueindex scheme_current {WifiSub} {WifiPower} 0"); Shell.Run("powercfg", "/setactive scheme_current"); },
            revert: prev => { Shell.Run("powercfg", $"/setacvalueindex scheme_current {WifiSub} {WifiPower} {prev}"); Shell.Run("powercfg", "/setactive scheme_current"); },
            recommended: true, available: () => WifiInterfaces().Any()));

        Network.Add(Tweak.Registry("wifi.adapter", "Wi-Fi adapter gaming settings",
            "Sets your Wi-Fi card for stable gaming: lowest roaming aggressiveness (it stops jumping between access points mid-match), prefer 5 GHz, and turns off packet coalescing and power-save modes that hold packets back. Only settings your card actually has are changed.",
            Impact.Situational, () => WifiAdapterSets(), reboot: true, available: () => WifiAdapterSets().Any(),
            warning: "If you move around the house a lot, low roaming means your laptop stays on a weaker access point longer. Your Wi-Fi drops for a second while the adapter restarts."));

        // ------------------------------------------------------------ NVIDIA
        Nvidia.Add(Nvapi.CreateTweak("nv.power", "Power management: Prefer maximum performance",
            "Stops the GPU from dropping to low clock speeds when a game isn't pushing it hard. Fixes stutter in menus, in esports games and in CPU-bound games.",
            Impact.Medium, Nvapi.PREFERRED_PSTATE, Nvapi.PSTATE_PREFER_MAX, recommended: true,
            warning: "The GPU idles warmer and uses more power. You can also set this for individual games only, in NVIDIA Control Panel."));

        Nvidia.Add(Nvapi.CreateTweak("nv.lowlatency", "Low Latency Mode: On",
            "Limits the CPU to 1 frame queued ahead of the GPU, so your clicks show up on screen sooner. Helps in GPU-heavy games that don't have NVIDIA Reflex. If a game has Reflex (Fortnite, Valorant, CS2, Apex, CoD...), turn on Reflex in that game too; Reflex replaces this setting.",
            Impact.Medium, Nvapi.PRERENDERLIMIT, 1, recommended: true));

        Nvidia.Add(Nvapi.CreateTweak("nv.texture", "Texture filtering: High performance",
            "Uses cheaper texture filtering. Gives a few extra FPS on older and low-end cards, and textures look slightly less sharp.",
            Impact.Low, Nvapi.QUALITY_ENHANCEMENTS, Nvapi.QUALITY_HIGHPERFORMANCE));

        Nvidia.Add(Nvapi.CreateTweak("nv.vsync", "Force V-Sync off",
            "Removes the V-Sync FPS cap and its input lag in every game.",
            Impact.Situational, Nvapi.VSYNCMODE, Nvapi.VSYNC_FORCEOFF,
            warning: "If you have a G-SYNC monitor, the best setup is usually G-SYNC + V-Sync ON in the control panel + Reflex or an FPS cap 3 below your refresh rate. Leave this off in that case."));

        Nvidia.Add(Nvapi.CreateTweak("nv.threaded", "Threaded optimization: On",
            "Spreads OpenGL driver work across more CPU cores. Mainly helps Minecraft Java and other OpenGL games.",
            Impact.Situational, Nvapi.OGL_THREAD_CONTROL, Nvapi.OGL_THREAD_ENABLE));

        Nvidia.Add(Nvapi.CreateTweak("nv.shadercache", "Shader cache size: Unlimited",
            "Lets the driver keep every compiled shader on disk instead of the default 4GB. Games don't have to re-compile shaders, which is the #1 cause of stutter in modern games like Fortnite, CoD and Marvel Rivals.",
            Impact.Medium, Nvapi.SHADERCACHE_MAX_SIZE, Nvapi.SHADERCACHE_UNLIMITED, recommended: true,
            warning: "Uses more disk space over time (usually 5-20GB). Clear it any time in Cleanup."));

        Nvidia.Add(Nvapi.CreateTweak("nv.refresh", "Preferred refresh rate: Highest available",
            "Forces games to run your monitor at its highest refresh rate, even games that would otherwise pick 60Hz in fullscreen.",
            Impact.Medium, Nvapi.REFRESH_RATE_OVERRIDE, Nvapi.REFRESH_HIGHEST, recommended: true));

        Nvidia.Add(Nvapi.CreateTweak("nv.aniso", "Anisotropic sample optimization: On",
            "Uses fewer texture samples at steep angles. Gives a small FPS gain with barely any visual difference.",
            Impact.Low, Nvapi.ANISO_OPTS2, 1));

        Nvidia.Add(Nvapi.CreateTweak("nv.aagamma", "Antialiasing gamma correction: Off",
            "Skips extra work when the driver forces anti-aliasing. A tiny gain; competitive configs turn it off.",
            Impact.Low, Nvapi.AA_GAMMA, 0));

        Nvidia.Add(Nvapi.CreateTweak("nv.fpscap", "Max frame rate: 3 below your refresh rate",
            "Caps FPS just under your monitor's refresh rate, e.g. 141 FPS on 144Hz. With G-SYNC this is the smoothest, lowest-lag setup there is: no tearing and no V-Sync lag. It also stops the GPU running at 100% and adding input lag.",
            Impact.Situational, Nvapi.FRL_FPS, () => (uint)Math.Max(30, (Display.GetPrimary()?.MaxHz ?? 60) - 3),
            warning: "Without G-SYNC/FreeSync, competitive players often prefer uncapped FPS plus Reflex. In games with Reflex, Reflex already caps FPS for you when G-SYNC is on."));

        AddAmd();

        // Only keep what makes a real, noticeable difference. These were measured/debated as tiny or placebo.
        var cut = new HashSet<string>
        {
            "win.gamemode", "win.mmcss", "win.foreground", "win.powerthrottle", "win.bgapps", "win.transparency",
            "win.visualfx", "win.telemetry", "win.gpumsi",
            "net.throttle", "net.nagle", "net.qos",
            "nv.texture", "nv.aniso", "nv.aagamma",
        };
        // Visual effects and transparency are tiny on gaming PCs but help integrated graphics; keep them for the PC plan.
        Hidden.AddRange(Windows.Where(t => t.Id is "win.visualfx" or "win.transparency"));
        Windows.RemoveAll(t => cut.Contains(t.Id));
        Network.RemoveAll(t => cut.Contains(t.Id));
        Nvidia.RemoveAll(t => cut.Contains(t.Id));
    }

    /// <summary>Not shown in lists; only used by the 'Best for my PC' plan on low-end PCs.</summary>
    public static readonly List<Tweak> Hidden = new();

    /// <summary>Tweaks removed from the app in later versions; still undone by "Undo everything" if they were applied.</summary>
    public static readonly string[] Retired = { "win.gamemode", "win.mmcss", "win.foreground", "win.powerthrottle", "win.bgapps", "win.transparency", "win.visualfx", "win.telemetry", "win.gpumsi", "net.throttle", "net.nagle", "net.qos", "nv.texture", "nv.aniso", "nv.aagamma" };

    static void AddAmd()
    {
        Amd.Add(Tweak.Registry("amd.ulps", "Turn off ULPS (Ultra Low Power State)",
            "ULPS puts parts of the Radeon driver into a deep sleep to save power. Waking up from it can cause stutter, black-screen flickers and unstable clocks, especially if you overclock or undervolt. This keeps the GPU awake.",
            Impact.Situational,
            () => AmdAdapterKeys().Select(k => RegSet.HKLM(k, "EnableUlps", 0, def: 1)),
            reboot: true, available: () => AmdAdapterKeys().Any(),
            warning: "Slightly higher idle power use. Mainly useful on desktops; laptops may want to leave it on."));

        // Radeon driver settings live in the adapter's UMD key as UTF-16 text stored in binary values ("1" = 31 00).
        static byte[] U(string v) => System.Text.Encoding.Unicode.GetBytes(v);
        IEnumerable<string> Umd() => AmdAdapterKeys().Select(k => k + @"\UMD");

        Amd.Add(Tweak.Registry("amd.flipqueue", "Frames queued ahead: 1 (lower input delay)",
            "The driver normally lets the CPU prepare up to 3 frames ahead of the GPU. Limiting it to 1 means your clicks reach the screen sooner. This is AMD's equivalent of NVIDIA's Low Latency Mode. In games with Anti-Lag 2 (CS2, Fortnite...) also turn that on in-game.",
            Impact.Medium,
            () => Umd().Select(k => RegSet.HKLM(k, "FlipQueueSize", U("1"), RegistryValueKind.Binary)),
            recommended: true, reboot: true, available: () => AmdAdapterKeys().Any(),
            warning: "In games where your CPU is the limit this can cost a few FPS. Use Auto-tune on the Benchmark tab to check."));

        Amd.Add(Tweak.Registry("amd.shadercache", "Shader cache: Always on",
            "Makes the driver save compiled shaders for every game, so effects don't have to be rebuilt mid-match. Fewer stutters, especially the first time you see new effects.",
            Impact.Medium,
            () => Umd().Select(k => RegSet.HKLM(k, "ShaderCache", U("2"), RegistryValueKind.Binary)),
            recommended: true, reboot: true, available: () => AmdAdapterKeys().Any()));

        Amd.Add(Tweak.Registry("amd.texture", "Texture filtering quality: Performance",
            "Uses cheaper texture filtering. A few extra FPS on older and low-end Radeon cards with almost no visible difference.",
            Impact.Low,
            () => Umd().Select(k => RegSet.HKLM(k, "TFQ", U("2"), RegistryValueKind.Binary)),
            reboot: true, available: () => AmdAdapterKeys().Any()));

        Amd.Add(Tweak.Registry("amd.tessellation", "Tessellation: limit to 8x",
            "Some games use far more tessellation (extra geometry detail) than you can see. Capping it at 8x gives a big FPS gain in those games and looks the same.",
            Impact.Situational,
            () => Umd().SelectMany(k => new[]
            {
                RegSet.HKLM(k, "Tessellation_OPTION", U("2"), RegistryValueKind.Binary),
                RegSet.HKLM(k, "Tessellation", U("8"), RegistryValueKind.Binary),
            }),
            recommended: true, reboot: true, available: () => AmdAdapterKeys().Any()));

        Amd.Add(Tweak.Registry("amd.deepsleep", "GPU deep sleep: Off",
            "Stops the Radeon core clock from dropping into deep sleep between frames, so it doesn't have to ramp back up. Steadier clocks, smoother frame times and less input delay. AMD's equivalent of NVIDIA's 'Prefer maximum performance'.",
            Impact.Medium,
            () => AmdAdapterKeys().Select(k => RegSet.HKLM(k, "PP_SclkDeepSleepDisable", 1, def: 0)),
            reboot: true, available: () => AmdAdapterKeys().Any() && !SysInfo.HasBattery,
            warning: "Higher idle power use and a bit more heat. Desktops only."));
    }


    const string NetClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>Standard NDIS keywords, only where the physical adapter already exposes them.</summary>
    static IEnumerable<RegSet> NetAdapterSets()
    {
        var keywords = new[] { "*InterruptModeration", "*FlowControl", "*EEE", "EnableGreenEthernet", "GreenEthernet", "AdvancedEEE", "EEELinkAdvertisement", "*LsoV2IPv4", "*LsoV2IPv6", "*LsoV1IPv4", "*RscIPv4", "*RscIPv6", "PowerSavingMode", "EnablePME", "ULPMode" };
        foreach (var sub in Reg.SubKeys(RegistryHive.LocalMachine, NetClass))
        {
            if (!int.TryParse(sub, out _)) continue;
            var path = $@"{NetClass}\{sub}";
            string[] names;
            try
            {
                // physical adapters only (skip VPN/virtual/Hyper-V)
                var chars = Reg.Get(RegistryHive.LocalMachine, path, "Characteristics");
                if (chars == null || (Convert.ToInt32(chars) & 0x4) == 0) continue; // NCF_PHYSICAL
                names = Reg.ValueNames(RegistryHive.LocalMachine, path);
            }
            catch { continue; }
            foreach (var k in keywords)
                if (names.Contains(k, StringComparer.OrdinalIgnoreCase))
                    yield return RegSet.HKLM(path, k, "0", RegistryValueKind.String);
        }
    }


    // PowerShell / netsh are slow to start, so answers are cached for a bit and cleared when something changes.
    static readonly Dictionary<string, (DateTime At, string Value)> CmdCache = new();
    static string Cached(string key, Func<string> run)
    {
        lock (CmdCache)
            if (CmdCache.TryGetValue(key, out var c) && (DateTime.Now - c.At).TotalSeconds < 60) return c.Value;
        var v = run();
        lock (CmdCache) CmdCache[key] = (DateTime.Now, v);
        return v;
    }
    static void ClearCmdCache() { lock (CmdCache) CmdCache.Clear(); }

    static string Ps(string command) => Cached("ps:" + command, () =>
        Shell.Run("powershell", $"-NoProfile -NonInteractive -Command \"{command}\"", 20000).Output ?? "");

    /// <summary>"enabled"/"disabled", or null on Windows versions without UDP receive offload.</summary>
    static string UroState() => Cached("uro", () => UroStateRaw() ?? "");

    static string UroStateRaw()
    {
        var r = Shell.Run("netsh", "int udp show global", 8000);
        if (r.Code != 0) return null;
        var line = r.Output.Split('\n').FirstOrDefault(l => l.Contains("Offload", StringComparison.OrdinalIgnoreCase) || l.Contains("URO", StringComparison.OrdinalIgnoreCase));
        if (line == null) return null;
        if (line.Contains("disabled", StringComparison.OrdinalIgnoreCase)) return "disabled";
        if (line.Contains("enabled", StringComparison.OrdinalIgnoreCase)) return "enabled";
        return null;
    }

    static string NetshLevel(string psLevel) => psLevel?.ToLowerInvariant() switch
    {
        "disabled" => "disabled",
        "highlyrestricted" => "highlyrestricted",
        "restricted" => "restricted",
        "experimental" => "experimental",
        _ => "normal",
    };

    /// <summary>A tweak driven by commands: it reads the current value, saves it, and restores it exactly on undo.</summary>
    static Tweak CommandTweak(string id, string title, string desc, Impact impact, Func<bool> check, Func<string> read,
        Action apply, Action<string> revert, bool recommended = false, string warning = null, Func<bool> available = null, bool advanced = false) => new()
    {
        Id = id, Title = title, Description = desc, Impact = impact, Recommended = recommended, Warning = warning, Advanced = advanced,
        IsAvailable = available ?? (() => true),
        IsApplied = check,
        Apply = () =>
        {
            if (!Store.Backups.ContainsKey(id))
            {
                Store.Backups[id] = new TweakBackup { Extra = { ["previous"] = read() } };
                Store.SaveBackups();
            }
            apply();
            ClearCmdCache();
            Store.Log($"Applied {id}");
        },
        Revert = () =>
        {
            var prev = Store.Backups.TryGetValue(id, out var b) ? b.Extra.GetValueOrDefault("previous") : null;
            if (!string.IsNullOrWhiteSpace(prev)) revert(prev);
            ClearCmdCache();
            Store.Backups.Remove(id);
            Store.SaveBackups();
            Store.Log($"Reverted {id}");
        }
    };

    const string WifiSub = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", WifiPower = "12bbebe6-58d6-4636-95bb-3217ef867c1a";

    /// <summary>Names of Wi-Fi interfaces (e.g. "Wi-Fi"). These are adapter names, never network names, and are never shown.</summary>
    public static List<string> WifiInterfaces() => Cached("wifi-if", () =>
    {
        var r = Shell.Run("netsh", "wlan show interfaces", 5000);
        if (r.Code != 0) return "";
        var names = System.Text.RegularExpressions.Regex.Matches(r.Output, @"^\s*Name\s*:\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.Trim()).Where(n => n.Length > 0);
        return string.Join("\n", names);
    }).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Driver keywords on Wi-Fi adapters (Intel, Realtek, MediaTek, Killer...), only where the adapter has them.</summary>
    static IEnumerable<RegSet> WifiAdapterSets()
    {
        var wanted = new (string Key, string Value)[]
        {
            ("RoamAggressiveness", "0"), ("RoamingAggressiveness", "0"),     // lowest roaming
            ("RoamingPreferredBandType", "2"), ("PreferredBand", "2"),       // prefer 5 GHz
            ("*PacketCoalescing", "0"),                                      // no packet batching
            ("MIMOPowerSaveMode", "3"), ("uAPSDSupport", "0"),               // no power-save modes
            ("*WakeOnPattern", "0"), ("*WakeOnMagicPacket", "0"),
        };
        foreach (var sub in Reg.SubKeys(RegistryHive.LocalMachine, NetClass))
        {
            if (!int.TryParse(sub, out _)) continue;
            var path = $@"{NetClass}\{sub}";
            string[] names;
            try
            {
                var ifType = Reg.Get(RegistryHive.LocalMachine, path, "*IfType");
                if (ifType == null || Convert.ToInt32(ifType) != 71) continue; // 71 = IEEE 802.11 (Wi-Fi)
                names = Reg.ValueNames(RegistryHive.LocalMachine, path);
            }
            catch { continue; }
            foreach (var (k, v) in wanted)
                if (names.Contains(k, StringComparer.OrdinalIgnoreCase))
                    yield return RegSet.HKLM(path, k, v, RegistryValueKind.String);
        }
    }

    const string UsbSub = "2a737441-1930-4402-8d77-b2bebba308a3", UsbSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    const string ProcSub = "54533251-82be-4824-96c1-47b60b740d00", IdleDisable = "5d76a2ca-e8c0-402f-a133-2158492d58ad";

    /// <summary>Current AC value of a power-plan setting on the active plan.</summary>
    static int? PowerValue(string sub, string setting) => int.TryParse(Cached($"pwr:{sub}:{setting}:{PowerPlan.ActiveGuid()}", () =>
    {
        var r = Shell.Run("powercfg", $"/query scheme_current {sub} {setting}", 8000);
        // the AC value is the first-to-last hex value on its own line; language-independent
        var hex = System.Text.RegularExpressions.Regex.Matches(r.Output, @"0x([0-9a-fA-F]{8})").Select(m => m.Groups[1].Value).ToList();
        return hex.Count >= 2 ? Convert.ToInt32(hex[^2], 16).ToString() : "";
    }), out var v) ? v : null;

    /// <summary>Adds SwapEffectUpgradeEnable=1 to the user's DirectX settings string, keeping anything else in it.</summary>
    static string WindowedOptValue()
    {
        var cur = Reg.Get(RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings")?.ToString() ?? "";
        var parts = cur.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Where(p => !p.StartsWith("SwapEffectUpgradeEnable=", StringComparison.OrdinalIgnoreCase)).ToList();
        parts.Add("SwapEffectUpgradeEnable=1");
        return string.Join(";", parts) + ";";
    }

    static IEnumerable<string> ActiveInterfaces()
    {
        foreach (var g in Reg.SubKeys(RegistryHive.LocalMachine, TcpIf))
        {
            var path = $@"{TcpIf}\{g}";
            var dhcp = Reg.Get(RegistryHive.LocalMachine, path, "DhcpIPAddress")?.ToString();
            var ip = Reg.Get(RegistryHive.LocalMachine, path, "IPAddress") as string[];
            bool active = (!string.IsNullOrEmpty(dhcp) && dhcp != "0.0.0.0")
                          || (ip != null && ip.Any(a => !string.IsNullOrEmpty(a) && a != "0.0.0.0"));
            if (active) yield return g;
        }
    }

    static void ApplyMouseLive()
    {
        int Read(string n, int d) => int.TryParse(Reg.Get(RegistryHive.CurrentUser, @"Control Panel\Mouse", n)?.ToString(), out var v) ? v : d;
        var arr = new[] { Read("MouseThreshold1", 6), Read("MouseThreshold2", 10), Read("MouseSpeed", 1) };
        Native.SystemParametersInfo(Native.SPI_SETMOUSE, 0, arr, Native.SPIF_SENDCHANGE);
    }

    public record Preset(string Id, string Name, string Icon, string Blurb, Func<IEnumerable<Tweak>> TurnOn, Func<IEnumerable<Tweak>> TurnOff);

    static readonly string[] PowerHungry = { "win.powerplan", "nv.power", "win.cstates", "wifi.power", "win.usbsuspend" };

    public static List<Preset> Presets => new()
    {
        new("competitive", "Competitive", "\uE945", "Max FPS and the lowest input delay. Everything that helps, including the strong settings (V-Sync off, CPU sleep states off on desktops, adapter latency).",
            () => Visible.Where(t => (t.Recommended && !t.Advanced) || new[] { "nv.vsync", "win.hags", "net.adapter", "wifi.adapter" }.Contains(t.Id) || (t.Id == "win.cstates" && !SysInfo.HasBattery && !SysInfo.CpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))),
            () => Enumerable.Empty<Tweak>()),
        new("balanced", "Balanced", "\uE9F5", "All the recommended tweaks: big real gains with no heat or power downsides. Best for most people.",
            () => Visible.Where(t => t.Recommended && !t.Advanced),
            () => Visible.Where(t => t.Id == "win.cstates")),
        new("quiet", "Laptop / Quiet", "\uE83F", "Keeps the latency and network fixes but drops the power-hungry ones, so your PC runs cooler and quieter and your battery lasts.",
            () => Visible.Where(t => t.Recommended && !t.Advanced && !PowerHungry.Contains(t.Id)),
            () => Visible.Where(t => PowerHungry.Contains(t.Id))),
    };

    public static IEnumerable<Tweak> RecommendedForThisPc() =>
        Visible.Where(t => t.Recommended && !t.Advanced && SafeAvailable(t));

    /// <summary>Only the tweaks for the GPU brand this PC actually has.</summary>
    public static IEnumerable<Tweak> Visible => Windows.Concat(Network)
        .Concat(SysInfo.ShowNvidia ? Nvidia : Enumerable.Empty<Tweak>())
        .Concat(SysInfo.ShowAmd ? Amd : Enumerable.Empty<Tweak>());

    static bool SafeAvailable(Tweak t) { try { return t.IsAvailable(); } catch { return false; } }
}
