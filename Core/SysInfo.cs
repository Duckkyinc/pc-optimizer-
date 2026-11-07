using Microsoft.Win32;
using System.Management;

namespace Overdrive.Core;

public class GpuInfo
{
    public string Name { get; set; }
    public string DriverVersion { get; set; }
    public string PnpId { get; set; }
    public bool IsNvidia => Name?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsAmd => Name?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true || Name?.Contains("Radeon", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Windows reports e.g. 32.0.15.6094; NVIDIA calls that 560.94.</summary>
    public string NvidiaDriverVersion
    {
        get
        {
            if (!IsNvidia || string.IsNullOrEmpty(DriverVersion)) return DriverVersion;
            var digits = new string(DriverVersion.Where(char.IsDigit).ToArray());
            if (digits.Length < 5) return DriverVersion;
            var last5 = digits[^5..];
            return $"{last5[..3]}.{last5[3..]}";
        }
    }
}

public static class SysInfo
{
    public static string CpuName { get; private set; } = "Unknown CPU";
    public static int Cores { get; private set; }
    public static int Threads { get; private set; }
    public static string OsName { get; private set; } = "Windows";
    public static string OsBuild { get; private set; } = "";
    public static double RamGb { get; private set; }
    public static List<GpuInfo> Gpus { get; private set; } = new();
    public static bool HasBattery { get; private set; }

    public static GpuInfo PrimaryGpu =>
        Gpus.FirstOrDefault(g => g.IsNvidia) ?? Gpus.FirstOrDefault(g => g.IsAmd) ?? Gpus.FirstOrDefault();
    public static bool HasNvidia => Gpus.Any(g => g.IsNvidia);
    public static bool HasAmd => Gpus.Any(g => g.IsAmd);
    /// <summary>The GPU brand you game on. Laptops with Radeon integrated + NVIDIA dedicated count as NVIDIA.</summary>
    public static string GpuVendor => HasNvidia ? "NVIDIA" : HasAmd ? "AMD" : "Other";
    public static bool ShowNvidia => GpuVendor == "NVIDIA";
    public static bool ShowAmd => GpuVendor == "AMD";

    /// <summary>Fast hardware detection straight from the registry (WMI is slow, so it only fills in core counts later, in the background).</summary>
    public static void Load()
    {
        try
        {
            CpuName = Reg.Get(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.ToString()?.Trim() ?? CpuName;
        }
        catch { }
        Threads = Environment.ProcessorCount;
        Cores = Threads; // refined below

        try
        {
            const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            foreach (var sub in Reg.SubKeys(RegistryHive.LocalMachine, cls))
            {
                if (!int.TryParse(sub, out _)) continue;
                var path = $@"{cls}\{sub}";
                string name = null, ver = null, dev = null;
                try
                {
                    name = Reg.Get(RegistryHive.LocalMachine, path, "DriverDesc")?.ToString();
                    ver = Reg.Get(RegistryHive.LocalMachine, path, "DriverVersion")?.ToString();
                    dev = Reg.Get(RegistryHive.LocalMachine, path, "MatchingDeviceId")?.ToString();
                }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Remote", StringComparison.OrdinalIgnoreCase) || name.Contains("Mirror", StringComparison.OrdinalIgnoreCase)) continue;
                if (Gpus.Any(g => g.Name == name.Trim())) continue;
                Gpus.Add(new GpuInfo { Name = name.Trim(), DriverVersion = ver, PnpId = dev });
            }
        }
        catch (Exception ex) { Store.Log("GPU detect failed: " + ex.Message); }

        try
        {
            const string nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            OsBuild = Reg.Get(RegistryHive.LocalMachine, nt, "CurrentBuildNumber")?.ToString() ?? "";
            var product = Reg.Get(RegistryHive.LocalMachine, nt, "ProductName")?.ToString() ?? "Windows";
            // Windows 11 still says "Windows 10" in ProductName
            if (int.TryParse(OsBuild, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            OsName = product;
        }
        catch { }

        var mem = new Native.MEMORYSTATUSEX();
        if (Native.GlobalMemoryStatusEx(mem)) RamGb = mem.ullTotalPhys / 1024.0 / 1024 / 1024;
        if (Native.GetSystemPowerStatus(out var ps)) HasBattery = ps.BatteryFlag != 128 && ps.BatteryFlag != 255;

        // real core count (needs WMI) in the background
        Task.Run(() =>
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
                int cores = 0;
                foreach (ManagementObject o in s.Get()) cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                if (cores > 0) Cores = cores;
            }
            catch { }
        });
    }

    public static bool OnBattery => Native.GetSystemPowerStatus(out var ps) && ps.ACLineStatus == 0;

    // ---- live usage ----
    public class CpuSampler { internal ulong LastIdle, LastTotal; }
    static readonly CpuSampler DefaultSampler = new();

    public static double CpuUsage() => CpuUsage(DefaultSampler);

    public static double CpuUsage(CpuSampler s)
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong total = kernel.Value + user.Value; // kernel time includes idle
        ulong dIdle = idle.Value - s.LastIdle, dTotal = total - s.LastTotal;
        s.LastIdle = idle.Value; s.LastTotal = total;
        if (dTotal == 0) return 0;
        return Math.Clamp(100.0 * (1.0 - (double)dIdle / dTotal), 0, 100);
    }

    public static (double UsedGb, double Percent) RamUsage()
    {
        var mem = new Native.MEMORYSTATUSEX();
        if (!Native.GlobalMemoryStatusEx(mem)) return (0, 0);
        var used = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024.0 / 1024 / 1024;
        return (used, mem.dwMemoryLoad);
    }
}
