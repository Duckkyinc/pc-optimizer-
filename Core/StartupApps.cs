using Microsoft.Win32;

namespace Overdrive.Core;

/// <summary>
/// Startup apps, enabled/disabled exactly the way Task Manager does it (StartupApproved),
/// so nothing is deleted and everything shows up correctly in Task Manager too.
/// </summary>
public class StartupItem : Observable
{
    public string Name { get; init; }
    public string Command { get; init; }
    public RegistryHive Hive { get; init; }
    public string ApprovedKey { get; init; }
    public string Location => Hive == RegistryHive.CurrentUser ? "This user" : "All users";

    bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            try
            {
                StartupApps.SetEnabled(this, value);
                _enabled = value;
            }
            catch (Exception ex) { Store.Log($"Startup toggle failed for {Name}: {ex.Message}"); }
            Raise();
        }
    }
    internal void Init(bool e) => _enabled = e;
}

public static class StartupApps
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static List<StartupItem> List()
    {
        var items = new List<StartupItem>();
        void Scan(RegistryHive hive, string runKey, string approvedKey)
        {
            foreach (var name in Reg.ValueNames(hive, runKey))
            {
                if (string.IsNullOrEmpty(name)) continue;
                var cmd = Reg.Get(hive, runKey, name)?.ToString() ?? "";
                var item = new StartupItem { Name = name, Command = cmd, Hive = hive, ApprovedKey = approvedKey };
                item.Init(IsEnabled(hive, approvedKey, name));
                items.Add(item);
            }
        }
        Scan(RegistryHive.CurrentUser, Run, Approved + @"\Run");
        Scan(RegistryHive.LocalMachine, Run, Approved + @"\Run");
        Scan(RegistryHive.LocalMachine, Run32, Approved + @"\Run32");
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsEnabled(RegistryHive hive, string approvedKey, string name)
    {
        // first byte: even (02/06) = enabled, odd (03/07) = disabled
        return Reg.Get(hive, approvedKey, name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    public static void SetEnabled(StartupItem item, bool enabled)
    {
        var data = new byte[12];
        data[0] = (byte)(enabled ? 0x02 : 0x03);
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        Reg.Set(item.Hive, item.ApprovedKey, item.Name, data, RegistryValueKind.Binary);
        Store.Log($"Startup app '{item.Name}' {(enabled ? "enabled" : "disabled")}");
    }
}
