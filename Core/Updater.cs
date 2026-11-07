using System.Threading;
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

    public record Release(Version Version, string Tag, string Notes, string AssetUrl, string AssetName, long Size) { public long Id { get; init; } }

    /// <summary>When this build was made (from the +yyyyMMddHHmmss build stamp).</summary>
    static DateTime? BuildTimeUtc
    {
        get
        {
            var stamp = App.BuildId.Split('+').LastOrDefault();
            return DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
        }
    }

    /// <summary>"2.4.1" when the file name has a version, else the release name.</summary>
    public static string Label(Release r) => r.Version > new Version(0, 0) ? r.Version.ToString() : r.Tag;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static Updater() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("PC-Optimizer-Updater/1.0"); NoRedirect.DefaultRequestHeaders.UserAgent.ParseAdd("PC-Optimizer-Updater/1.0"); }

    public static Version Current => Version.TryParse(App.VersionText, out var v) ? v : new Version(0, 0, 0);
    public static Release Available { get; private set; }
    /// <summary>What the last check saw on GitHub, for the Settings page ("Latest on GitHub: ...").</summary>
    public static string LastSeen { get; private set; }
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
    static readonly HttpClient NoRedirect = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    static long StableId(string tag)
    {
        unchecked { long h = (long)14695981039346656037UL; foreach (char c in tag) { h ^= c; h *= 1099511628211L; } return h & long.MaxValue; }
    }

    /// <summary>
    /// Reads the latest release from github.com's normal release pages (not the API), which have no
    /// 60-per-hour limit. That's what lets every app check every 30 seconds.
    /// </summary>
    public static async Task<Release> CheckAsync()
    {
        // 1) /releases/latest redirects to /releases/tag/<tag>
        using var r1 = await NoRedirect.GetAsync($"https://github.com/{Owner}/{Repo}/releases/latest");
        var loc = r1.Headers.Location?.ToString() ?? "";
        int ti = loc.IndexOf("/releases/tag/", StringComparison.Ordinal);
        if (ti < 0)
        {
            if ((int)r1.StatusCode == 404 || loc.EndsWith("/releases", StringComparison.Ordinal)) throw new Exception("No releases published yet.");
            throw new Exception($"GitHub answered {(int)r1.StatusCode}. Try again in a minute.");
        }
        string tag = Uri.UnescapeDataString(loc[(ti + "/releases/tag/".Length)..]);

        // 2) the release's file list
        var html = await Http.GetStringAsync($"https://github.com/{Owner}/{Repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}");
        var links = System.Text.RegularExpressions.Regex.Matches(html, @"href=""(/[^""]+/releases/download/[^""]+\.(?:exe|zip))""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).Distinct().ToList();
        string pickPath = links.FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ?? links.FirstOrDefault();
        if (pickPath == null) throw new Exception("The latest release has no .exe or .zip attached.");
        string assetName = Uri.UnescapeDataString(pickPath[(pickPath.LastIndexOf('/') + 1)..]);
        var dm = System.Text.RegularExpressions.Regex.Match(html, @"datetime=""([^""]+)""");
        DateTime published = dm.Success && DateTime.TryParse(dm.Groups[1].Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : DateTime.UtcNow;
        long id = StableId(tag + "|" + assetName);

        // The tag and title can be anything. Decide like this:
        // 1) already installed this exact release -> nothing to do
        // 2) the file name has a version (PC-Optimizer-v2.4.1.zip) -> compare versions
        // 3) otherwise -> newer if it was published after this build was made
        bool newer;
        var ver = FindVersion(assetName);
        if (id == Store.Settings.InstalledReleaseId) newer = false;
        else if (ver != null) newer = ver > Current;
        else newer = BuildTimeUtc is DateTime bt && published > bt.AddMinutes(10);
        if (!newer && id != Store.Settings.InstalledReleaseId) { Store.Settings.InstalledReleaseId = id; Store.SaveSettings(); } // remember: this one's not newer

        var rel = new Release(ver ?? new Version(0, 0), tag, "", "https://github.com" + pickPath, assetName, 0) { Id = id };
        Available = newer ? rel : null;
        LastSeen = $"Latest on GitHub: {assetName} (published {published.ToLocalTime():MMM d, h:mm tt})";
        if (newer || _lastLogged != id) Store.Log($"Update check: latest {assetName} ({tag}), newer={newer} (running {App.VersionText})");
        _lastLogged = id;
        return Available;
    }
    static long _lastLogged;

    /// <summary>Background check shortly after startup and then every 6 hours.</summary>
    public static void StartAutoCheck()
    {
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            while (true)
            {
                try
                {
                    if (Store.Settings.AutoUpdateCheck && await CheckAsync() is Release r)
                    {
                        Store.Log($"Update available: {r.Tag}");
                        if (Store.Settings.AutoInstallUpdates) await AutoInstall(r);
                        else Found?.Invoke();
                    }
                }
                catch (Exception ex) { Store.Log("Update check: " + ex.Message); await Task.Delay(TimeSpan.FromMinutes(2)); } // back off on errors
                await Task.Delay(TimeSpan.FromSeconds(Busy ? 60 : 30));
            }
        });
    }

    /// <summary>Downloads in the background right away, then installs as soon as no game is running.</summary>
    static bool Busy;
    static async Task AutoInstall(Release r)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var exe = await DownloadAsync(r, null);
            while (BoostMode.ActiveGame != null) await Task.Delay(TimeSpan.FromSeconds(15)); // never restart mid-match
            Tray.Notify("Updating PC Optimizer", $"Installing {Label(r)}. Back in a few seconds.");
            Launch(r, exe);
        }
        catch { Busy = false; throw; }
    }

    /// <summary>Downloads the release and starts it. The new version replaces this one and restarts the app.</summary>
    public static async Task InstallAsync(Release r, IProgress<double> progress)
    {
        var exe = await DownloadAsync(r, progress);
        Launch(r, exe);
    }

    static string _downloadedFor, _downloadedExe;

    /// <summary>Downloads (4 connections in parallel when the server allows it) and returns the path to the new .exe.</summary>
    public static async Task<string> DownloadAsync(Release r, IProgress<double> progress)
    {
        if (_downloadedFor == r.AssetUrl && File.Exists(_downloadedExe)) { progress?.Report(1); return _downloadedExe; }
        var dir = Path.Combine(Path.GetTempPath(), "PCOptimizer-update");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, r.AssetName);

        // GitHub redirects release downloads to its CDN; resolve once so every chunk goes straight there.
        using var head = await Http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
        head.EnsureSuccessStatusCode();
        var url = head.RequestMessage?.RequestUri?.ToString() ?? r.AssetUrl;
        long total = head.Content.Headers.ContentLength ?? r.Size;
        bool ranges = head.Headers.AcceptRanges.Contains("bytes") && total > 4_000_000;
        long done = 0;
        void Add(int n) { var d = Interlocked.Add(ref done, n); if (total > 0) progress?.Report(Math.Min(1, (double)d / total)); }

        if (!ranges)
        {
            await using var src = await head.Content.ReadAsStreamAsync();
            await using var dst = File.Create(file);
            var buf = new byte[1 << 17]; int n;
            while ((n = await src.ReadAsync(buf)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n)); Add(n); }
        }
        else
        {
            head.Dispose(); // don't read the first response; download in parts instead
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) fs.SetLength(total);
            const int parts = 4;
            long size = (total + parts - 1) / parts;
            await Task.WhenAll(Enumerable.Range(0, parts).Select(async i =>
            {
                long from = i * size, to = Math.Min(total, from + size) - 1;
                if (from > to) return;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                dst.Position = from;
                var buf = new byte[1 << 17]; int n;
                while ((n = await src.ReadAsync(buf)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n)); Add(n); }
            }));
            if (new FileInfo(file).Length != total) throw new Exception("The download didn't finish. Try again.");
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
        _downloadedFor = r.AssetUrl; _downloadedExe = exe;
        return exe;
    }

    /// <summary>Starts the new build and exits right away. The new build installs itself and opens.</summary>
    static void Launch(Release r, string exe)
    {
        Store.Log($"Starting update {r.Tag} from {exe}");
        Store.Settings.InstalledReleaseId = r.Id; Store.SaveSettings(); // so the new version doesn't offer this same release again
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }
}
