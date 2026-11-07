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
    static Updater() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("PC-Optimizer-Updater/1.0"); }

    public static Version Current => Version.TryParse(App.VersionText, out var v) ? v : new Version(0, 0, 0);
    public static Release Available { get; private set; }
    static string _etag;
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
    public static async Task<Release> CheckAsync()
    {
        // Conditional request: if nothing changed GitHub answers 304, which doesn't count against its rate limit.
        // That's what lets the app check every few seconds.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        if (_etag != null) req.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        using var resp = await Http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotModified) return Available;
        if ((int)resp.StatusCode == 404) throw new Exception("No releases published yet.");
        resp.EnsureSuccessStatusCode();
        _etag = resp.Headers.ETag?.ToString();
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
        long id = root.GetProperty("id").GetInt64();
        DateTime published = root.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetDateTime().ToUniversalTime() : DateTime.MinValue;
        string assetName = asset.GetProperty("name").GetString();

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

        var rel = new Release(ver ?? new Version(0, 0), string.IsNullOrWhiteSpace(relName) ? tag : relName, notes, asset.GetProperty("browser_download_url").GetString(), assetName, asset.GetProperty("size").GetInt64()) { Id = id };
        Available = newer ? rel : null;
        LastSeen = $"Latest on GitHub: {assetName} (published {published.ToLocalTime():MMM d, h:mm tt})";
        Store.Log($"Update check: latest {assetName} id {id}, newer={newer} (running {App.VersionText})");
        return Available;
    }

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
                catch (Exception ex) { Store.Log("Update check: " + ex.Message); }
                await Task.Delay(TimeSpan.FromSeconds(Busy ? 60 : 10)); // unchanged checks are free (304), so poll fast
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
