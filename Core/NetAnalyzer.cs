using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace Overdrive.Core;

/// <summary>
/// Network Analyzer: traces every hop between you and a game server region, then pings each hop over and
/// over to find exactly where lag, jitter or packet loss starts: your Wi-Fi/router, your ISP or the wider
/// internet. Addresses are never shown (privacy); hops are labelled by what they are.
/// </summary>
public static class NetAnalyzer
{
    public enum Zone { Router, Isp, Internet, Server }

    public class Hop
    {
        public int Number;
        public Zone Zone;
        internal IPAddress Address;            // never shown in the UI
        public readonly List<double> Rtts = new();
        public int Sent;
        public double Avg => Rtts.Count == 0 ? 0 : Rtts.Average();
        public double Min => Rtts.Count == 0 ? 0 : Rtts.Min();
        public double Jitter => Rtts.Count < 2 ? 0 : Rtts.Zip(Rtts.Skip(1), (a, b) => Math.Abs(a - b)).Average();
        public double Loss => Sent == 0 ? 0 : 100.0 * (Sent - Rtts.Count) / Sent;
        public bool Silent => Rtts.Count == 0; // routers that ignore pings (normal)
        public string Label => Zone switch
        {
            Zone.Router => "Your router / Wi-Fi",
            Zone.Isp => "Your internet provider",
            Zone.Internet => "Internet backbone",
            _ => "Game server region",
        };
    }

    public record Finding(string Where, string Problem, string Fix, bool Bad);
    public record Result(string Region, List<Hop> Hops, List<Finding> Findings, double FinalPing, double FinalJitter, double FinalLoss);

    static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31
               || b[0] == 100 && b[1] >= 64 && b[1] <= 127 || b[0] == 169 && b[1] == 254;
    }

    public static async Task<Result> RunAsync(NetLab.Region region, int rounds, IProgress<(double Pct, string Text)> progress, CancellationToken ct)
    {
        var addrs = await Dns.GetHostAddressesAsync(region.Host, ct);
        var target = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? throw new Exception("Couldn't reach the server region.");

        // 1) discover the route
        progress?.Report((0, "Finding the route to the server..."));
        var hops = new List<Hop>();
        int silentRun = 0;
        for (int ttl = 1; ttl <= 30 && !ct.IsCancellationRequested; ttl++)
        {
            IPAddress found = null; bool reached = false;
            for (int t = 0; t < 2 && found == null; t++)
            {
                try
                {
                    using var ping = new Ping();
                    var r = await ping.SendPingAsync(target, 1000, new byte[32], new PingOptions(ttl, true));
                    if (r.Status is IPStatus.TtlExpired or IPStatus.Success) { found = r.Address; reached = r.Status == IPStatus.Success; }
                }
                catch { }
            }
            hops.Add(new Hop { Number = ttl, Address = found });
            if (reached) break;
            silentRun = found == null ? silentRun + 1 : 0;
            if (silentRun >= 4) { hops.RemoveRange(hops.Count - 4, 4); break; } // the rest of the path ignores pings
            progress?.Report((Math.Min(0.2, ttl / 30.0 * 0.4), $"Finding the route... hop {ttl}"));
        }

        // label each hop
        bool seenPublic = false; int publicCount = 0;
        for (int i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            if (i == 0) { h.Zone = Zone.Router; continue; }
            if (h.Address != null && !IsPrivate(h.Address)) { seenPublic = true; publicCount++; }
            h.Zone = !seenPublic || publicCount <= 3 ? Zone.Isp : Zone.Internet;
        }
        var server = new Hop { Number = hops.Count + 1, Zone = Zone.Server };
        if (hops.Count > 0 && hops[^1].Address != null && hops[^1].Address.Equals(target)) hops.RemoveAt(hops.Count - 1);

        // 2) ping every hop + the server (TCP, which game servers answer) at the same time, many rounds
        for (int round = 0; round < rounds && !ct.IsCancellationRequested; round++)
        {
            var tasks = hops.Where(h => h.Address != null).Select(async h =>
            {
                h.Sent++;
                try
                {
                    using var p = new Ping();
                    var r = await p.SendPingAsync(h.Address, 1000);
                    if (r.Status == IPStatus.Success) lock (h.Rtts) h.Rtts.Add(r.RoundtripTime);
                }
                catch { }
            }).ToList();
            tasks.Add(Task.Run(async () =>
            {
                server.Sent++;
                var m = await NetLab.MeasureTcp(region.Host, 1, ct);
                if (m?.AvgMs is double ms) lock (server.Rtts) server.Rtts.Add(ms);
            }));
            await Task.WhenAll(tasks);
            progress?.Report((0.2 + 0.8 * (round + 1) / rounds, $"Testing every hop... {round + 1}/{rounds}"));
            await Task.Delay(400, ct);
        }
        ct.ThrowIfCancellationRequested();
        var all = hops.Where(h => h.Address != null).Append(server).ToList();
        for (int i = 0; i < all.Count; i++) all[i].Number = i + 1;
        return new Result(region.Name, all, Analyze(all), server.Avg, server.Jitter, server.Loss);
    }

    static List<Finding> Analyze(List<Hop> hops)
    {
        var f = new List<Finding>();
        var router = hops.FirstOrDefault(h => h.Zone == Zone.Router);
        var server = hops[^1];
        bool wifi = false; try { wifi = HwMonitor.GetConnection()?.Type?.Contains("Wi", StringComparison.OrdinalIgnoreCase) == true; } catch { }

        if (router != null && !router.Silent)
        {
            if (router.Loss >= 2 || router.Jitter > 6 || router.Avg > 8)
                f.Add(new("Your home network", $"Your connection to the router is unstable ({router.Avg:0} ms, {router.Jitter:0} ms jitter, {router.Loss:0}% loss). Every game packet starts with this delay.",
                    wifi ? "This is your Wi-Fi. Use an Ethernet cable if you can, move closer to the router, or switch to 5 GHz. Turn on 'Pause Wi-Fi scans in games'." : "Restart the router and check the cable. If other people are streaming or downloading, Traffic Shaper and router QoS help.", true));
        }

        // Real packet loss = loss that starts at a hop and continues all the way to the server.
        // Loss at one middle hop only is normal: routers answer pings last.
        if (server.Loss >= 2)
        {
            Hop start = null;
            for (int i = hops.Count - 2; i >= 0; i--) { if (hops[i].Silent) continue; if (hops[i].Loss >= server.Loss * 0.5 && hops[i].Loss >= 2) start = hops[i]; else break; }
            var where = start?.Zone ?? Zone.Server;
            f.Add(new(start == null ? "Server side" : start.Label, $"{server.Loss:0}% of packets to the server are lost{(start != null ? $", starting at hop {start.Number}" : "")}. Lost packets cause rubber-banding and shots that don't register.",
                where switch
                {
                    Zone.Router => "Fix your Wi-Fi/router first (see above).",
                    Zone.Isp => "This is inside your internet provider's network. Restart your modem; if it keeps happening, contact your ISP and tell them where the loss starts.",
                    _ => "This is past your provider, so you can't fix it directly. Try another server region in-game.",
                }, true));
        }

        // Where does the biggest latency jump happen?
        var answered = hops.Where(h => !h.Silent).ToList();
        if (answered.Count >= 2)
        {
            double best = 0; Hop jumpAt = null; double prevMin = 0;
            foreach (var h in answered) { double d = h.Min - prevMin; if (d > best && h.Min <= server.Min + 5) { best = d; jumpAt = h; } prevMin = Math.Max(prevMin, h.Min); }
            if (jumpAt != null && best >= 25)
                f.Add(new(jumpAt.Label, $"The biggest delay ({best:0} ms) is added at hop {jumpAt.Number} ({jumpAt.Label.ToLower()}).",
                    jumpAt.Zone switch
                    {
                        Zone.Router => "Your own network adds this. Use Ethernet and restart the router.",
                        Zone.Isp => "Your provider's network adds this. It's often distance to their nearest hub. A different ISP or plan may route better.",
                        _ => "That's mostly physical distance to the server. Pick the closest server region in-game (see Ping Lab).",
                    }, best >= 60));
        }

        if (server.Jitter > 10 && f.All(x => !x.Bad))
            f.Add(new("Whole route", $"Your ping to the server jumps around a lot ({server.Jitter:0} ms jitter).", "Check that nothing is downloading (Traffic Shaper does this during games) and run the speed test to check bufferbloat.", true));

        if (f.Count == 0)
            f.Add(new("Everything", $"Your route looks healthy: {server.Avg:0} ms to the server with {server.Jitter:0} ms jitter and no real packet loss.", "Nothing to fix. If games still lag, it's the game server itself.", false));
        return f;
    }
}
