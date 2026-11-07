using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Overdrive.Core;

/// <summary>
/// Honest ping tools. No app can make the internet faster, but you CAN pick the closest
/// server region, find out if Wi-Fi is the problem, and stop things hogging your connection.
/// </summary>
public static class NetLab
{
    public record Region(string Name, string Where, string Host);

    // AWS regional endpoints. Fortnite, Apex, Rocket League, Marvel Rivals and many more host on AWS,
    // so these are a good proxy for "which region should I pick in matchmaking".
    public static readonly Region[] Regions =
    {
        new("NA East", "Virginia", "dynamodb.us-east-1.amazonaws.com"),
        new("NA East 2", "Ohio", "dynamodb.us-east-2.amazonaws.com"),
        new("NA West", "N. California", "dynamodb.us-west-1.amazonaws.com"),
        new("NA West 2", "Oregon", "dynamodb.us-west-2.amazonaws.com"),
        new("Canada", "Montreal", "dynamodb.ca-central-1.amazonaws.com"),
        new("Brazil", "Sao Paulo", "dynamodb.sa-east-1.amazonaws.com"),
        new("Europe", "Frankfurt", "dynamodb.eu-central-1.amazonaws.com"),
        new("Europe West", "London", "dynamodb.eu-west-2.amazonaws.com"),
        new("Middle East", "Bahrain", "dynamodb.me-south-1.amazonaws.com"),
        new("Asia", "Tokyo", "dynamodb.ap-northeast-1.amazonaws.com"),
        new("Asia SE", "Singapore", "dynamodb.ap-southeast-1.amazonaws.com"),
        new("India", "Mumbai", "dynamodb.ap-south-1.amazonaws.com"),
        new("Oceania", "Sydney", "dynamodb.ap-southeast-2.amazonaws.com"),
    };

    public record Measurement(double? AvgMs, double JitterMs, int Lost, int Sent);

    /// <summary>TCP handshake time to port 443. Works even where ICMP ping is blocked.</summary>
    public static async Task<Measurement> MeasureTcp(string host, int samples = 5, CancellationToken ct = default)
    {
        var times = new List<double>();
        int lost = 0;
        for (int i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                var sw = Stopwatch.StartNew();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(2000);
                await client.ConnectAsync(host, 443, cts.Token);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { lost++; }
            catch (SocketException) { lost++; }
            await Task.Delay(100, ct);
        }
        return Summarize(times, lost, samples);
    }

    /// <summary>ICMP ping burst, for jitter and packet loss on your own connection.</summary>
    public static async Task<Measurement> MeasureIcmp(string host, int samples, IProgress<int> progress = null, CancellationToken ct = default)
    {
        var times = new List<double>();
        int lost = 0;
        using var ping = new Ping();
        for (int i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await ping.SendPingAsync(host, 1500);
                if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime); else lost++;
            }
            catch { lost++; }
            progress?.Report(i + 1);
            await Task.Delay(150, ct);
        }
        return Summarize(times, lost, samples);
    }

    static Measurement Summarize(List<double> t, int lost, int sent)
    {
        if (t.Count == 0) return new Measurement(null, 0, lost, sent);
        // TCP connect is 1 round trip; drop the slowest sample (DNS/first-connection warmup)
        var use = t.Count > 2 ? t.OrderBy(x => x).Take(t.Count - 1).ToList() : t;
        double jitter = 0;
        for (int i = 1; i < t.Count; i++) jitter += Math.Abs(t[i] - t[i - 1]);
        jitter = t.Count > 1 ? jitter / (t.Count - 1) : 0;
        return new Measurement(use.Average(), jitter, lost, sent);
    }

    public static bool OnWifi()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                && !NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public record SpeedResult(double DownMbps, double UpMbps, double? PingMs, double JitterMs, double? LoadedDownPingMs, double? LoadedUpPingMs, DateTime At);
    public record SpeedProgress(string Phase, double CurrentMbps, double? LivePingMs, double Fraction);

    /// <summary>
    /// Real download/upload test against Cloudflare's speed test servers (speed.cloudflare.com),
    /// plus "ping under load" (bufferbloat): how much your ping rises while the connection is busy.
    /// </summary>
    public static async Task<SpeedResult> SpeedTest(IProgress<SpeedProgress> progress, CancellationToken ct = default)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PCOptimizer/1.2");

        progress?.Report(new("Ping", 0, null, 0.02));
        var idle = await MeasureTcp("speed.cloudflare.com", 8, ct);
        progress?.Report(new("Ping", 0, idle.AvgMs, 0.1));

        progress?.Report(new("Download", 0, null, 0.1));
        var (down, downPing) = await Measure(async (token, add) =>
        {
            using var resp = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=50000000", System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token);
            using var stream = await resp.Content.ReadAsStreamAsync(token);
            var buf = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buf, token)) > 0) add(n);
        }, 6, TimeSpan.FromSeconds(9), "Download", 0.1, 0.55, progress, ct);

        progress?.Report(new("Upload", 0, null, 0.55));
        var data = new byte[2_000_000];
        Random.Shared.NextBytes(data);
        var (up, upPing) = await Measure(async (token, add) =>
        {
            while (!token.IsCancellationRequested)
            {
                using var content = new System.Net.Http.ByteArrayContent(data);
                using var resp = await http.PostAsync("https://speed.cloudflare.com/__up", content, token);
                add(data.Length);
            }
        }, 4, TimeSpan.FromSeconds(9), "Upload", 0.55, 1.0, progress, ct);

        return new SpeedResult(down, up, idle.AvgMs, idle.JitterMs, downPing, upPing, DateTime.Now);
    }

    static async Task<(double Mbps, double? LoadedPing)> Measure(Func<CancellationToken, Action<long>, Task> worker, int streams, TimeSpan duration,
        string phase, double fracFrom, double fracTo, IProgress<SpeedProgress> progress, CancellationToken ct)
    {
        long bytes = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();
        long warmBytes = 0; double warmSec = 0; bool warmed = false;
        void Add(long n)
        {
            Interlocked.Add(ref bytes, n);
            if (!warmed && sw.Elapsed.TotalSeconds > 1.5) { warmed = true; warmBytes = Interlocked.Read(ref bytes); warmSec = sw.Elapsed.TotalSeconds; } // skip TCP slow-start
        }
        var tasks = Enumerable.Range(0, streams).Select(async _ =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await worker(cts.Token, Add); }
                catch (OperationCanceledException) { break; }
                catch { try { await Task.Delay(200, cts.Token); } catch { break; } }
            }
        }).ToList();

        // ping while the line is busy (bufferbloat) + live speed for the UI
        var pings = new List<double>();
        var pinger = Task.Run(async () =>
        {
            await Task.Delay(1500, cts.Token).ContinueWith(_ => { });
            while (!cts.IsCancellationRequested)
            {
                var m = await MeasureTcp("1.1.1.1", 1, CancellationToken.None);
                if (m.AvgMs.HasValue) lock (pings) pings.Add(m.AvgMs.Value);
                try { await Task.Delay(400, cts.Token); } catch { break; }
            }
        });
        long lastBytes = 0; double lastT = 0;
        while (sw.Elapsed < duration && !ct.IsCancellationRequested)
        {
            await Task.Delay(250, CancellationToken.None);
            double t = sw.Elapsed.TotalSeconds; long b = Interlocked.Read(ref bytes);
            double mbps = (b - lastBytes) * 8 / Math.Max(0.001, t - lastT) / 1_000_000;
            lastBytes = b; lastT = t;
            double? lp; lock (pings) lp = pings.Count > 0 ? pings[^1] : null;
            progress?.Report(new(phase, mbps, lp, fracFrom + (fracTo - fracFrom) * Math.Min(1, t / duration.TotalSeconds)));
        }
        cts.Cancel();
        try { await Task.WhenAll(tasks); } catch { }
        try { await pinger; } catch { }
        double secs = sw.Elapsed.TotalSeconds - warmSec;
        long counted = Interlocked.Read(ref bytes) - warmBytes;
        double? loaded; lock (pings) loaded = pings.Count > 0 ? pings.OrderBy(x => x).ElementAt(pings.Count / 2) : null; // median
        return (secs > 0 ? counted * 8 / secs / 1_000_000 : 0, loaded);
    }

    /// <summary>Bufferbloat grade from how much ping rises under load (same scale as common online tests).</summary>
    public static string BloatGrade(double? idle, double? loaded)
    {
        if (!idle.HasValue || !loaded.HasValue) return "?";
        double inc = Math.Max(0, loaded.Value - idle.Value);
        return inc < 5 ? "A+" : inc < 30 ? "A" : inc < 60 ? "B" : inc < 200 ? "C" : inc < 400 ? "D" : "F";
    }

    // ---------------- Wi-Fi details ----------------
    public record WifiInfo(string Ssid, int? SignalPct, string Band, int? Channel, double? RxMbps, double? TxMbps, string RadioType);

    public static WifiInfo GetWifi()
    {
        try
        {
            var o = Shell.Run("netsh", "wlan show interfaces", 5000).Output;
            if (!o.Contains("SSID", StringComparison.OrdinalIgnoreCase)) return null;
            string V(string key)
            {
                var m = System.Text.RegularExpressions.Regex.Match(o, @"^\s*" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*:\s*(.+)$",
                    System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }
            var state = V("State");
            if (state != null && !state.Contains("connected", StringComparison.OrdinalIgnoreCase)) return null;
            int? I(string s) => int.TryParse(new string((s ?? "").TakeWhile(char.IsDigit).ToArray()), out var v) ? v : null;
            double? D(string s) => double.TryParse((s ?? "").Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
            var ch = I(V("Channel"));
            var band = V("Band") ?? (ch.HasValue ? (ch <= 14 ? "2.4 GHz" : "5 GHz") : null);
            return new WifiInfo(V("SSID"), I(V("Signal")), band, ch, D(V("Receive rate (Mbps)")), D(V("Transmit rate (Mbps)")), V("Radio type"));
        }
        catch { return null; }
    }

    public static string FlushDns() => Shell.Run("ipconfig", "/flushdns").Output.Trim();

    public static string ResetNetworkStack()
    {
        var a = Shell.Run("netsh", "winsock reset").Output;
        var b = Shell.Run("netsh", "int ip reset").Output;
        var c = Shell.Run("ipconfig", "/flushdns").Output;
        Store.Log("Network reset:\n" + a + b + c);
        return "Network stack reset. Restart your PC to finish.";
    }
}
