using System.Text.RegularExpressions;

namespace Overdrive.Core;

/// <summary>
/// Builds the best set of tweaks for THIS PC from its specs: CPU brand and cores, RAM, GPU brand and age,
/// laptop or desktop, monitor refresh rate and Wi-Fi vs Ethernet. Each choice comes with a plain reason.
/// </summary>
public static class PcPlan
{
    public record Item(Tweak Tweak, bool Include, string Reason);
    public record Plan(string Summary, List<Item> Items, bool LowEndBoost);

    public static Plan Build()
    {
        bool laptop = SysInfo.HasBattery;
        string cpu = SysInfo.CpuName ?? "";
        bool ryzen = cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        int threads = SysInfo.Threads;
        bool lowEnd = LowEnd.IsLowEndPc;
        bool weakCpu = threads > 0 && threads <= 6;
        string gpu = SysInfo.PrimaryGpu?.Name ?? "";
        bool modernGpu = Regex.IsMatch(gpu, @"RTX|GTX 1[0-6]\d\d|RX [5-9]\d{3}|Arc", RegexOptions.IgnoreCase);
        int hz = 60; try { hz = Display.GetPrimary()?.MaxHz ?? 60; } catch { }
        bool wifi = false; try { wifi = HwMonitor.GetConnection()?.Type?.Contains("Wi", StringComparison.OrdinalIgnoreCase) == true; } catch { }

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(gpu)) parts.Add(gpu.Replace("NVIDIA ", "").Replace("GeForce ", ""));
        if (!string.IsNullOrEmpty(cpu)) parts.Add(Regex.Replace(cpu, @"\s+(\d+-Core Processor|CPU @.*|with Radeon.*)$", "").Replace("(R)", "").Replace("(TM)", "").Trim());
        if (SysInfo.RamGb > 0) parts.Add($"{SysInfo.RamGb:0} GB RAM");
        parts.Add(laptop ? "laptop" : "desktop");
        parts.Add($"{hz} Hz");
        parts.Add(wifi ? "Wi-Fi" : "Ethernet");

        var items = new List<Item>();
        var all = Catalog.Visible.Concat(Catalog.Hidden).GroupBy(t => t.Id).Select(g => g.First()).ToDictionary(t => t.Id);
        void Rule(string id, bool include, string reason) { if (all.TryGetValue(id, out var t)) items.Add(new Item(t, include, reason)); }

        // ---- power
        Rule("win.powerplan", !laptop, laptop ? "Laptop: Game Boost already switches to max power only while you play, so your battery lasts." : "Keeps the CPU at full speed. One of the biggest real gains.");
        Rule("win.cstates", !laptop && !ryzen && !lowEnd && threads >= 8,
            laptop ? "Laptop: would make it run hot and throttle." : ryzen ? "Ryzen CPUs boost higher with sleep states on. Turning them off can LOWER FPS." : lowEnd || threads < 8 ? "Uses a lot of power for a tiny gain on your CPU." : "Lowest input delay on a desktop with your CPU.");
        Rule("win.usbsuspend", !laptop, laptop ? "Laptop: USB power saving helps battery life." : "Stops your mouse/keyboard USB ports from sleeping, which can cause hitches.");
        Rule("nv.power", !laptop, laptop ? "Laptop: keeps the GPU from running at full power on battery." : "Stops your GPU from downclocking mid-game.");
        Rule("amd.ulps", !laptop, laptop ? "Laptop: ULPS saves battery." : "Stops your Radeon dropping into deep sleep, which can cause stutter.");

        // ---- windows
        Rule("win.gamedvr", true, "Background recording costs FPS in every game.");
        Rule("win.mouseaccel", true, "Raw, consistent aim. No acceleration.");
        Rule("win.timerres", true, "More precise frame pacing and input timing.");
        Rule("win.windowedopt", true, "Lower latency for older DirectX games in borderless mode.");
        Rule("win.hags", modernGpu, modernGpu ? "Your GPU supports it well. Lower latency and needed for DLSS Frame Generation." : "Your GPU is older; it can cause stutter there.");
        Rule("win.vbs", weakCpu || lowEnd, weakCpu || lowEnd ? $"Your CPU ({threads} threads) loses up to 10% FPS to this security layer." : "Your CPU is fast enough that the gain is small. Kept on for security.");
        Rule("win.mitigations", false, "Skipped: removes CPU security protections. Turn it on yourself in the Windows tab if you want it.");
        Rule("win.visualfx", lowEnd, lowEnd ? "Low-end PC: Windows animations steal GPU time from integrated graphics." : "No real gain on your PC.");
        Rule("win.transparency", lowEnd, lowEnd ? "Low-end PC: transparency effects cost GPU time." : "No real gain on your PC.");

        // ---- network
        Rule("net.deliveryopt", true, "Stops Windows uploading updates to other PCs while you play.");
        Rule("net.wulimit", true, "Stops Windows Update hogging your connection.");
        Rule("net.autotune", true, "Lets downloads use your full speed.");
        Rule("net.rss", threads >= 4, threads >= 4 ? "Spreads network work across your CPU cores." : "Your CPU has too few cores to benefit.");
        Rule("net.uro", true, "Fixes UDP packet batching that adds delay in games.");
        Rule("net.adapter", !wifi && !lowEnd && threads >= 8, wifi ? "You're on Wi-Fi; this is for Ethernet adapters." : lowEnd || threads < 8 ? "Costs extra CPU on your PC." : "Lowest network delay on Ethernet.");
        Rule("wifi.power", wifi, wifi ? "Stops your Wi-Fi from napping, which causes ping spikes." : "You're on Ethernet.");
        Rule("wifi.adapter", wifi, wifi ? "Best Wi-Fi adapter settings for gaming." : "You're on Ethernet.");
        Rule("wifi.scanpause", wifi, wifi ? "Stops background Wi-Fi scans that cause lag spikes." : "You're on Ethernet.");

        // ---- AMD
        Rule("amd.flipqueue", !weakCpu, weakCpu ? $"Your CPU ({threads} threads) is likely the limit, and this would cost FPS." : "Clicks reach the screen sooner (AMD's low latency setting).");
        Rule("amd.shadercache", true, "Fewer stutters when games load new effects.");
        Rule("amd.tessellation", true, "Big FPS gain in games that overdo tessellation, with no visible difference.");
        Rule("amd.texture", lowEnd, lowEnd ? "Low-end PC: a few extra FPS from cheaper texture filtering." : "Tiny gain on your GPU; keeps textures sharp.");
        Rule("amd.deepsleep", !laptop, laptop ? "Laptop: GPU deep sleep saves battery." : "Steadier GPU clocks and less input delay.");

        // ---- NVIDIA
        Rule("nv.shadercache", true, "Fewer stutters when games load new effects.");
        Rule("nv.lowlatency", !weakCpu, weakCpu ? $"Your CPU ({threads} threads) is likely the limit, and this would cost FPS." : "Lower input delay in games without NVIDIA Reflex.");
        Rule("nv.vsync", true, "V-Sync adds input delay. Use G-SYNC or an in-game cap instead.");
        Rule("nv.refresh", hz > 60, hz > 60 ? $"Makes games use your full {hz} Hz." : "Your monitor is 60 Hz.");
        Rule("nv.threaded", true, "Helps OpenGL games like Minecraft use more CPU cores.");
        Rule("nv.fpscap", false, "Skipped: a cap lowers your max FPS. Only use it with G-SYNC.");

        return new Plan(string.Join("  ·  ", parts), items, lowEnd);
    }
}
