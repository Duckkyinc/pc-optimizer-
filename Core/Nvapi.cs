using System.Runtime.InteropServices;

namespace Overdrive.Core;

/// <summary>
/// Minimal NVIDIA driver-settings (DRS) access through nvapi64.dll, the same API the
/// NVIDIA Control Panel uses. Function and setting IDs come from NVIDIA's public NVAPI headers.
/// Everything here edits the *global* driver profile, and is fully undoable.
/// </summary>
public static class Nvapi
{
    // Function IDs (nvapi_interface.h)
    const uint ID_Initialize = 0x0150e828;
    const uint ID_CreateSession = 0x0694d52e;
    const uint ID_DestroySession = 0xdad9cff8;
    const uint ID_LoadSettings = 0x375dbd6b;
    const uint ID_SaveSettings = 0xfcbc7e14;
    const uint ID_GetBaseProfile = 0xda8466a0;
    const uint ID_SetSetting = 0x577dd202;
    const uint ID_GetSetting = 0x73bf8338;
    const uint ID_DeleteProfileSetting = 0xe4a26362;

    // Setting IDs / values (NvApiDriverSettings.h)
    public const uint PREFERRED_PSTATE = 0x1057EB71;   // Power management mode
    public const uint PSTATE_PREFER_MAX = 0x1;
    public const uint PRERENDERLIMIT = 0x007BA09E;     // Max pre-rendered frames ("Low Latency Mode")
    public const uint QUALITY_ENHANCEMENTS = 0x00CE2691; // Texture filtering - quality
    public const uint QUALITY_HIGHPERFORMANCE = 0x14;
    public const uint VSYNCMODE = 0x00A879CF;
    public const uint VSYNC_FORCEOFF = 0x08416747;
    public const uint OGL_THREAD_CONTROL = 0x20C1221E; // Threaded optimization
    public const uint OGL_THREAD_ENABLE = 0x1;
    public const uint SHADERCACHE_MAX_SIZE = 0x00AC8497;  // Shader cache size
    public const uint SHADERCACHE_UNLIMITED = 0xFFFFFFFF;
    public const uint REFRESH_RATE_OVERRIDE = 0x0064B541; // Preferred refresh rate
    public const uint REFRESH_HIGHEST = 0x1;
    public const uint ANISO_OPTS2 = 0x00E73211;           // Anisotropic sample optimization
    public const uint AA_GAMMA = 0x107D639D;              // Antialiasing - gamma correction
    public const uint FRL_FPS = 0x10835002;               // Max frame rate

    const int NVAPI_OK = 0;
    const int NVAPI_SETTING_NOT_FOUND = -160;

    // NVDRS_SETTING_V1 layout (pack 4): see nvapi.h
    const int SETTING_SIZE = 12320;
    const uint SETTING_VER1 = SETTING_SIZE | (1u << 16);
    const int OFF_ID = 4100, OFF_TYPE = 4104, OFF_LOCATION = 4108, OFF_CURRENT = 8220;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr QueryInterfaceFn(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateSessionFn(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SessionFn(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetBaseProfileFn(IntPtr session, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetSettingFn(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetSettingFn(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeleteSettingFn(IntPtr session, IntPtr profile, uint id);

    static readonly object Gate = new();
    static volatile bool _loaded, _ok;
    static readonly object LoadGate = new();
    static InitFn _init;
    static CreateSessionFn _create;
    static SessionFn _destroy, _load, _save;
    static GetBaseProfileFn _base;
    static SetSettingFn _set;
    static GetSettingFn _get;
    static DeleteSettingFn _delete;

    public static bool Available { get { if (_loaded) return _ok; EnsureLoaded(); return _ok; } }

    static T Fn<T>(QueryInterfaceFn qi, uint id) where T : Delegate
    {
        var p = qi(id);
        if (p == IntPtr.Zero) throw new EntryPointNotFoundException($"NVAPI function 0x{id:x8} missing");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (LoadGate) // separate from the session lock, so a slow settings load never blocks this
        {
            if (_loaded) return;
            try
            {
                if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib)) return;
                if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qiPtr)) return;
                var qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(qiPtr);
                _init = Fn<InitFn>(qi, ID_Initialize);
                _create = Fn<CreateSessionFn>(qi, ID_CreateSession);
                _destroy = Fn<SessionFn>(qi, ID_DestroySession);
                _load = Fn<SessionFn>(qi, ID_LoadSettings);
                _save = Fn<SessionFn>(qi, ID_SaveSettings);
                _base = Fn<GetBaseProfileFn>(qi, ID_GetBaseProfile);
                _set = Fn<SetSettingFn>(qi, ID_SetSetting);
                _get = Fn<GetSettingFn>(qi, ID_GetSetting);
                _delete = Fn<DeleteSettingFn>(qi, ID_DeleteProfileSetting);
                _ok = _init() == NVAPI_OK;
            }
            catch (Exception ex) { Store.Log("NVAPI unavailable: " + ex.Message); _ok = false; }
            finally { _loaded = true; }
        }
    }

    static IntPtr _session, _profile;
    static volatile bool _stale = true;

    /// <summary>Re-read driver settings next time (e.g. the user changed something in NVIDIA Control Panel).</summary>
    public static void Invalidate() => _stale = true; // no lock: never block the UI

    static T WithBaseProfile<T>(bool save, Func<IntPtr, IntPtr, T> work)
    {
        EnsureLoaded();
        if (!_ok) throw new InvalidOperationException("NVIDIA driver API not available. Is an NVIDIA GPU with a current driver installed?");
        lock (Gate)
        {
            if (_stale || _session == IntPtr.Zero)
            {
                // Loading the driver profile database is slow, so do it once and reuse it.
                if (_session != IntPtr.Zero) { try { _destroy(_session); } catch { } _session = IntPtr.Zero; }
                Check(_create(out var session), "CreateSession");
                try
                {
                    Check(_load(session), "LoadSettings");
                    Check(_base(session, out var profile), "GetBaseProfile");
                    _session = session; _profile = profile; _stale = false;
                }
                catch { _destroy(session); throw; }
            }
            try
            {
                var result = work(_session, _profile);
                if (save) Check(_save(_session), "SaveSettings");
                return result;
            }
            catch { _stale = true; throw; }
        }
    }

    static void Check(int status, string what)
    {
        if (status != NVAPI_OK) throw new InvalidOperationException($"NVIDIA driver call {what} failed (code {status}).");
    }

    /// <summary>Returns (value, setByUser). Value is null when the setting doesn't exist on the global profile.</summary>
    public static (uint? Value, bool UserSet) Get(uint settingId) => WithBaseProfile(false, (s, p) =>
    {
        var buf = Marshal.AllocHGlobal(SETTING_SIZE);
        try
        {
            ZeroMemory(buf, SETTING_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)SETTING_VER1));
            int st = _get(s, p, settingId, buf);
            if (st == NVAPI_SETTING_NOT_FOUND) return ((uint?)null, false);
            Check(st, "GetSetting");
            uint val = unchecked((uint)Marshal.ReadInt32(buf, OFF_CURRENT));
            int location = Marshal.ReadInt32(buf, OFF_LOCATION);
            return ((uint?)val, location == 0); // 0 = NVDRS_CURRENT_PROFILE_LOCATION
        }
        finally { Marshal.FreeHGlobal(buf); }
    });

    public static void Set(uint settingId, uint value) => WithBaseProfile(true, (s, p) =>
    {
        var buf = Marshal.AllocHGlobal(SETTING_SIZE);
        try
        {
            ZeroMemory(buf, SETTING_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)SETTING_VER1));
            Marshal.WriteInt32(buf, OFF_ID, unchecked((int)settingId));
            Marshal.WriteInt32(buf, OFF_TYPE, 0); // NVDRS_DWORD_TYPE
            Marshal.WriteInt32(buf, OFF_CURRENT, unchecked((int)value));
            Check(_set(s, p, buf), "SetSetting");
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    });

    public static void Delete(uint settingId) => WithBaseProfile(true, (s, p) =>
    {
        int st = _delete(s, p, settingId);
        if (st != NVAPI_OK && st != NVAPI_SETTING_NOT_FOUND) Check(st, "DeleteProfileSetting");
        return true;
    });

    static void ZeroMemory(IntPtr p, int size)
    {
        var zeros = new byte[size];
        Marshal.Copy(zeros, 0, p, size);
    }

    /// <summary>Tweak that sets one global NVIDIA setting and restores the exact previous value on revert.</summary>
    public static Tweak CreateTweak(string id, string title, string desc, Impact impact, uint settingId, uint value,
        bool recommended = false, string warning = null)
        => CreateTweak(id, title, desc, impact, settingId, () => value, recommended, warning);

    public static Tweak CreateTweak(string id, string title, string desc, Impact impact, uint settingId, Func<uint> valueFn,
        bool recommended = false, string warning = null) => new()
    {
        Id = id, Title = title, Description = desc, Impact = impact, Recommended = recommended, Warning = warning,
        IsAvailable = () => Available,
        IsApplied = () => Available && Get(settingId).Value == valueFn(),
        Apply = () =>
        {
            if (!Store.Backups.ContainsKey(id))
            {
                var (cur, userSet) = Get(settingId);
                var bk = new TweakBackup();
                bk.Extra["userSet"] = (userSet && cur.HasValue) ? "1" : "0";
                if (cur.HasValue) bk.Extra["value"] = cur.Value.ToString();
                Store.Backups[id] = bk;
                Store.SaveBackups();
            }
            Set(settingId, valueFn());
            Store.Log($"Applied {id}");
        },
        Revert = () =>
        {
            if (Store.Backups.TryGetValue(id, out var bk) && bk.Extra.GetValueOrDefault("userSet") == "1"
                && uint.TryParse(bk.Extra.GetValueOrDefault("value"), out var old))
                Set(settingId, old);
            else
                Delete(settingId); // back to NVIDIA's default
            Store.Backups.Remove(id);
            Store.SaveBackups();
            Store.Log($"Reverted {id}");
        }
    };
}
