using System.Runtime.InteropServices;

namespace Overdrive.Core;

/// <summary>
/// The #1 free "FPS boost": a lot of people buy a 144/165/240Hz monitor and Windows leaves it at 60Hz.
/// </summary>
public static class Display
{
    public record Info(int Width, int Height, int CurrentHz, int MaxHz);

    static Native.DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<Native.DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    static Info _cached; static DateTime _cachedAt;
    /// <summary>Last known display info; refreshes itself in the background every 30s.</summary>
    public static Info Cached
    {
        get
        {
            if ((DateTime.Now - _cachedAt).TotalSeconds > 30)
            {
                _cachedAt = DateTime.Now;
                Task.Run(() => { try { _cached = GetPrimary(); } catch { } });
            }
            return _cached;
        }
    }

    public static Info GetPrimary()
    {
        var cur = NewMode();
        if (!Native.EnumDisplaySettings(null, Native.ENUM_CURRENT_SETTINGS, ref cur)) return null;
        int max = cur.dmDisplayFrequency;
        for (int i = 0; ; i++)
        {
            var m = NewMode();
            if (!Native.EnumDisplaySettings(null, i, ref m)) break;
            if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmBitsPerPel == cur.dmBitsPerPel)
                max = Math.Max(max, m.dmDisplayFrequency);
            if (i > 2000) break;
        }
        return new Info(cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, max);
    }

    public static bool SetRefresh(int hz)
    {
        var cur = NewMode();
        if (!Native.EnumDisplaySettings(null, Native.ENUM_CURRENT_SETTINGS, ref cur)) return false;
        cur.dmDisplayFrequency = hz;
        cur.dmFields = Native.DM_DISPLAYFREQUENCY;
        if (Native.ChangeDisplaySettings(ref cur, Native.CDS_TEST) != Native.DISP_CHANGE_SUCCESSFUL) return false;
        var ok = Native.ChangeDisplaySettings(ref cur, Native.CDS_UPDATEREGISTRY) == Native.DISP_CHANGE_SUCCESSFUL;
        Store.Log($"Set refresh rate to {hz}Hz: {ok}");
        return ok;
    }
}
