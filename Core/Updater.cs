using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Overdrive.Core;

/// <summary>
/// In-app updates from GitHub Releases. Checks the repo's latest release, downloads the .exe (or a .zip with
/// the .exe inside) and starts it. The new build installs itself over this one (see SelfInstall) and restarts.
/// To publish an update: push a tag like v2.4.0 and the GitHub Action builds and attaches the .exe.
/// </summary>
public static class Updater
{
    // >>> Change these to your GitHub account / repository <<<
    public const string Owner = "Duckkyinc";
    public const string Repo = "pc-optimizer-";

    public record Release(Version Version, string Tag, string Notes, string AssetUrl, string AssetName, long Size);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static Updater() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("PC-Optimizer-Updater/1.0"); }

    public static Version Current => Version.TryParse(App.VersionText, out var v) ? v : new Version(0, 0, 0);
    public static Release Available { get; private set; }
    public static event Action Found;

    static Version ParseTag(string tag)
    {
        var t = (tag ?? "").Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (dash > 0) t = t[..dash];
        return Version.TryParse(t, out var v) ? v : null;
    }

    static Version FindVersion(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"(\d+\.\d+(\.\d+)?)");
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    /// <summary>Returns the latest release if it's newer than this build, otherwise null.</summary>
    public static async Task<Release> CheckAsync()
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        if ((int)resp.StatusCode == 404) throw new Exception("No releases published yet.");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString();
        string relName = root.TryGetProperty("name", out var rn) ? rn.GetString() : null;
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        JsonElement? pick = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
            if (pick == null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) pick = a;
        }
        if (pick == null) throw new Exception("The latest release has no .exe or .zip attached.");
        var asset = pick.Value;
        // Version comes from the tag (v2.4.0), or else the release title or the file name (PC-Optimizer-v2.4.0.zip).
        var ver = ParseTag(tag) ?? FindVersion(relName) ?? FindVersion(asset.GetProperty("name").GetString());
        if (ver == null) throw new Exception($"Couldn't find a version number in the release '{tag}'. Name the tag or the zip like v2.4.0.");
        var rel = new Release(ver, tag, notes, asset.GetProperty("browser_download_url").GetString(), asset.GetProperty("name").GetString(), asset.GetProperty("size").GetInt64());
        Available = ver > Current ? rel : null;
        return Available;
    }

    /// <summary>Background check shortly after startup and then every 6 hours.</summary>
    public static void StartAutoCheck()
    {
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            while (true)
            {
                try
                {
                    if (Store.Settings.AutoUpdateCheck && await CheckAsync() is Release r)
                    {
                        Store.Log($"Update available: {r.Tag}");
                        Found?.Invoke();
                    }
                }
                catch (Exception ex) { Store.Log("Update check: " + ex.Message); }
                await Task.Delay(TimeSpan.FromHours(6));
            }
        });
    }

    /// <summary>Downloads the release and starts it. The new version replaces this one and restarts the app.</summary>
    public static async Task InstallAsync(Release r, IProgress<double> progress)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PCOptimizer-update");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, r.AssetName);

        using (var resp = await Http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? r.Size;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(file);
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string exe = file;
        if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var outDir = Path.Combine(dir, "x");
            ZipFile.ExtractToDirectory(file, outDir);
            exe = Directory.GetFiles(outDir, "*.exe", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
                  ?? throw new Exception("The downloaded zip has no .exe inside.");
        }
        if (new FileInfo(exe).Length < 5_000_000) throw new Exception("The download looks incomplete. Try again.");

        Store.Log($"Starting update {r.Tag} from {exe}");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        // The new build closes this copy, installs itself and restarts. Exit anyway in case it can't signal us.
        await Task.Delay(8000);
        System.Windows.Application.Current?.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
    }
}
