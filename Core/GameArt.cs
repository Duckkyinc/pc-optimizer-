using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Overdrive.Core;

/// <summary>
/// Game artwork from files already on the user's PC: Steam's local library cache, or the game's own .exe icon.
/// Nothing is downloaded.
/// </summary>
public static class GameArt
{
    static readonly Dictionary<string, ImageSource> Cache = new();
    static readonly object Gate = new();

    public record Art(ImageSource Banner, ImageSource Icon);

    public static Art Get(GameDef g, string exe)
    {
        var key = g.Id + "|" + exe;
        ImageSource banner = null, icon = null;
        lock (Gate)
        {
            Cache.TryGetValue(key + "|b", out banner);
            Cache.TryGetValue(key + "|i", out icon);
            if (banner != null || icon != null) return new Art(banner, icon);
        }
        var appId = SteamAppId(g);
        if (appId != null) banner = LoadFile(SteamHeader(appId));
        if (banner == null && exe != null) icon = ExeIcon(exe);
        lock (Gate)
        {
            if (banner != null) Cache[key + "|b"] = banner;
            if (icon != null) Cache[key + "|i"] = icon;
        }
        return new Art(banner, icon);
    }

    static string SteamAppId(GameDef g)
    {
        var m = Regex.Match(g.Id ?? "", @"^steam_(\d+)$");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(g.LaunchUri ?? "", @"^steam://rungameid/(\d+)");
        if (!m.Success) return null;
        // only use Steam art if the game was actually found inside a Steam library
        return m.Groups[1].Value;
    }

    static string SteamRoot()
    {
        var p = Reg.Get(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")?.ToString()
                ?? Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")?.ToString();
        return p?.Replace('/', '\\');
    }

    static string SteamHeader(string appId)
    {
        try
        {
            var root = SteamRoot();
            if (root == null) return null;
            var cache = Path.Combine(root, "appcache", "librarycache");
            if (!Directory.Exists(cache)) return null;
            // old layout: 730_header.jpg   new layout: 730\header.jpg (sometimes inside a hashed subfolder)
            var flat = new[] { $"{appId}_header.jpg", $"{appId}_library_hero.jpg" }.Select(f => Path.Combine(cache, f)).FirstOrDefault(File.Exists);
            if (flat != null) return flat;
            var dir = Path.Combine(cache, appId);
            if (!Directory.Exists(dir)) return null;
            foreach (var name in new[] { "header.jpg", "library_header.jpg", "library_hero.jpg" })
            {
                var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    static ImageSource LoadFile(string path)
    {
        if (path == null) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 460;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);

    /// <summary>The exe's own icon at up to 256px (with transparency).</summary>
    static ImageSource ExeIcon(string exe)
    {
        if (!File.Exists(exe)) return null;
        foreach (var size in new[] { 256, 128, 64, 48 })
        {
            var icons = new IntPtr[1];
            var ids = new uint[1];
            try
            {
                if (PrivateExtractIcons(exe, 0, size, size, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero) continue;
                var src = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { }
            finally { if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]); }
        }
        return null;
    }
}
