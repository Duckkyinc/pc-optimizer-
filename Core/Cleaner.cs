using System.IO;

namespace Overdrive.Core;

public class CleanTarget : Observable
{
    public string Name { get; init; }
    public string Description { get; init; }
    public string[] Folders { get; init; }
    public bool IsRecycleBin { get; init; }

    bool _selected = true;
    public bool Selected { get => _selected; set => SetField(ref _selected, value); }

    long _bytes = -1;
    public long Bytes { get => _bytes; set { SetField(ref _bytes, value); Raise(nameof(SizeText)); } }
    public string SizeText => Bytes < 0 ? "—" : Cleaner.Format(Bytes);
}

public static class Cleaner
{
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static List<CleanTarget> Targets() => new()
    {
        new CleanTarget { Name = "Temporary files", Description = "Leftover files apps forgot to delete.",
            Folders = new[] { Path.GetTempPath(), Path.Combine(Win, "Temp") } },
        new CleanTarget { Name = "DirectX shader cache", Description = "Windows rebuilds this. Clear it after a driver update if games stutter.",
            Folders = new[] { Path.Combine(Local, "D3DSCache") }, Selected = false },
        new CleanTarget { Name = "NVIDIA shader cache", Description = "Old compiled shaders. Games re-compile them, so there may be some stutter the first match after.",
            Folders = new[] { Path.Combine(Local, "NVIDIA", "DXCache"), Path.Combine(Local, "NVIDIA", "GLCache"), Path.Combine(Local, "NVIDIA Corporation", "NV_Cache") }, Selected = false },
        new CleanTarget { Name = "AMD shader cache", Description = "Old compiled shaders. Games re-compile them, so there may be some stutter the first match after.",
            Folders = new[] { Path.Combine(Local, "AMD", "DxCache"), Path.Combine(Local, "AMD", "DxcCache"), Path.Combine(Local, "AMD", "GLCache"), Path.Combine(Local, "AMD", "VkCache") }, Selected = false },
        new CleanTarget { Name = "Windows Update downloads", Description = "Update installers Windows already used.",
            Folders = new[] { Path.Combine(Win, "SoftwareDistribution", "Download") } },
        new CleanTarget { Name = "Crash dumps & error reports", Description = "Crash logs nobody reads.",
            Folders = new[] { Path.Combine(Local, "CrashDumps"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportArchive") } },
        new CleanTarget { Name = "Recycle Bin", Description = "Empties the Recycle Bin.", Folders = Array.Empty<string>(), IsRecycleBin = true, Selected = false },
    };

    public static long Measure(CleanTarget t)
    {
        if (t.IsRecycleBin) return 0;
        long total = 0;
        foreach (var f in t.Folders.Where(Directory.Exists))
            total += DirSize(new DirectoryInfo(f));
        return total;
    }

    static long DirSize(DirectoryInfo d)
    {
        long size = 0;
        try
        {
            foreach (var f in d.EnumerateFiles()) { try { size += f.Length; } catch { } }
            foreach (var s in d.EnumerateDirectories())
            {
                if ((s.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never follow links
                size += DirSize(s);
            }
        }
        catch { }
        return size;
    }

    /// <summary>Deletes the contents (never the folder itself). Files in use are skipped.</summary>
    public static long Clean(CleanTarget t)
    {
        if (t.IsRecycleBin)
        {
            Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); // no confirm, no progress, no sound
            return 0;
        }
        long freed = 0;
        foreach (var f in t.Folders.Where(Directory.Exists))
            freed += CleanDir(new DirectoryInfo(f));
        Store.Log($"Cleaned {t.Name}: {Format(freed)}");
        return freed;
    }

    static long CleanDir(DirectoryInfo d)
    {
        long freed = 0;
        try
        {
            foreach (var f in d.EnumerateFiles())
            {
                try { var len = f.Length; f.Attributes = FileAttributes.Normal; f.Delete(); freed += len; } catch { }
            }
            foreach (var s in d.EnumerateDirectories())
            {
                if ((s.Attributes & FileAttributes.ReparsePoint) != 0) { try { s.Delete(false); } catch { } continue; }
                freed += CleanDir(s);
                try { s.Delete(false); } catch { }
            }
        }
        catch { }
        return freed;
    }

    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}
