using Microsoft.Win32;

namespace Overdrive.Core;

/// <summary>A single registry value a tweak wants to set.</summary>
public record RegSet(RegistryHive Hive, string Path, string Name, object Value,
                     RegistryValueKind Kind = RegistryValueKind.DWord, object Default = null)
{
    public static RegSet HKLM(string path, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord, object def = null)
        => new(RegistryHive.LocalMachine, path, name, value, kind, def);
    public static RegSet HKCU(string path, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord, object def = null)
        => new(RegistryHive.CurrentUser, path, name, value, kind, def);
}

public static class Reg
{
    static RegistryKey Base(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    public static object Get(RegistryHive hive, string path, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public static RegistryValueKind? GetKind(RegistryHive hive, string path, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path);
        if (k == null || k.GetValue(name) == null) return null;
        return k.GetValueKind(name);
    }

    public static void Set(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        using var b = Base(hive);
        using var k = b.CreateSubKey(path, true);
        k.SetValue(name, Normalize(value, kind), kind);
    }

    public static void Delete(RegistryHive hive, string path, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path, true);
        k?.DeleteValue(name, false);
    }

    public static string[] SubKeys(RegistryHive hive, string path)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path);
        return k?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public static string[] ValueNames(RegistryHive hive, string path)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path);
        return k?.GetValueNames() ?? Array.Empty<string>();
    }

    /// <summary>Registry DWORDs come back as int; we store uint-ish values. Make them comparable and writable.</summary>
    static object Normalize(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)Convert.ToUInt32(value is int i ? unchecked((uint)i) : value)),
        RegistryValueKind.QWord => unchecked((long)Convert.ToUInt64(value is long l ? unchecked((ulong)l) : value)),
        _ => value
    };

    public static bool Matches(RegSet s)
    {
        var cur = Get(s.Hive, s.Path, s.Name);
        if (cur == null) return false;
        return s.Kind switch
        {
            RegistryValueKind.DWord => unchecked((uint)Convert.ToInt32(cur)) == Convert.ToUInt32(s.Value is int i ? unchecked((uint)i) : s.Value),
            RegistryValueKind.QWord => unchecked((ulong)Convert.ToInt64(cur)) == Convert.ToUInt64(s.Value),
            RegistryValueKind.Binary => cur is byte[] a && s.Value is byte[] bb && a.SequenceEqual(bb),
            RegistryValueKind.MultiString => cur is string[] ma && s.Value is string[] mb && ma.SequenceEqual(mb),
            _ => string.Equals(cur.ToString(), s.Value?.ToString(), StringComparison.OrdinalIgnoreCase)
        };
    }

    // ---------- backup / restore ----------

    public static RegBackup Capture(RegistryHive hive, string path, string name)
    {
        var cur = Get(hive, path, name);
        var rb = new RegBackup { Hive = hive.ToString(), Path = path, Name = name, Existed = cur != null };
        if (cur != null)
        {
            var kind = GetKind(hive, path, name) ?? RegistryValueKind.String;
            rb.Kind = kind.ToString();
            rb.Data = kind switch
            {
                RegistryValueKind.Binary => Convert.ToBase64String((byte[])cur),
                RegistryValueKind.MultiString => string.Join("\0", (string[])cur),
                RegistryValueKind.DWord => unchecked((uint)Convert.ToInt32(cur)).ToString(),
                RegistryValueKind.QWord => unchecked((ulong)Convert.ToInt64(cur)).ToString(),
                _ => cur.ToString()
            };
        }
        return rb;
    }

    public static void Restore(RegBackup rb)
    {
        var hive = Enum.Parse<RegistryHive>(rb.Hive);
        if (!rb.Existed) { Delete(hive, rb.Path, rb.Name); return; }
        var kind = Enum.Parse<RegistryValueKind>(rb.Kind);
        object value = kind switch
        {
            RegistryValueKind.Binary => Convert.FromBase64String(rb.Data),
            RegistryValueKind.MultiString => rb.Data.Split('\0'),
            RegistryValueKind.DWord => uint.Parse(rb.Data),
            RegistryValueKind.QWord => ulong.Parse(rb.Data),
            _ => rb.Data
        };
        Set(hive, rb.Path, rb.Name, value, kind);
    }

    /// <summary>Apply a set of values, recording originals under the tweak id (only the first time).</summary>
    public static void ApplyWithBackup(string tweakId, IEnumerable<RegSet> sets)
    {
        var list = sets.ToList();
        if (!Store.Backups.TryGetValue(tweakId, out var bk))
        {
            bk = new TweakBackup();
            Store.Backups[tweakId] = bk;
        }
        foreach (var s in list)
        {
            if (!bk.Registry.Any(r => r.Hive == s.Hive.ToString() && r.Path.Equals(s.Path, StringComparison.OrdinalIgnoreCase) && r.Name == s.Name))
                bk.Registry.Add(Capture(s.Hive, s.Path, s.Name));
        }
        Store.SaveBackups(); // save originals BEFORE changing anything
        foreach (var s in list) Set(s.Hive, s.Path, s.Name, s.Value, s.Kind);
        Store.Log($"Applied {tweakId}");
    }

    /// <summary>Undo a tweak: restore from backup if we have one, otherwise fall back to Windows defaults.</summary>
    /// <summary>Writes the Windows default for every value (delete when the default is "not set") and drops the backup.</summary>
    public static void ResetToDefault(string tweakId, IEnumerable<RegSet> sets)
    {
        foreach (var s in sets)
        {
            if (s.Default == null) Delete(s.Hive, s.Path, s.Name);
            else Set(s.Hive, s.Path, s.Name, s.Default, s.Kind);
        }
        Store.Backups.Remove(tweakId);
        Store.SaveBackups();
        Store.Log($"Reset {tweakId} to default");
    }

    public static void RevertWithBackup(string tweakId, IEnumerable<RegSet> sets)
    {
        if (Store.Backups.TryGetValue(tweakId, out var bk))
        {
            foreach (var r in bk.Registry) Restore(r);
        }
        else
        {
            foreach (var s in sets)
            {
                if (s.Default == null) Delete(s.Hive, s.Path, s.Name);
                else Set(s.Hive, s.Path, s.Name, s.Default, s.Kind);
            }
        }
        Store.Backups.Remove(tweakId);
        Store.SaveBackups();
        Store.Log($"Reverted {tweakId}");
    }
}
