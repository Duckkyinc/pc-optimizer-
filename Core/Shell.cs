using System.Diagnostics;
using System.Text;

namespace Overdrive.Core;

public static class Shell
{
    public record Result(int Code, string Output);

    public static Result Run(string file, string args, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi);
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return new Result(-1, "Timed out"); }
        var text = outTask.Result + errTask.Result;
        return new Result(p.ExitCode, text);
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Store.Log($"Open failed for {target}: {ex.Message}"); throw; }
    }
}
