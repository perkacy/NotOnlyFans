using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Overclock;

/// <summary>
/// The OC tab's service side: finds the cards that can be tuned, applies what the user chose, keeps it in
/// %ProgramData%\NotOnlyFans\oc.json and, if the user asked for it, applies it again when the service starts.
/// Safety:
/// - nothing is written to a card the user never applied anything to;
/// - at startup an overclock is applied only if Windows and the graphics driver have not crashed since it was
///   applied last (a crash may be the overclock's fault) - otherwise the app says why it was skipped;
/// - when the card's clocks change under us (the driver reset after a crash, or another tool), the overclock is
///   not forced back; the app says so and the user decides.
/// Every call happens under <see cref="SensorPoller.Sync"/>.
/// </summary>
internal sealed class OcController(ILogger log)
{
    private const string FileName = "oc.json";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

    /// <summary>At boot the driver may not be ready yet: startup apply is retried this long.</summary>
    private static readonly TimeSpan StartupRetries = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, IOcCard> _cards = new(StringComparer.Ordinal);
    private readonly List<OcGpu> _blocked = [];   // a maker's library that crashed the service before: shown, not used
    private readonly HashSet<string> _startupPending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _appliedNow = new(StringComparer.Ordinal);   // applied by this run: watched for resets
    private DateTime _started;
    private DateTime _lastCheck;

    public OcState State { get; } = new();

    /// <summary>Raised (under the lock) after any change the window app should see.</summary>
    public event Action<OcState>? StateChanged;

    public void Start(DateTime now)
    {
        _started = now;
        State.Settings = Load();
        Find("NVIDIA", null, NvidiaCard.Find);
        Find("AMD", AmdCard.Library, AmdCard.Find);
        Find("Intel", IntelCard.Library, () => IntelCard.Find());
        Refresh();
        log.LogInformation("Overclocking: {Cards}.", State.Gpus.Count == 0 ? "no graphics card"
            : string.Join(", ", State.Gpus.Select(g => g.Unsupported is null
                ? $"{g.Name} (core {g.CoreMin:+0;-0;0}…{g.CoreMax:+0;-0;0} MHz, memory {g.MemoryMin:+0;-0;0}…{g.MemoryMax:+0;-0;0} MHz, power {g.PowerMin:0}–{g.PowerMax:0} {g.PowerUnit})"
                : $"{g.Name} (not supported)")));

        foreach (var (key, setting) in State.Settings)
        {
            if (!setting.ApplyAtStartup)
            {
                continue;
            }
            if (CrashSince(setting.AppliedAt) is { } crash)
            {
                State.Notices[key] = $"Your overclock was not applied when Windows started: {crash} after it was applied. " +
                    "If that was the overclock, lower it. Apply sets it again.";
                log.LogWarning("Overclock of {Card} not applied at startup: {Crash}.", key, crash);
            }
            else
            {
                _startupPending.Add(key);
            }
        }
        ApplyStartup(now);
    }

    /// <summary>
    /// One maker's cards. AMD's and Intel's libraries are first used under <see cref="NativeGuard"/>: when one
    /// crashed the service before, it is left alone and the app says why.
    /// </summary>
    private void Find<T>(string maker, string? library, Func<List<T>> find) where T : IOcCard
    {
        if (library is not null && NativeGuard.Crashed(library))
        {
            _blocked.Add(new OcGpu
            {
                Key = library,
                Name = $"{maker} graphics card",
                Unsupported = $"NotOnlyFans stopped while it was talking to the {maker} graphics driver, so it doesn't use it any more. " +
                              "It tries again after you install NotOnlyFans again (or a newer version). Copy diagnostics in Settings and report it, please.",
            });
            log.LogWarning("{Maker} overclocking is off: the service stopped inside the driver library before.", maker);
            return;
        }
        try
        {
            var cards = library is null ? find() : NativeGuard.Run(library, () =>
            {
                var found = find();
                foreach (var card in found)
                {
                    card.Read();   // the first read is guarded too
                }
                return found;
            });
            foreach (var card in cards)
            {
                _cards[card.Key] = card;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Looking for {Maker} graphics cards to overclock failed.", maker);
        }
    }

    /// <summary>About once a second: startup retries, and every few seconds a check that the card still runs what was applied.</summary>
    public void Tick(DateTime now)
    {
        if (now - _lastCheck < CheckEvery)
        {
            return;
        }
        _lastCheck = now;
        ApplyStartup(now);
        if (_cards.Count == 0)
        {
            return;
        }

        string before = Fingerprint();
        Refresh();
        foreach (var key in _appliedNow.ToList())
        {
            var gpu = State.Gpus.FirstOrDefault(g => g.Key == key);
            if (gpu is null || !State.Settings.TryGetValue(key, out var setting) || Runs(gpu, setting))
            {
                continue;
            }
            _appliedNow.Remove(key);
            State.Notices[key] = "Your overclock is no longer applied: the graphics driver reset (for example after a game or the driver crashed) " +
                "or another program changed the clocks. NotOnlyFans did not set it again. If it was unstable, lower it, then Apply.";
            log.LogWarning("Overclock of {Card} is no longer applied (driver reset or another tool).", key);
        }
        if (Fingerprint() != before)
        {
            Changed();
        }
    }

    public string? Apply(string key, OcSetting setting)
    {
        if (!_cards.TryGetValue(key, out var card))
        {
            return "This graphics card is not available.";
        }
        if (setting.PowerLimit is { } watts && !float.IsFinite(watts))
        {
            return "Not a valid power limit.";
        }
        string? error = card.Apply(setting);
        Refresh();
        if (error is null)
        {
            var saved = setting.Copy();
            saved.AppliedAt = DateTimeOffset.Now;
            State.Settings[key] = saved;
            State.Notices.Remove(key);
            _startupPending.Remove(key);
            _appliedNow.Add(key);
            Save();
            log.LogInformation("Overclock applied to {Card}: core {Core:+0;-0;0} MHz, memory {Memory:+0;-0;0} MHz, power {Power}.",
                key, setting.CoreOffset, setting.MemoryOffset, setting.PowerLimit is { } p ? $"{p:0} {State.Gpus.FirstOrDefault(g => g.Key == key)?.PowerUnit}" : "default");
        }
        Changed();
        return error;
    }

    /// <summary>The card's defaults again; the saved setting goes (so nothing is applied at startup either).</summary>
    public string? Reset(string key)
    {
        if (!_cards.TryGetValue(key, out var card))
        {
            return "This graphics card is not available.";
        }
        string? error = card.Apply(new OcSetting());
        _appliedNow.Remove(key);
        _startupPending.Remove(key);
        if (error is null)
        {
            State.Settings.Remove(key);
            State.Notices.Remove(key);
            Save();
            log.LogInformation("Overclock of {Card} reset to the card's defaults.", key);
        }
        Refresh();
        Changed();
        return error;
    }

    public string? SetStartup(string key, bool on)
    {
        if (!State.Settings.TryGetValue(key, out var setting))
        {
            return "Apply an overclock first.";
        }
        setting.ApplyAtStartup = on;
        Save();
        Changed();
        return null;
    }

    private void ApplyStartup(DateTime now)
    {
        foreach (var key in _startupPending.ToList())
        {
            if (!State.Settings.TryGetValue(key, out var setting))
            {
                _startupPending.Remove(key);
                continue;
            }
            string? error = _cards.TryGetValue(key, out var card) ? card.Apply(setting) : "The graphics card was not found.";
            if (error is null)
            {
                _startupPending.Remove(key);
                _appliedNow.Add(key);
                log.LogInformation("Overclock of {Card} applied at startup.", key);
                Refresh();
                Changed();
            }
            else if (now - _started > StartupRetries)
            {
                _startupPending.Remove(key);
                State.Notices[key] = $"Your overclock was not applied when Windows started: {error}";
                log.LogWarning("Overclock of {Card} not applied at startup: {Error}", key, error);
                Changed();
            }
        }
    }

    /// <summary>Whether the card runs this setting (clock offsets; the power limit within a watt).</summary>
    private static bool Runs(OcGpu gpu, OcSetting setting) =>
        (gpu.CoreMax == gpu.CoreMin || gpu.CoreOffset == Math.Clamp(setting.CoreOffset, gpu.CoreMin, gpu.CoreMax))
        && (gpu.MemoryMax == gpu.MemoryMin || gpu.MemoryOffset == Math.Clamp(setting.MemoryOffset, gpu.MemoryMin, gpu.MemoryMax))
        && (gpu.PowerMax <= gpu.PowerMin || Math.Abs(gpu.PowerLimit - Math.Clamp(setting.PowerLimit ?? gpu.PowerDefault, gpu.PowerMin, gpu.PowerMax)) < 1);

    private void Refresh()
    {
        var gpus = new List<OcGpu>();
        foreach (var card in _cards.Values)
        {
            try
            {
                gpus.Add(card.Read());
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Reading {Card} failed.", card.Key);
                gpus.Add(new OcGpu { Key = card.Key, Name = card.Name, Unsupported = "The graphics driver didn't answer." });
            }
        }
        gpus.AddRange(_blocked);
        State.Gpus = gpus;
    }

    private string Fingerprint() => string.Join(';', State.Gpus.Select(g =>
        $"{g.Key}|{g.Unsupported}|{g.CoreOffset}|{g.MemoryOffset}|{g.PowerLimit:0.0}|{g.BoostClock}|{g.MemoryClock}"))
        + "#" + string.Join(';', State.Notices.Select(n => n.Key));

    private void Changed() => StateChanged?.Invoke(State);

    /// <summary>
    /// A crash of Windows or of the graphics driver since <paramref name="since"/>, in words, or null.
    /// Read from the System event log: unexpected restarts, blue screens, driver timeouts and NVIDIA or AMD driver errors.
    /// </summary>
    private string? CrashSince(DateTimeOffset? since)
    {
        if (since is not { } from)
        {
            return null;
        }
        string time = from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string query =
            $"*[System[TimeCreated[@SystemTime>='{time}'] and (" +
            "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or " +
            "(Provider[@Name='EventLog'] and EventID=6008) or " +
            "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or " +
            "(Provider[@Name='Display'] and EventID=4101) or " +
            "((Provider[@Name='nvlddmkm'] or Provider[@Name='amdkmdag']) and (Level=1 or Level=2)))]]";
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true });
            using var record = reader.ReadEvent();
            if (record is null)
            {
                return null;
            }
            string when = record.TimeCreated?.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) ?? "recently";
            return record.ProviderName switch
            {
                "Display" => $"the graphics driver stopped responding and recovered ({when})",
                "nvlddmkm" => $"the NVIDIA driver reported an error ({when})",
                "amdkmdag" => $"the AMD driver reported an error ({when})",
                "Microsoft-Windows-WER-SystemErrorReporting" => $"Windows stopped with an error ({when})",
                _ => $"Windows restarted without shutting down properly ({when})",
            };
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Reading the System event log failed - the overclock is not applied at startup to be safe.");
            return "NotOnlyFans couldn't check the System event log for crashes";
        }
    }

    private Dictionary<string, OcSetting> Load()
    {
        var settings = DataDirectory.Load(FileName, ContractsJson.Default.DictionaryStringOcSetting, log) ?? [];
        foreach (var (key, setting) in settings.ToList())
        {
            if (setting is null || (setting.PowerLimit is { } p && !float.IsFinite(p)))
            {
                settings.Remove(key);
                log.LogWarning("Saved overclock of {Card} is damaged - it is not applied.", key);
            }
        }
        return settings;
    }

    private void Save() => DataDirectory.Save(FileName, State.Settings, ContractsJson.Default.DictionaryStringOcSetting, log);
}
