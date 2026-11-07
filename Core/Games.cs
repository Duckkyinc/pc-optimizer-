using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Overdrive.Core;

public class GameDef
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Short { get; init; }
    public string Genre { get; init; }
    public string ColorA { get; init; } = "#00E5FF";
    public string ColorB { get; init; } = "#8B5CF6";
    public string[] ProcessNames { get; init; } = Array.Empty<string>();
    public Func<IEnumerable<string>> Candidates { get; init; } = () => Array.Empty<string>();
    public string LaunchUri { get; init; }
    public Func<string> LaunchCommand { get; init; }
    public string AntiCheat { get; init; }
    public string[] Tips { get; init; } = Array.Empty<string>();
    public bool IsCustom { get; init; }
    public bool IsDiscovered { get; init; }
    /// <summary>Store apps have no accessible .exe; a found install folder counts as installed.</summary>
    public bool AllowFolder { get; init; }
}

public static class Games
{
    // ---------------------------------------------------------------- launchers
    static IEnumerable<string> FixedDrives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok; try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }

    static List<string> _steamLibs;
    public static List<string> SteamLibraries()
    {
        if (_steamLibs != null) return _steamLibs;
        var libs = new List<string>();
        try
        {
            var steam = Reg.Get(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")?.ToString()
                        ?? Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")?.ToString();
            if (steam != null)
            {
                steam = steam.Replace('/', '\\');
                libs.Add(steam);
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }
        catch (Exception ex) { Store.Log("Steam scan failed: " + ex.Message); }
        foreach (var d in FixedDrives())
        {
            libs.Add(Path.Combine(d, "Program Files (x86)", "Steam"));
            libs.Add(Path.Combine(d, "SteamLibrary"));
        }
        return _steamLibs = libs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    static IEnumerable<string> Steam(string relative) =>
        SteamLibraries().Select(l => Path.Combine(l, "steamapps", "common", relative));

    static List<(string Name, string Dir)> _epic;
    static IEnumerable<string> Epic(string displayName, string relativeExe)
    {
        if (_epic == null)
        {
            _epic = new();
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.item"))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        var root = doc.RootElement;
                        if (root.TryGetProperty("DisplayName", out var n) && root.TryGetProperty("InstallLocation", out var l))
                            _epic.Add((n.GetString(), l.GetString()));
                    }
            }
            catch (Exception ex) { Store.Log("Epic scan failed: " + ex.Message); }
        }
        foreach (var e in _epic.Where(e => string.Equals(e.Name, displayName, StringComparison.OrdinalIgnoreCase)))
            yield return Path.Combine(e.Dir, relativeExe);
        foreach (var d in FixedDrives())
        {
            yield return Path.Combine(d, "Program Files", "Epic Games", displayName.Replace(" ", ""), relativeExe);
            yield return Path.Combine(d, "Epic Games", displayName.Replace(" ", ""), relativeExe);
        }
    }

    static IEnumerable<string> OnDrives(string relative) => FixedDrives().Select(d => Path.Combine(d, relative));

    static string RiotClient() => OnDrives(@"Riot Games\Riot Client\RiotClientServices.exe").FirstOrDefault(File.Exists);

    static IEnumerable<string> RobloxPaths()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
        };
        foreach (var r in roots.Where(Directory.Exists))
            foreach (var d in Directory.GetDirectories(r).OrderByDescending(Directory.GetLastWriteTime))
                yield return Path.Combine(d, "RobloxPlayerBeta.exe");
    }

    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Minecraft Java runs on its own bundled javaw.exe. Find it for every common launcher.</summary>
    static IEnumerable<string> MinecraftJavaPaths()
    {
        var roots = new[]
        {
            Path.Combine(LocalData, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"), // new launcher (Store / Xbox app)
            Path.Combine(AppData, ".minecraft", "runtime"),                       // classic launcher
            Path.Combine(Home, ".lunarclient", "jre"),                            // Lunar Client
            Path.Combine(AppData, "PrismLauncher", "java"),                       // Prism
            Path.Combine(AppData, "ModrinthApp", "meta", "java_versions"),        // Modrinth
            Path.Combine(Home, "curseforge", "minecraft", "Install", "runtime"),  // CurseForge
            Path.Combine(AppData, "Badlion Client"),                              // Badlion
            Path.Combine(AppData, ".feather"), Path.Combine(AppData, "feather"),  // Feather
            Path.Combine(AppData, ".tlauncher"),                                  // TLauncher
            Path.Combine(AppData, "gdlauncher_next"), Path.Combine(AppData, "gdlauncher_carbon"), // GDLauncher
            Path.Combine(AppData, "ATLauncher", "jre"),                           // ATLauncher
            Path.Combine(AppData, "com.modrinth.theseus", "meta", "java_versions"), // Modrinth (older)
            Path.Combine(AppData, ".lunarclient", "jre"),
        };
        var found = new List<FileInfo>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var r in roots.Where(Directory.Exists))
        {
            try { found.AddRange(Directory.EnumerateFiles(r, "javaw.exe", opts).Take(20).Select(f => new FileInfo(f))); } catch { }
            try { found.AddRange(Directory.EnumerateFiles(r, "java.exe", opts).Take(20).Select(f => new FileInfo(f))); } catch { }
        }
        // newest runtime first (Minecraft always uses the newest one for current versions)
        foreach (var f in found.OrderByDescending(f => f.LastWriteTimeUtc)) yield return f.FullName;
        // installed but never launched yet: point at the launcher so it still shows as installed
        if (MinecraftLauncher() is string l) yield return l;
    }

    public static List<string> AllMinecraftJava() => MinecraftJavaPaths().Where(p => p.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase) || p.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static string MinecraftLauncher() => new[]
    {
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "XboxGames", "Minecraft Launcher", "Content", "Minecraft.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "MinecraftLauncher.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft", "MinecraftLauncher.exe"),
        Path.Combine(LocalData, "Programs", "lunarclient", "Lunar Client.exe"),
        Path.Combine(LocalData, "Programs", "PrismLauncher", "prismlauncher.exe"),
        Path.Combine(LocalData, "Programs", "Badlion Client", "Badlion Client.exe"),
        Path.Combine(LocalData, "Programs", "Feather Launcher", "Feather Launcher.exe"),
        Path.Combine(AppData, ".minecraft", "TLauncher.exe"),
        Path.Combine(LocalData, "Programs", "Modrinth App", "Modrinth App.exe"),
        Path.Combine(LocalData, "Programs", "CurseForge Windows", "CurseForge.exe"),
    }.Concat(FixedDrives().Select(d => Path.Combine(d, "XboxGames", "Minecraft Launcher", "Content", "Minecraft.exe"))).FirstOrDefault(File.Exists);

    static IEnumerable<string> MinecraftBedrockPaths()
    {
        yield return Path.Combine(LocalData, "Packages", "Microsoft.MinecraftUWP_8wekyb3d8bbwe");
        foreach (var d in FixedDrives()) yield return Path.Combine(d, "XboxGames", "Minecraft for Windows", "Content", "Minecraft.Windows.exe");
    }

    // ---------------------------------------------------------------- the list
    public static List<GameDef> Builtin { get; } = new()
    {
        new GameDef
        {
            Id = "fortnite", Name = "Fortnite", Short = "FN", Genre = "Battle royale", ColorA = "#7B2FF7", ColorB = "#00C2FF",
            ProcessNames = new[] { "FortniteClient-Win64-Shipping" },
            Candidates = () => Epic("Fortnite", @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"),
            LaunchUri = "com.epicgames.launcher://apps/Fortnite?action=launch&silent=true",
            AntiCheat = "Easy Anti-Cheat / BattlEye",
            Tips = new[]
            {
                "Rendering Mode: 'Performance' for the highest FPS on low and mid PCs. Use DirectX 12 on RTX/RX 6000+ cards if you want better visuals.",
                "NVIDIA Reflex Low Latency: On + Boost. This is the real 'input delay reducer'.",
                "Frame Rate Limit: set it to your monitor's refresh rate (or Unlimited if your FPS is very stable).",
                "Turn off Record Replays: it saves your matches in the background, which costs FPS and disk.",
                "Matchmaking Region: set it manually to the lowest ping region. Use the Ping Lab in PC Optimizer to find it.",
                "View Distance Medium/Far, Shadows Off, Post Processing Low: competitive players do this for visibility and FPS.",
                "In Performance Mode, set 'Mesh Quality' to Low if you have a 4GB-or-smaller GPU.",
            }
        },
        new GameDef
        {
            Id = "valorant", Name = "VALORANT", Short = "VAL", Genre = "Tactical shooter", ColorA = "#FF4655", ColorB = "#7A1020",
            ProcessNames = new[] { "VALORANT-Win64-Shipping" },
            Candidates = () => OnDrives(@"Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"),
            LaunchCommand = () => RiotClient() is string rc ? $"\"{rc}\" --launch-product=valorant --launch-patchline=live" : null,
            AntiCheat = "Vanguard",
            Tips = new[]
            {
                "NVIDIA Reflex Low Latency: On + Boost.",
                "Multithreaded Rendering: On (almost always more FPS on 4+ core CPUs).",
                "Limit FPS Always: Off. VALORANT is CPU-heavy, and more FPS means less input lag.",
                "Material/Texture/Detail Quality: Low; Vignette Off; VSync Off; Anti-aliasing MSAA 2x at most.",
                "Turn off 'Improve Clarity' and 'Bloom' if you're below your refresh rate.",
                "Vanguard needs Secure Boot / TPM on Windows 11. PC Optimizer never touches those.",
            }
        },
        new GameDef
        {
            Id = "cs2", Name = "Counter-Strike 2", Short = "CS2", Genre = "Tactical shooter", ColorA = "#F5A623", ColorB = "#4A3000",
            ProcessNames = new[] { "cs2" },
            Candidates = () => Steam(@"Counter-Strike Global Offensive\game\bin\win64\cs2.exe"),
            LaunchUri = "steam://rungameid/730",
            AntiCheat = "VAC",
            Tips = new[]
            {
                "NVIDIA Reflex: Enabled + Boost.",
                "Boost Player Contrast: Enabled (better visibility, very small FPS cost).",
                "Shader detail Low, Particle detail Low, Ambient Occlusion Off, FSR Off (unless your GPU is very weak).",
                "Multisampling 2x/4x MSAA: CS2 looks bad without it. 4x if you have spare FPS.",
                "In console: 'fps_max 0' for uncapped FPS, or cap 3 below your refresh rate with G-SYNC.",
            }
        },
        new GameDef
        {
            Id = "apex", Name = "Apex Legends", Short = "APX", Genre = "Battle royale", ColorA = "#DA292A", ColorB = "#3A0A0A",
            ProcessNames = new[] { "r5apex_dx12", "r5apex" },
            Candidates = () => Steam(@"Apex Legends\r5apex_dx12.exe").Concat(Steam(@"Apex Legends\r5apex.exe"))
                .Concat(OnDrives(@"Program Files\EA Games\Apex\r5apex_dx12.exe")),
            LaunchUri = "steam://rungameid/1172470",
            AntiCheat = "Easy Anti-Cheat",
            Tips = new[]
            {
                "NVIDIA Reflex: Enabled + Boost.",
                "Launch option '+fps_max 0' (Steam > Properties) removes the built-in FPS cap. Or use your refresh rate.",
                "Model Detail Low, Effects Low, Ambient Occlusion Disabled, Volumetric Lighting Disabled, Dynamic Spot Shadows Disabled.",
                "Texture Streaming Budget: about half your GPU's VRAM.",
            }
        },
        new GameDef
        {
            Id = "cod", Name = "Call of Duty / Warzone", Short = "COD", Genre = "Shooter", ColorA = "#9BE564", ColorB = "#1E3A0F",
            ProcessNames = new[] { "cod", "BlackOps6", "ModernWarfare" },
            Candidates = () => Steam(@"Call of Duty HQ\cod.exe").Concat(OnDrives(@"Program Files (x86)\Call of Duty\_retail_\cod.exe"))
                .Concat(OnDrives(@"Program Files (x86)\Call of Duty\cod.exe")),
            LaunchUri = "steam://rungameid/1938090",
            AntiCheat = "RICOCHET",
            Tips = new[]
            {
                "NVIDIA Reflex: On + Boost.",
                "Upscaling: DLSS Quality/Balanced on RTX cards gives large FPS gains with little visual loss.",
                "On-demand Texture Streaming: Off (saves bandwidth and stutter).",
                "Shadow Quality Low, Screen Space Reflections Off, Ambient Occlusion Off, Particle Quality Low.",
                "VRAM Scale Target: 80-90% to prevent stutter.",
            }
        },
        new GameDef
        {
            Id = "rocketleague", Name = "Rocket League", Short = "RL", Genre = "Sports", ColorA = "#1E90FF", ColorB = "#FF8C00",
            ProcessNames = new[] { "RocketLeague" },
            Candidates = () => Epic("Rocket League", @"Binaries\Win64\RocketLeague.exe").Concat(Steam(@"rocketleague\Binaries\Win64\RocketLeague.exe")),
            LaunchUri = "com.epicgames.launcher://apps/Sugar?action=launch&silent=true",
            AntiCheat = "Easy Anti-Cheat",
            Tips = new[]
            {
                "Frame Rate: Uncapped (or your refresh rate). Higher FPS = more responsive car controls.",
                "Turn off Weather Effects, Dynamic Shadows, Motion Blur, Bloom and Light Shafts.",
                "World Detail 'Performance' and Render Quality 'High Performance' only if you're really struggling.",
            }
        },
        new GameDef
        {
            Id = "overwatch", Name = "Overwatch 2", Short = "OW2", Genre = "Hero shooter", ColorA = "#F99E1A", ColorB = "#40210A",
            ProcessNames = new[] { "Overwatch" },
            Candidates = () => OnDrives(@"Program Files (x86)\Overwatch\_retail_\Overwatch.exe").Concat(Steam(@"Overwatch\_retail_\Overwatch.exe")),
            LaunchUri = "steam://rungameid/2357570",
            AntiCheat = "Warden",
            Tips = new[]
            {
                "NVIDIA Reflex: Enabled + Boost.",
                "Render Scale 100% (Custom), Dynamic Render Scale Off.",
                "Shadow Detail Low, Model Detail Low, Effects Low, Local Fog Low, Ambient Occlusion Off.",
                "Reduce Buffering: On (lower input lag).",
            }
        },
        new GameDef
        {
            Id = "marvelrivals", Name = "Marvel Rivals", Short = "MR", Genre = "Hero shooter", ColorA = "#E62429", ColorB = "#FFD700",
            ProcessNames = new[] { "Marvel-Win64-Shipping" },
            Candidates = () => Steam(@"MarvelRivals\MarvelGame\Marvel\Binaries\Win64\Marvel-Win64-Shipping.exe"),
            LaunchUri = "steam://rungameid/2767030",
            AntiCheat = "NetEase Anti-Cheat",
            Tips = new[]
            {
                "NVIDIA Reflex: On + Boost.",
                "Use DLSS / FSR / XeSS Quality: this Unreal Engine 5 game is very GPU-heavy.",
                "Global Illumination and Reflections are the biggest FPS costs. Set them to Low.",
            }
        },
        new GameDef
        {
            Id = "r6", Name = "Rainbow Six Siege", Short = "R6", Genre = "Tactical shooter", ColorA = "#C9D1D9", ColorB = "#2B3138",
            ProcessNames = new[] { "RainbowSix", "RainbowSix_Vulkan", "RainbowSix_DX12" },
            Candidates = () => Steam(@"Tom Clancy's Rainbow Six Siege\RainbowSix.exe"),
            LaunchUri = "steam://rungameid/359550",
            AntiCheat = "BattlEye",
            Tips = new[]
            {
                "Siege now runs on DirectX 12 only (Vulkan and DX11 were removed), so there's no API to pick. If DX12 stutters, let it rebuild its shader cache: the first few matches after an update are always worse.",
                "NVIDIA Reflex: On + Boost.",
                "Render Scaling 100, Texture Quality depends on VRAM, Lens Effects Off, Zoom-in DOF Off.",
            }
        },
        new GameDef
        {
            Id = "lol", Name = "League of Legends", Short = "LoL", Genre = "MOBA", ColorA = "#C8AA6E", ColorB = "#0A1428",
            ProcessNames = new[] { "League of Legends" },
            Candidates = () => OnDrives(@"Riot Games\League of Legends\Game\League of Legends.exe"),
            LaunchCommand = () => RiotClient() is string rc ? $"\"{rc}\" --launch-product=league_of_legends --launch-patchline=live" : null,
            AntiCheat = "Vanguard",
            Tips = new[]
            {
                "Frame Rate Cap: your monitor's refresh rate (Uncapped can cause the client to overheat laptops).",
                "Turn off 'Wait for vertical sync' and Anti-aliasing for the lowest input delay.",
                "Shadows Off is the biggest FPS gain on low-end PCs.",
            }
        },
        new GameDef
        {
            Id = "roblox", Name = "Roblox", Short = "RBX", Genre = "Platform", ColorA = "#E2231A", ColorB = "#393B3D",
            ProcessNames = new[] { "RobloxPlayerBeta" },
            Candidates = RobloxPaths,
            LaunchUri = "https://www.roblox.com/home",
            AntiCheat = "Hyperion (Byfron)",
            Tips = new[]
            {
                "Graphics Mode Manual, then lower Graphics Quality until it's smooth. Each step is a big change.",
                "Roblox's 'Maximum Frame Rate' setting is stuck at 60 FPS on 'Default'. Set it to 120, 144 or 240 (match your monitor). Press Shift+F5 in-game to see your real FPS.",
                "Don't use 'FPS unlocker' tools that inject into Roblox. They can get you banned under Hyperion. Use the in-game setting.",
            }
        },
        new GameDef
        {
            Id = "minecraft", Name = "Minecraft (Java)", Short = "MC", Genre = "Sandbox", ColorA = "#62B47A", ColorB = "#3B2A1A",
            ProcessNames = new[] { "javaw", "java" },
            Candidates = MinecraftJavaPaths,
            LaunchCommand = () => MinecraftLauncher() is string l ? $"\"{l}\"" : null,
            LaunchUri = "minecraft://",
            AntiCheat = "None (servers may have their own)",
            Tips = new[]
            {
                "Install Fabric + Sodium (+ Lithium, FerriteCore). Often 2-3x the FPS of vanilla. This is the real Minecraft FPS boost.",
                "Turn on 'Threaded optimization' in the NVIDIA tab (Minecraft Java uses OpenGL).",
                "Give it 4-6GB of RAM in the launcher (Installations > More options > -Xmx6G). More than 8GB usually makes it worse.",
                "Render distance is the biggest FPS setting: 8-12 chunks is the sweet spot. Simulation distance 6-8.",
                "Video Settings: Graphics 'Fast', Clouds Off, Smooth Lighting Off, Max Framerate = your refresh rate, Entity Distance 75%.",
            }
        },
        new GameDef
        {
            Id = "minecraft_bedrock", Name = "Minecraft (Bedrock)", Short = "MC", Genre = "Sandbox", ColorA = "#3C8527", ColorB = "#1E3A14",
            ProcessNames = new[] { "Minecraft.Windows", "Minecraft.Education", "Minecraft.WindowsPreview" },
            Candidates = MinecraftBedrockPaths,
            AllowFolder = true,
            LaunchUri = "minecraft:",
            AntiCheat = "None",
            Tips = new[]
            {
                "Video > Graphics Mode: 'Simple' for the most FPS ('Fancy' is a middle ground). 'Vibrant Visuals' is the new default on many PCs and is by far the heaviest; switch it off from the main menu.",
                "In Simple/Fancy options, turn off Beautiful Skies and Smooth Lighting. Render Distance 12-16 chunks is the sweet spot.",
                "Turn V-Sync off in Video settings to cut input lag.",
                "Turn off Ray Tracing unless you have an RTX/RX 7000+ card; it's very heavy.",
                "Bedrock is a Microsoft Store app, so Windows protects its files. GPU priority is set in Windows Settings > System > Display > Graphics instead.",
            }
        },
        new GameDef
        {
            Id = "gta5", Name = "GTA V", Short = "GTA", Genre = "Open world", ColorA = "#4CAF50", ColorB = "#0D2B12",
            ProcessNames = new[] { "GTA5", "GTA5_Enhanced" },
            Candidates = () => Steam(@"Grand Theft Auto V Enhanced\GTA5_Enhanced.exe").Concat(Steam(@"Grand Theft Auto V\GTA5.exe"))
                .Concat(OnDrives(@"Program Files\Rockstar Games\Grand Theft Auto V\GTA5.exe")),
            LaunchUri = "steam://rungameid/3240220",
            AntiCheat = "BattlEye (online)",
            Tips = new[]
            {
                "The Enhanced edition supports DLSS/FSR. Use it on RTX cards.",
                "Grass Quality and MSAA are the biggest FPS costs. Grass 'High' instead of 'Ultra' is a huge gain.",
                "Extended Distance Scaling and Extended Shadows Distance: keep them low.",
            }
        },
    };

    public static List<GameDef> Discovered { get; set; } = new();

    public static IEnumerable<GameDef> All => Builtin.Concat(Discovered).Concat(Store.Settings.CustomGames.Select(c => new GameDef
    {
        Id = c.Id, Name = c.Name, Short = MakeShort(c.Name), Genre = "Your game", IsCustom = true,
        ProcessNames = new[] { Path.GetFileNameWithoutExtension(c.ExePath) },
        Candidates = () => new[] { c.ExePath },
        Tips = new[]
        {
            "Look for NVIDIA Reflex / AMD Anti-Lag in the game's settings and turn it on.",
            "Shadows, volumetric fog/clouds, ambient occlusion and anti-aliasing are usually the biggest FPS costs.",
            "Use DLSS / FSR / XeSS 'Quality' if the game has it. You get large FPS gains for very little visual loss.",
        }
    }));

    static string MakeShort(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var s = parts.Length > 1 ? string.Concat(parts.Take(3).Select(p => char.ToUpperInvariant(p[0]))) : name[..Math.Min(3, name.Length)].ToUpperInvariant();
        return s;
    }

    public static string AddCustom(string exePath)
    {
        var id = "custom_" + Math.Abs(exePath.ToLowerInvariant().GetHashCode()).ToString("x");
        if (Store.Settings.CustomGames.All(c => c.Id != id))
        {
            var name = FileVersionInfo.GetVersionInfo(exePath).ProductName;
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exePath);
            Store.Settings.CustomGames.Add(new CustomGame { Id = id, Name = name.Trim(), ExePath = exePath });
            Store.SaveSettings();
        }
        return id;
    }

    public static void RemoveCustom(string id)
    {
        Store.Settings.CustomGames.RemoveAll(c => c.Id == id);
        Store.SaveSettings();
    }

    // ---------------------------------------------------------------- library discovery
    static readonly string[] Palette =
    {
        "#00B4D8|#3A0CA3", "#F72585|#4361EE", "#FF7A00|#7A1E00", "#2EC4B6|#06424A", "#8338EC|#2B0A5A",
        "#E63946|#3D0A10", "#06D6A0|#073B30", "#FFB703|#5A3A00", "#4CC9F0|#14385C", "#B5179E|#3A0838",
    };

    static readonly Regex JunkExe = new(@"(unins|setup|install|redist|vcredist|dxsetup|directx|crash|report|launcher|helper|easyanticheat|eac|battleye|be_service|prereq|dotnet|cef|notification|update|patch|benchmark|config|editor|server|dedicated|python|ffmpeg|7z|unitycrash|ue4prereq|uploader|overlay|webhelper)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex NotAGame = new(@"(redistributable|proton|steam linux runtime|steamvr|steamworks|wallpaper engine|soundtrack|dedicated server|sdk|benchmark|demo$|playtest)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Finds the game's main .exe: the biggest one that isn't an installer, crash reporter or anti-cheat.</summary>
    static string GuessExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            return Directory.EnumerateFiles(dir, "*.exe", opts)
                .Take(400)
                .Where(f => !JunkExe.IsMatch(Path.GetFileNameWithoutExtension(f)) && !f.Contains(@"\_CommonRedist\", StringComparison.OrdinalIgnoreCase)
                            && !f.Contains(@"\Redist\", StringComparison.OrdinalIgnoreCase) && !f.Contains(@"\Engine\Binaries\ThirdParty\", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => (f.FullName.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ? 1L << 40 : 0) + f.Length)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    static GameDef LibraryGame(string id, string name, string source, string exe, string launchUri)
    {
        var colors = Palette[Math.Abs(StableHash(name)) % Palette.Length].Split('|');
        return new GameDef
        {
            Id = id, Name = name, Short = MakeShort(name), Genre = source, ColorA = colors[0], ColorB = colors[1],
            ProcessNames = exe != null ? new[] { Path.GetFileNameWithoutExtension(exe) } : Array.Empty<string>(),
            Candidates = () => exe != null ? new[] { exe } : Array.Empty<string>(),
            LaunchUri = launchUri, IsCustom = false, IsDiscovered = true,
            Tips = GenericTips,
        };
    }

    static int StableHash(string s) { unchecked { int h = 23; foreach (var c in s) h = h * 31 + c; return h; } }

    static readonly string[] GenericTips =
    {
        "Look for NVIDIA Reflex / AMD Anti-Lag in the game's settings and turn it on.",
        "Shadows, volumetric fog/clouds, ambient occlusion and anti-aliasing are usually the biggest FPS costs.",
        "Use DLSS / FSR / XeSS 'Quality' if the game has it. You get large FPS gains for very little visual loss.",
        "Set the in-game FPS limit to your monitor's refresh rate (or 3 below it with G-SYNC/FreeSync), and turn V-Sync off.",
    };

    /// <summary>Every game installed through Steam or Epic that isn't already one of the built-in games.</summary>
    public static List<GameDef> DiscoverLibrary(ICollection<string> builtinExes)
    {
        var found = new List<GameDef>();
        var known = new HashSet<string>(builtinExes.Where(e => e != null).Select(e => Path.GetDirectoryName(e)), StringComparer.OrdinalIgnoreCase);
        bool InsideKnown(string dir) => known.Any(k => k.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || k.Equals(dir, StringComparison.OrdinalIgnoreCase));

        // Steam
        foreach (var lib in SteamLibraries())
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in SafeFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(acf);
                    string V(string key) => Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"").Groups[1].Value;
                    var appid = V("appid"); var name = V("name"); var dirName = V("installdir");
                    if (string.IsNullOrEmpty(name) || NotAGame.IsMatch(name) || appid == "228980") continue;
                    var dir = Path.Combine(apps, "common", dirName);
                    if (!Directory.Exists(dir) || InsideKnown(dir)) continue;
                    var exe = GuessExe(dir);
                    if (exe == null) continue;
                    found.Add(LibraryGame("steam_" + appid, name, "Steam", exe, "steam://rungameid/" + appid));
                }
                catch (Exception ex) { Store.Log($"Steam manifest {acf}: {ex.Message}"); }
            }
        }

        // Epic
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            foreach (var f in SafeFiles(dir, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var name = S("DisplayName"); var loc = S("InstallLocation"); var launch = S("LaunchExecutable"); var appName = S("AppName");
                    if (name == null || loc == null || !Directory.Exists(loc) || InsideKnown(loc) || NotAGame.IsMatch(name)) continue;
                    if (r.TryGetProperty("bIsApplication", out var isApp) && isApp.ValueKind == JsonValueKind.False) continue;
                    var exe = launch != null && File.Exists(Path.Combine(loc, launch)) ? Path.Combine(loc, launch) : GuessExe(loc);
                    if (exe == null) continue;
                    found.Add(LibraryGame("epic_" + appName, name, "Epic Games", exe, $"com.epicgames.launcher://apps/{appName}?action=launch&silent=true"));
                }
                catch (Exception ex) { Store.Log($"Epic manifest {f}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Store.Log("Epic scan failed: " + ex.Message); }

        return found.GroupBy(g => g.Id).Select(g => g.First()).ToList();
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    // ---------------------------------------------------------------- detection
    public static string FindExe(GameDef g)
    {
        if (Store.Settings.GameExePaths.TryGetValue(g.Id, out var saved) && File.Exists(saved)) return saved;
        foreach (var c in SafeCandidates(g))
            if (File.Exists(c) || (g.AllowFolder && Directory.Exists(c))) return c;
        // running right now? grab its real path
        foreach (var pn in g.ProcessNames)
            foreach (var p in SafeProcesses(pn))
            {
                var path = Native.ProcessPath(p.Id);
                if (path != null && (g.Id != "minecraft" || IsMinecraft(p))) return path;
            }
        if (g.Id == "minecraft" && MinecraftJavaWindowPid() is int mp && mp != 0 && Native.ProcessPath(mp) is string mpath) return mpath;
        return null;
    }

    static readonly Dictionary<int, bool> McCache = new();
    static readonly string[] McWords = { "minecraft", "lunar", "badlion", "feather", "tlauncher", "lwjgl", "fabric", "forge", "optifine", "prismlauncher", "multimc", "modrinth", "curseforge", "labymod", "atlauncher", "gdlauncher", "sklauncher", "polymc", "net.minecraft", "knot", "salwyrr", "duckky" };

    static bool IsMinecraft(Process p)
    {
        try
        {
            var t = p.MainWindowTitle;
            if (!string.IsNullOrEmpty(t) && McWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase))) return true;
        }
        catch { }
        lock (McCache) if (McCache.TryGetValue(p.Id, out var known)) return known;
        bool isMc = false;
        try
        {
            // Java processes don't say what they run in their name, so look at how they were launched.
            using var s = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {p.Id}");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                var cmd = o["CommandLine"]?.ToString() ?? "";
                isMc = McWords.Any(w => cmd.Contains(w, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { }
        lock (McCache) { if (McCache.Count > 200) McCache.Clear(); McCache[p.Id] = isMc; }
        return isMc;
    }

    static IEnumerable<string> SafeCandidates(GameDef g)
    {
        List<string> list;
        try { list = g.Candidates().ToList(); } catch (Exception ex) { Store.Log($"Candidates for {g.Id} failed: {ex.Message}"); list = new(); }
        return list;
    }

    static Process[] SafeProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); } catch { return Array.Empty<Process>(); }
    }

    // One cheap snapshot of running process names, shared by everything that asks "is a game running?"
    static HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    static long _namesAt;
    static readonly object NamesGate = new();

    public static HashSet<string> RunningNames()
    {
        lock (NamesGate)
        {
            if (Environment.TickCount64 - _namesAt < 2000) return _names;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] all;
            try { all = Process.GetProcesses(); } catch { all = Array.Empty<Process>(); }
            foreach (var p in all) { try { set.Add(p.ProcessName); } catch { } finally { p.Dispose(); } }
            _names = set; _namesAt = Environment.TickCount64;
            return set;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    static List<(int Pid, string Title, string Class)> _wins = new();
    static long _winsAt;
    /// <summary>Every visible, titled top-level window (pid + title). Cached for 2 seconds.</summary>
    static List<(int Pid, string Title, string Class)> VisibleWindows()
    {
        lock (NamesGate)
        {
            if (Environment.TickCount64 - _winsAt < 2000) return _wins;
            var list = new List<(int, string, string)>();
            var sb = new System.Text.StringBuilder(256);
            var cb = new System.Text.StringBuilder(128);
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                sb.Clear();
                if (GetWindowText(h, sb, 256) <= 0) return true;
                GetWindowThreadProcessId(h, out var pid);
                cb.Clear(); GetClassName(h, cb, 128);
                list.Add(((int)pid, sb.ToString(), cb.ToString()));
                return true;
            }, IntPtr.Zero);
            _wins = list; _winsAt = Environment.TickCount64;
            return list;
        }
    }

    // Minecraft Java window titles from the vanilla launcher and popular clients ("Minecraft 1.21.4", "Minecraft* 1.20.1 - Multiplayer",
    // "Lunar Client 1.8.9 (...)", "Badlion Minecraft Client", "Feather 1.21", "LabyMod 4"...). Launcher windows are excluded.
    static readonly System.Text.RegularExpressions.Regex McTitle = new(
        @"^(Minecraft\*?\s*\d|Minecraft\*?\s*-|Minecraft\*?$|Lunar Client\s*[\d(]|Badlion Minecraft Client|Feather\s*(Client\s*)?\d|LabyMod|Minecraft\s+(Forge|Fabric|NeoForge)|Duckky\s*(Client)?\s*[\d(-]|Salwyrr|Cheatbreaker|PvPLounge|Hypixel)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Pid of a Minecraft Java game window on screen, from any launcher or client, or 0.</summary>
    public static int MinecraftJavaWindowPid()
    {
        foreach (var (pid, title, cls) in VisibleWindows())
        {
            if (title.Contains("Launcher", StringComparison.OrdinalIgnoreCase) || title.Contains("Bedrock", StringComparison.OrdinalIgnoreCase)) continue;
            // Minecraft Java draws its window with LWJGL: "GLFW30" (1.13+) or "LWJGL" (1.8-1.12 clients like Lunar/Badlion 1.8.9).
            // That catches every launcher and client, even ones that rename Java or the window.
            bool lwjgl = cls.Equals("GLFW30", StringComparison.Ordinal) || cls.Equals("LWJGL", StringComparison.Ordinal);
            if (!lwjgl && !McTitle.IsMatch(title)) continue;
            if (cls is "ApplicationFrameWindow" or "Windows.UI.Core.CoreWindow") continue; // Bedrock (Store app) frame
            string name = null;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { continue; }
            if (name.Equals("Minecraft.Windows", StringComparison.OrdinalIgnoreCase)) continue; // that's Bedrock
            if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) || name.Contains("chrome", StringComparison.OrdinalIgnoreCase)
                || name.Contains("msedge", StringComparison.OrdinalIgnoreCase) || name.Contains("firefox", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Discord", StringComparison.OrdinalIgnoreCase)) continue; // a browser tab or folder named "Minecraft"
            return pid;
        }
        return 0;
    }

    /// <summary>Running AND has a visible window (not just sitting in the tray / background).</summary>
    public static bool IsOnScreen(GameDef g)
    {
        // Bedrock from the Microsoft Store draws inside an ApplicationFrameHost window, not its own process window.
        if (g.Id == "minecraft_bedrock" && IsRunning(g)
            && VisibleWindows().Any(w => w.Title.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase) && w.Class is "ApplicationFrameWindow" or "Windows.UI.Core.CoreWindow"))
            return true;
        if (g.Id == "minecraft" && MinecraftJavaWindowPid() != 0) return true;
        if (!IsRunning(g)) return false;
        foreach (var n in g.ProcessNames)
            foreach (var p in SafeProcesses(n))
            {
                try { if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle) && (g.Id != "minecraft" || IsMinecraft(p))) return true; }
                catch { }
            }
        return false;
    }

    public static bool IsRunning(GameDef g)
    {
        var names = RunningNames();
        foreach (var n in g.ProcessNames)
        {
            if (!names.Contains(n)) continue;
            if (g.Id != "minecraft") return true;
            if (SafeProcesses(n).Any(IsMinecraft)) return true; // javaw is shared with other Java apps: check the window title
        }
        return g.Id == "minecraft" && MinecraftJavaWindowPid() != 0; // clients that bundle a renamed Java runtime
    }

    public static void Launch(GameDef g, string exe)
    {
        var cmd = g.LaunchCommand?.Invoke();
        if (cmd != null)
        {
            var exeEnd = cmd.IndexOf('"', 1);
            Process.Start(new ProcessStartInfo(cmd[1..exeEnd], cmd[(exeEnd + 1)..].Trim()) { UseShellExecute = true });
            return;
        }
        if (g.LaunchUri != null) { Shell.Open(g.LaunchUri); return; }
        if (exe != null) { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); return; }
        throw new InvalidOperationException("PC Optimizer doesn't know how to launch this game yet. Locate its .exe first.");
    }

    /// <summary>Tips matched to the user's GPU: NVIDIA-only tips are hidden on AMD and vice versa.</summary>
    public static List<string> TipsFor(GameDef g)
    {
        bool nv = SysInfo.ShowNvidia, amd = SysInfo.ShowAmd;
        var list = new List<string>();
        if (LowEnd.IsLowEndPc)
        {
            list.Add("Low-end PC: set the game's render resolution / resolution scale to 75-85%, or use FSR (Quality). This gives 20-40% more FPS, more than any Windows tweak.");
            list.Add("Low-end PC: Shadows, Effects and Post-processing on Low are the biggest FPS costs. Textures can stay Medium if you have 4 GB+ VRAM.");
            if (SysInfo.RamGb <= 8.5) list.Add("Only 8 GB RAM: close your browser before playing. Chrome/Edge often use 1-3 GB, which forces the game to stutter.");
        }
        foreach (var t in g.Tips)
        {
            bool nvOnly = t.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || (t.Contains("DLSS", StringComparison.Ordinal) && !t.Contains("FSR"));
            if (nvOnly && !nv)
            {
                if (amd && t.Contains("Reflex", StringComparison.OrdinalIgnoreCase))
                    list.Add("AMD: turn on Anti-Lag (or Anti-Lag 2 if the game supports it) in AMD Software or the game's settings. It's AMD's version of Reflex.");
                else if (amd && t.Contains("DLSS"))
                    list.Add("Use AMD FSR 'Quality' upscaling for a big FPS gain with little visual loss.");
                continue;
            }
            list.Add(t.Replace("RTX/RX 6000+", nv ? "RTX" : amd ? "RX 6000+" : "RTX/RX 6000+")
                      .Replace("DLSS / FSR", nv ? "DLSS" : amd ? "FSR" : "DLSS / FSR"));
        }
        return list.Distinct().ToList();
    }

    // ---------------------------------------------------------------- per-game tweaks
    const string GpuPrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    const string FsoFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public static List<Tweak> TweaksFor(GameDef g, Func<string> exe)
    {
        bool Has() { var e = exe(); return e != null && e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(e); }
        return new List<Tweak>
        {
            Tweak.Registry($"game.{g.Id}.gpu", "Run on your best GPU",
                "Forces the game to use your dedicated graphics card (NVIDIA/AMD) instead of the weak one built into the CPU. On gaming laptops this can be a huge FPS difference. On desktops with one GPU it does nothing.",
                Impact.High,
                () => !Has() ? Array.Empty<RegSet>()
                    : g.Id == "minecraft"
                        ? AllMinecraftJava().Append(exe()).Where(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(p).StartsWith("java", StringComparison.OrdinalIgnoreCase))
                              .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => RegSet.HKCU(GpuPrefKey, p, "GpuPreference=2;", RegistryValueKind.String)).ToArray()
                        : new[] { RegSet.HKCU(GpuPrefKey, exe(), "GpuPreference=2;", RegistryValueKind.String) },
                recommended: true, available: Has),

            new Tweak
            {
                Id = $"game.{g.Id}.defender",
                Title = "Skip antivirus scanning of game files",
                Description = "Windows Defender scans every file a game reads, including textures and shaders it loads in the middle of a match. That causes hitches and slower loading in big games. This excludes only this game's own folder from real-time scanning. The rest of your PC stays protected.",
                Impact = Impact.Situational,
                Warning = "Only do this for games from official stores (Steam, Epic, Riot, Battle.net...). Files in that folder won't be scanned while they run.",
                IsAvailable = () => Has() && GameFolder(exe()) != null && DefenderAvailable(),
                IsApplied = () => Has() && GameFolder(exe()) is string f && DefenderExclusions().Contains(f, StringComparer.OrdinalIgnoreCase),
                Apply = () =>
                {
                    var f = GameFolder(exe()) ?? throw new InvalidOperationException("Game folder not found.");
                    var r = Shell.Run("powershell", $"-NoProfile -NonInteractive -Command \"Add-MpPreference -ExclusionPath '{f.Replace("'", "''")}'\"", 30000);
                    _defCache = null;
                    if (r.Code != 0) throw new InvalidOperationException("Windows Security refused the change. If another antivirus is installed, add the exclusion there instead.");
                    Store.Backups[$"game.{g.Id}.defender"] = new TweakBackup { Extra = { ["folder"] = f } }; Store.SaveBackups();
                },
                Revert = () =>
                {
                    var f = Store.Backups.TryGetValue($"game.{g.Id}.defender", out var b) ? b.Extra.GetValueOrDefault("folder") : GameFolder(exe());
                    if (f != null) Shell.Run("powershell", $"-NoProfile -NonInteractive -Command \"Remove-MpPreference -ExclusionPath '{f.Replace("'", "''")}'\"", 30000);
                    _defCache = null;
                    Store.Backups.Remove($"game.{g.Id}.defender"); Store.SaveBackups();
                },
            },

            Tweak.Registry($"game.{g.Id}.qos", "Network priority (game traffic first)",
                "Tags the game's network packets with the highest priority class (DSCP 46, the same tag used for voice calls). Routers with QoS / 'gaming priority' send them first, so a download or a Netflix stream on your network causes fewer ping spikes. This is the same idea as ExitLag's 'traffic shaper', built into Windows.",
                Impact.Situational,
                () => Has() ? QosSets(g, exe()) : Array.Empty<RegSet>(),
                recommended: true, available: Has, after: () => Shell.Run("gpupdate", "/target:computer /force", 90000),
                warning: "Only helps if your router respects QoS tags (most gaming routers do, some ISP routers ignore them). It never makes things worse."),

        };
    }

    /// <summary>The game's install root: the folder under steamapps\common, Epic Games, Riot Games etc., not just the exe's own folder.</summary>
    static string GameFolder(string exe)
    {
        if (exe == null) return null;
        var dir = Path.GetDirectoryName(exe);
        var markers = new[] { @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\XboxGames\", @"\EA Games\", @"\Ubisoft Game Launcher\games\" };
        foreach (var m in markers)
        {
            int i = dir.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var rest = dir[(i + m.Length)..];
                var first = rest.Split('\\')[0];
                if (first.Length > 0) return dir[..(i + m.Length)] + first;
            }
        }
        // never exclude a whole drive or Program Files
        if (dir.Length <= 3 || dir.TrimEnd('\\').EndsWith("Program Files", StringComparison.OrdinalIgnoreCase) || dir.TrimEnd('\\').EndsWith("Program Files (x86)", StringComparison.OrdinalIgnoreCase)) return null;
        return dir;
    }

    static string[] _defCache; static DateTime _defAt;
    static string[] DefenderExclusions()
    {
        if (_defCache != null && (DateTime.Now - _defAt).TotalSeconds < 60) return _defCache;
        var r = Shell.Run("powershell", "-NoProfile -NonInteractive -Command \"(Get-MpPreference).ExclusionPath\"", 20000);
        _defCache = r.Output.Split('\n').Select(l => l.Trim().TrimEnd('\\')).Where(l => l.Length > 0).ToArray();
        _defAt = DateTime.Now;
        return _defCache;
    }
    static bool? _defOk;
    static bool DefenderAvailable() => _defOk ??= Shell.Run("powershell", "-NoProfile -NonInteractive -Command \"(Get-MpComputerStatus).RealTimeProtectionEnabled\"", 20000).Output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);

    static RegSet[] QosSets(GameDef g, string exe)
    {
        // Minecraft Java runs inside javaw.exe/java.exe whatever client launched it
        if (g.Id == "minecraft")
            return QosPolicy("PC Optimizer minecraft", "javaw.exe").Concat(QosPolicy("PC Optimizer minecraft java", "java.exe")).Distinct().ToArray();
        return QosPolicy($"PC Optimizer {g.Id}", Path.GetFileName(exe));
    }

    static RegSet[] QosPolicy(string name, string app)
    {
        var key = $@"SOFTWARE\Policies\Microsoft\Windows\QoS\{name}";
        RegSet S(string n, string v) => RegSet.HKLM(key, n, v, RegistryValueKind.String);
        return new[]
        {
            S("Version", "1.0"), S("Application Name", app), S("Protocol", "*"),
            S("Local Port", "*"), S("Local IP", "*"), S("Local IP Prefix Length", "*"),
            S("Remote Port", "*"), S("Remote IP", "*"), S("Remote IP Prefix Length", "*"),
            S("DSCP Value", "46"), S("Throttle Rate", "-1"),
            RegSet.HKLM(@"SYSTEM\CurrentControlSet\Services\Tcpip\QoS", "Do not use NLA", "1", RegistryValueKind.String), // lets QoS work off company networks
        };
    }

    static string FsoValue(string exe)
    {
        var cur = Reg.Get(RegistryHive.CurrentUser, LayersKey, exe)?.ToString();
        if (string.IsNullOrWhiteSpace(cur)) return "~ " + FsoFlag;
        if (cur.Contains(FsoFlag, StringComparison.OrdinalIgnoreCase)) return cur;
        return cur.TrimStart().StartsWith("~") ? cur.TrimEnd() + " " + FsoFlag : "~ " + cur.Trim() + " " + FsoFlag;
    }
}
