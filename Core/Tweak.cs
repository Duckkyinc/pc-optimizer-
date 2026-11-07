using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Overdrive.Core;

public enum Impact { High, Medium, Low, Situational, Quality }

public class Tweak
{
    public string Id { get; init; }
    public string Title { get; init; }
    public string Description { get; init; }
    public Impact Impact { get; init; } = Impact.Low;
    public bool NeedsReboot { get; init; }
    public bool Advanced { get; init; }
    public bool Recommended { get; init; }
    public string Warning { get; init; }

    public Func<bool> IsAvailable { get; init; } = () => true;
    public Func<bool> IsApplied { get; init; }
    public Action Apply { get; init; }
    public Action Revert { get; init; }

    public string ImpactLabel => Impact switch
    {
        Impact.High => "BIG IMPACT",
        Impact.Medium => "MEDIUM IMPACT",
        Impact.Low => "SMALL IMPACT",
        Impact.Situational => "SITUATIONAL",
        Impact.Quality => "FEEL / AIM",
        _ => ""
    };

    /// <summary>Simple registry-only tweak. Sets are re-evaluated each call so dynamic targets work.</summary>
    public static Tweak Registry(string id, string title, string desc, Impact impact, Func<IEnumerable<RegSet>> sets,
        bool recommended = false, bool reboot = false, bool advanced = false, string warning = null,
        Action after = null, Func<bool> available = null)
    {
        return new Tweak
        {
            Id = id, Title = title, Description = desc, Impact = impact, Recommended = recommended,
            NeedsReboot = reboot, Advanced = advanced, Warning = warning,
            IsAvailable = available ?? (() => true),
            IsApplied = () => { var l = sets().ToList(); return l.Count > 0 && l.All(Reg.Matches); },
            Apply = () => { Reg.ApplyWithBackup(id, sets()); after?.Invoke(); },
            Revert = () => { Reg.RevertWithBackup(id, sets()); after?.Invoke(); },
        };
    }

    public static Tweak Registry(string id, string title, string desc, Impact impact, RegSet[] sets,
        bool recommended = false, bool reboot = false, bool advanced = false, string warning = null,
        Action after = null, Func<bool> available = null)
        => Registry(id, title, desc, impact, () => sets, recommended, reboot, advanced, warning, after, available);
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

/// <summary>What the UI binds to. Flipping IsOn runs Apply/Revert in the background.</summary>
public class TweakVM : Observable
{
    public Tweak Tweak { get; }
    public TweakVM(Tweak t) { Tweak = t; Ready = RefreshAsync(); }

    /// <summary>Completes when the first background state check is done.</summary>
    public Task Ready { get; }

    public static event Action AnyChanged;

    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string ImpactLabel => Tweak.ImpactLabel;
    public string ImpactKey => Tweak.Impact.ToString();
    public bool NeedsReboot => Tweak.NeedsReboot;
    public bool Advanced => Tweak.Advanced;
    public bool Recommended => Tweak.Recommended;
    public string Warning => Tweak.Warning;
    public bool HasWarning => !string.IsNullOrEmpty(Tweak.Warning);

    bool _available = true;
    public bool Available { get => _available; private set => SetField(ref _available, value); }

    bool _busy;
    public bool Busy { get => _busy; private set { SetField(ref _busy, value); Raise(nameof(CanToggle)); } }
    public bool CanToggle => !Busy && Available && !_checking;
    bool _checking = true;

    string _status;
    public string Status { get => _status; set => SetField(ref _status, value); }

    bool _isOn;
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn || Busy) return;
            _ = SetAsync(value);
        }
    }

    public void Refresh() => _ = RefreshAsync();

    /// <summary>Checks the real state in the background so the UI never freezes.</summary>
    public async Task RefreshAsync()
    {
        bool avail = false, on = false;
        await Task.Run(() =>
        {
            try { avail = Tweak.IsAvailable(); on = avail && Tweak.IsApplied(); }
            catch (Exception ex) { Store.Log($"State check failed for {Tweak.Id}: {ex.Message}"); }
        });
        if (Busy) return; // an apply/revert is in progress; it will set the state itself
        Available = avail;
        _isOn = on;
        _checking = false;
        Raise(nameof(IsOn));
        Raise(nameof(CanToggle));
    }

    public async Task SetAsync(bool on, bool quiet = false)
    {
        if (!Available || Busy) return;
        Busy = true;
        Status = on ? "Applying..." : "Reverting...";
        _isOn = on; // optimistic, so the switch doesn't flicker
        Raise(nameof(IsOn));
        try
        {
            await Task.Run(() => { if (on) Tweak.Apply(); else Tweak.Revert(); });
            // Most tweaks take effect the moment you flip them. Driver/game settings are read when a game starts.
            bool gameSide = Tweak.Id.StartsWith("nv.") || Tweak.Id.StartsWith("game.") || Tweak.Id is "win.gamedvr" or "win.windowedopt";
            string when = NeedsReboot ? " - restart to finish" : gameSide && BoostMode.ActiveGame != null ? " - restart the game to see it" : "";
            Status = (on ? "Applied" : "Turned off") + when;
            if (gameSide && !NeedsReboot && BoostMode.ActiveGame != null)
                Tray.Notify(Tweak.Title, $"Changed. Restart {BoostMode.ActiveGame.Name} for it to take effect.");
        }
        catch (Exception ex)
        {
            Store.Log($"{(on ? "Apply" : "Revert")} failed for {Tweak.Id}: {ex}");
            Status = "Failed: " + ex.Message;
            if (!quiet)
                Application.Current?.Dispatcher.Invoke(() =>
                    Overdrive.Pages.Dialog.Show($"{Tweak.Title}\n\n{ex.Message}", "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        finally
        {
            Busy = false;
            await Task.Run(() => { try { _isOn = Tweak.IsApplied(); } catch { _isOn = false; } });
            _checking = false;
            Raise(nameof(IsOn));
            Raise(nameof(CanToggle));
            AnyChanged?.Invoke();
        }
    }
}
