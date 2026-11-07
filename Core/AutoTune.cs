using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Overdrive.Core;

/// <summary>
/// Tries each big tweak on and off with a quick benchmark and keeps whichever state is faster on this PC.
/// Each benchmark runs in a fresh process so driver settings take effect for every run.
/// </summary>
public static class AutoTune
{
    public record Result(string Title, double Delta, bool Kept, bool FinalOn);

    public static bool Running { get; private set; }

    /// <summary>Tweaks that change instantly (no restart) and can change FPS either way depending on the PC.</summary>
    static readonly (string Id, bool Latency)[] Candidates =
    {
        ("win.powerplan", false), ("nv.power", false), ("win.cstates", true), ("nv.lowlatency", true),
    };

    public static async Task<List<Result>> RunAsync()
    {
        Running = true;
        var results = new List<Result>();
        try
        {
            var byId = new Dictionary<string, Tweak>();
            foreach (var t in Catalog.Visible) byId.TryAdd(t.Id, t);
            var vms = new List<(TweakVM Vm, bool Latency)>();
            foreach (var (id, lat) in Candidates)
            {
                if (!byId.TryGetValue(id, out var t) || t.NeedsReboot) continue;
                var vm = new TweakVM(t);
                try { await vm.Ready; } catch { continue; }
                if (vm.Available) vms.Add((vm, lat));
            }
            if (vms.Count == 0) return results;

            await RunChild();                    // warm-up (shader compile, GPU clocks), not counted
            double baseScore = await RunChild();

            foreach (var (vm, latency) in vms)
            {
                bool before = vm.IsOn;
                await vm.SetAsync(!before, quiet: true);
                double score;
                try { score = await RunChild(); }
                catch { await vm.SetAsync(before, quiet: true); throw; }
                double delta = (score - baseScore) / Math.Max(1, baseScore) * 100;
                // FPS tweaks must clearly help. Latency tweaks stay on unless they clearly cost FPS.
                bool turningOn = !before;
                bool keep = turningOn && latency ? delta > -1.5 : delta > 1.5;
                if (keep) baseScore = score;
                else await vm.SetAsync(before, quiet: true);
                results.Add(new Result(vm.Title, delta, keep, vm.IsOn));
                Store.Log($"Auto-tune: {vm.Tweak.Id} {(turningOn ? "on" : "off")} -> {delta:+0.0;-0.0}% ({(keep ? "kept" : "reverted")})");
            }
            return results;
        }
        finally { Running = false; }
    }

    static async Task<double> RunChild()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"pco-bench-{Guid.NewGuid():N}.json");
        using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"--bench quick --out \"{tmp}\"") { UseShellExecute = false })
                      ?? throw new Exception("couldn't start the benchmark");
        await p.WaitForExitAsync();
        if (!File.Exists(tmp)) throw new Exception("the benchmark was cancelled");
        try
        {
            var run = JsonSerializer.Deserialize<SimRun>(File.ReadAllText(tmp));
            if (run == null || run.Score <= 0) throw new Exception("the benchmark was cancelled");
            return run.Score;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Child-process entry: run one benchmark and write the result as JSON.</summary>
    public static void ChildMain(string[] args)
    {
        bool quick = args.Contains("quick");
        int i = Array.IndexOf(args, "--out");
        string outPath = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        try { SysInfo.Load(); } catch { }
        var (run, _) = Benchmark.RunAsync(quick).GetAwaiter().GetResult();
        if (run != null && outPath != null) File.WriteAllText(outPath, JsonSerializer.Serialize(run));
    }
}
