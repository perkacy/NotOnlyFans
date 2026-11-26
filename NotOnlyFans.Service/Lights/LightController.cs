using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Lights;

/// <summary>
/// Finds the lights NotOnlyFans can drive, shows what the user chose, keeps temperature colors current
/// and stores settled choices in the devices (so they come back at power-on even before the service runs).
/// Every call happens under <see cref="SensorPoller.Sync"/>.
/// </summary>
internal sealed class LightController(SensorPoller poller, ILogger log) : IDisposable
{
    /// <summary>A choice is stored in the device once it has not changed for this long (device memory wears).</summary>
    private static readonly TimeSpan SaveAfter = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan WarningCheck = TimeSpan.FromSeconds(30);

    private readonly List<ILightDevice> _devices = [];
    private readonly Dictionary<string, ILightDevice> _deviceOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LightOutput> _shown = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unsaved = new(StringComparer.Ordinal);
    private DateTime _lastChange;
    private DateTime _lastWarningCheck;

    public ColorState State { get; private set; } = new();

    /// <summary>Raised (under the lock) after any change the window app should see.</summary>
    public event Action<ColorState>? StateChanged;

    public void Start()
    {
        State = Load();
        Detect<MsiMysticLight>("MSI motherboard", MsiMysticLight.TryOpen);
        Detect<GigabyteGpuLight>("Gigabyte graphics card", GigabyteGpuLight.TryOpen);

        State.Lights = _devices.SelectMany(d => d.Lights).ToList();
        foreach (var device in _devices)
        {
            Try(device.Refresh, "Reading lights");
        }
        CheckWarnings();
        log.LogInformation("Lights: {Lights}.", State.Lights.Count == 0 ? "none found" : string.Join(", ", State.Lights.Select(l => $"{l.Device} {l.Detail}")));

        // Show the saved choices (the devices may have lost them, e.g. after a power cut).
        ShowAll();
    }

    private void Detect<T>(string what, Func<T?> open) where T : class, ILightDevice
    {
        try
        {
            if (open() is { } device)
            {
                _devices.Add(device);
                foreach (var light in device.Lights)
                {
                    _deviceOf[light.Key] = device;
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Looking for {What} lights failed.", what);
        }
    }

    /// <summary>The setting a light follows now: all of them with Sync all, else its own (null = leave it alone).</summary>
    private LightSetting? Effective(string key) =>
        State.Sync && State.All is { } all ? all : State.Settings.GetValueOrDefault(key);

    public string? SetLight(string key, LightSetting setting)
    {
        if (!_deviceOf.ContainsKey(key))
        {
            return "This light is not available.";
        }
        if (Validate(setting) is { } error)
        {
            return error;
        }
        State.Settings[key] = setting;
        if (!State.Sync)
        {
            Show([key], save: false);
        }
        Changed();
        return null;
    }

    public string? SetSync(bool sync, LightSetting? all)
    {
        if (all is not null && Validate(all) is { } error)
        {
            return error;
        }
        if (sync && all is null && State.All is null)
        {
            return "Choose how all lights should look.";
        }
        State.Sync = sync;
        if (all is not null)
        {
            State.All = all;
        }
        Show(State.Lights.Select(l => l.Key), save: false);
        Changed();
        return null;
    }

    private string? Validate(LightSetting s)
    {
        if (CheckShape(s) is { } problem)
        {
            return problem;
        }
        s.Brightness = Math.Clamp(s.Brightness, 0, 100);
        s.Color = s.Color.ToUpperInvariant();
        s.CoolColor = s.CoolColor?.ToUpperInvariant();
        s.HotColor = s.HotColor?.ToUpperInvariant();
        if (s.Effect == LightEffect.Temperature
            && s.Sources.Any(k => poller.Find(k) is not { SensorType: SensorType.Temperature }))
        {
            return "Choose a temperature sensor.";
        }
        return null;
    }

    /// <summary>Whether a setting makes sense on its own (also for settings read from the file, before sensors are known).</summary>
    private static string? CheckShape(LightSetting? s)
    {
        if (s is null || s.Sources is null)
        {
            return "Incomplete setting.";
        }
        if (!Enum.IsDefined(s.Effect) || !Enum.IsDefined(s.Speed))
        {
            return "Unknown effect.";
        }
        if (!LightColor.TryParse(s.Color, out _, out _, out _)
            || (s.CoolColor is not null && !LightColor.TryParse(s.CoolColor, out _, out _, out _))
            || (s.HotColor is not null && !LightColor.TryParse(s.HotColor, out _, out _, out _)))
        {
            return "Not a color.";
        }
        if (!float.IsFinite(s.Brightness) || !float.IsFinite(s.Cool) || !float.IsFinite(s.Hot))
        {
            return "Not a number.";
        }
        if (s.Effect == LightEffect.Temperature)
        {
            if (s.Cool < 0 || s.Hot > 125 || s.Hot - s.Cool < 5)
            {
                return "Hot must be at least 5 °C above cool (0–125 °C).";
            }
            if (s.Sources.Count is 0 or > 8 || s.Sources.Any(string.IsNullOrEmpty))
            {
                return "Choose a temperature sensor.";
            }
        }
        return null;
    }

    /// <summary>Every second, after the sensors are read: temperature colors, and storing settled choices.</summary>
    public void Tick()
    {
        if (_devices.Count == 0)
        {
            return;
        }

        bool liveChanged = false;
        foreach (var light in State.Lights)
        {
            if (Effective(light.Key) is { Effect: LightEffect.Temperature } setting && Output(light.Key, setting) is { } output
                && (!_shown.TryGetValue(light.Key, out var shown) || shown != output))
            {
                Show(light.Key, output, save: false, settle: false);   // not stored in the device every second
                liveChanged = true;
            }
        }

        if (_unsaved.Count > 0 && DateTime.UtcNow - _lastChange >= SaveAfter)
        {
            var keys = _unsaved.ToList();
            _unsaved.Clear();
            Show(keys, save: true);
        }

        if (DateTime.UtcNow - _lastWarningCheck >= WarningCheck && CheckWarnings())
        {
            liveChanged = true;
        }
        if (liveChanged)
        {
            StateChanged?.Invoke(State);
        }
    }

    /// <summary>At start-up: what was chosen before (already stored in the devices, so not stored again).</summary>
    private void ShowAll() => Show(State.Lights.Select(l => l.Key), save: false, settle: false);

    private void Show(IEnumerable<string> keys, bool save, bool settle = true)
    {
        foreach (var key in keys)
        {
            if (Effective(key) is { } setting && Output(key, setting) is { } output)
            {
                Show(key, output, save, settle);
            }
        }
    }

    private void Show(string key, LightOutput output, bool save, bool settle = true)
    {
        if (!_deviceOf.TryGetValue(key, out var device))
        {
            return;
        }
        if (Try(() => device.Apply(new Dictionary<string, LightOutput> { [key] = output }, save), "Changing a light"))
        {
            _shown[key] = output;
            if (!save && settle)
            {
                _unsaved.Add(key);
                _lastChange = DateTime.UtcNow;
            }
        }
        else
        {
            _shown.Remove(key);
        }
    }

    /// <summary>What a light shows for a setting; temperature becomes the color for the current reading.</summary>
    private LightOutput? Output(string key, LightSetting s)
    {
        string color = s.Color;
        var effect = s.Effect;
        if (effect == LightEffect.Temperature)
        {
            float? hottest = null;
            foreach (var source in s.Sources)
            {
                if (poller.Reading(source) is { } v && (hottest is null || v > hottest))
                {
                    hottest = v;
                }
            }
            if (hottest is null)
            {
                return _shown.TryGetValue(key, out var last) ? last : null;   // no reading: keep the last color
            }
            color = LightColor.ForTemperature(hottest.Value, s);
            State.Live[key] = color;
            effect = LightEffect.Static;
        }
        else
        {
            State.Live.Remove(key);
        }
        LightColor.TryParse(color, out byte r, out byte g, out byte b);
        return new LightOutput(effect, r, g, b, s.Brightness, s.Speed);
    }

    /// <summary>Vendor lighting software running next to us can change the lights back.</summary>
    private bool CheckWarnings()
    {
        _lastWarningCheck = DateTime.UtcNow;
        var warnings = new List<string>();
        bool Running(params string[] names) => names.Any(n => Process.GetProcessesByName(n).Length > 0);

        if (_devices.OfType<MsiMysticLight>().Any() && Running("Mystic_Light_Service", "LEDKeeper2"))
        {
            warnings.Add("MSI Center's Mystic Light is running. It can change the motherboard lights back; turn off Mystic Light in MSI Center.");
        }
        if (_devices.Count > 0 && Running("OpenRGB", "SignalRgb", "SignalRgbLauncher"))
        {
            warnings.Add("Another lighting app (OpenRGB or SignalRGB) is running. Two apps changing the same lights will fight; close one.");
        }
        if (warnings.SequenceEqual(State.Warnings))
        {
            return false;
        }
        State.Warnings = warnings;
        return true;
    }

    private void Changed()
    {
        Save();
        StateChanged?.Invoke(State);
    }

    private bool Try(Action action, string what)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "{What} failed.", what);
            return false;
        }
    }

    private const string FileName = "colors.json";

    private ColorState Load()
    {
        if (DataDirectory.Load(FileName, ContractsJson.Default.ColorState, log) is not { } state)
        {
            return new ColorState();   // lights are left as they are
        }
        state.Lights = [];
        state.Warnings = [];
        state.Live = [];
        state.Settings ??= [];
        foreach (var (key, setting) in state.Settings.ToList())
        {
            if (CheckShape(setting) is { } problem)
            {
                state.Settings.Remove(key);   // left as it is rather than guessing
                log.LogWarning("Saved setting of light {Key} is damaged ({Problem}) - left as it is.", key, problem);
            }
        }
        if (state.All is not null && CheckShape(state.All) is not null)
        {
            state.All = null;
        }
        if (state.All is null)
        {
            state.Sync = false;
        }
        return state;
    }

    private void Save() =>
        DataDirectory.Save(FileName, new ColorState { Settings = State.Settings, Sync = State.Sync, All = State.All },
            ContractsJson.Default.ColorState, log);

    public void Dispose()
    {
        foreach (var device in _devices)
        {
            device.Dispose();
        }
    }
}
