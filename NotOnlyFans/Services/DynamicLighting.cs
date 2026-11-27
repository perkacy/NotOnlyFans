using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using NotOnlyFans.Contracts;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.Devices.Lights.Effects;
using Windows.UI;

namespace NotOnlyFans.Services;

/// <summary>
/// Windows Dynamic Lighting devices (keyboards, mice, motherboards… that speak the HID LampArray standard).
/// Windows hands them only to apps of the signed-in user, never to a service, so the window app drives them:
/// it tells the service which devices there are (they become lights like any other: listed, set, synced,
/// temperature colors worked out there) and shows on them what the service's color state says.
/// Windows applies it while NotOnlyFans is in front, or always if the user picks NotOnlyFans for background
/// light control in Settings › Personalization › Dynamic Lighting. A device NotOnlyFans hasn't set isn't opened,
/// so it keeps what Windows or another app shows.
/// </summary>
public sealed class DynamicLighting
{
    public const string Prefix = "lamp/";

    /// <summary>Opens Windows Settings at Dynamic Lighting.</summary>
    public static readonly Uri SettingsUri = new("ms-settings:personalization-lighting");

    private sealed class Lamp(string id, string key, string name)
    {
        public string Id { get; } = id;
        public string Key { get; } = key;
        public string Name { get; set; } = name;
        public LampArray? Array { get; set; }
        public bool Opening { get; set; }
        public LampArrayEffectPlaylist? Playlist { get; set; }
        public Look? Shown { get; set; }
    }

    /// <summary>What a device shows: effect, color (already dimmed), speed.</summary>
    private readonly record struct Look(LightEffect Effect, Color Color, LightSpeed Speed);

    private readonly DispatcherQueue _ui;
    private readonly Dictionary<string, Lamp> _lamps = [];   // by device id
    private DeviceWatcher? _watcher;
    private bool _listed;
    private DateTime _lastReport;

    public DynamicLighting(DispatcherQueue ui) => _ui = ui;

    public void Start()
    {
        try
        {
            _watcher = DeviceInformation.CreateWatcher(LampArray.GetDeviceSelector(), (string[])["System.Devices.ContainerId"]);
            _watcher.Added += (sender, info) => _ = AddAsync(info);
            _watcher.Removed += (_, update) => _ui.TryEnqueue(() => Remove(update.Id));
            _watcher.EnumerationCompleted += (_, _) => _ui.TryEnqueue(() =>
            {
                _listed = true;
                Report();
            });
            _watcher.Start();
        }
        catch (Exception ex)
        {
            App.LogError(ex);   // Windows without Dynamic Lighting: nothing to drive
            return;
        }
        App.Sensors.ColorsChanged += OnColorsChanged;
    }

    private static string KeyOf(string id) =>
        Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant())), 0, 6).ToLowerInvariant();

    private async Task AddAsync(DeviceInformation info)
    {
        string name = await FriendlyNameAsync(info);
        _ui.TryEnqueue(() =>
        {
            if (!_lamps.ContainsKey(info.Id))
            {
                _lamps[info.Id] = new Lamp(info.Id, KeyOf(info.Id), name);
                if (_listed)
                {
                    Report();
                }
            }
        });
    }

    /// <summary>The product's name (the device container's, e.g. "ROG Strix Scope"), else the interface's.</summary>
    private static async Task<string> FriendlyNameAsync(DeviceInformation info)
    {
        try
        {
            if (info.Properties.TryGetValue("System.Devices.ContainerId", out var value) && value is Guid container)
            {
                var device = await DeviceInformation.CreateFromIdAsync(container.ToString("B"), null, DeviceInformationKind.DeviceContainer);
                if (!string.IsNullOrWhiteSpace(device?.Name))
                {
                    return device.Name.Trim();
                }
            }
        }
        catch (Exception)
        {
        }
        return string.IsNullOrWhiteSpace(info.Name) ? "Dynamic Lighting device" : info.Name.Trim();
    }

    private void Remove(string id)
    {
        if (_lamps.Remove(id, out var lamp))
        {
            Stop(lamp);
            Report();
        }
    }

    /// <summary>Tells the service which devices there are (again after it restarted and lost them).</summary>
    private void Report()
    {
        _lastReport = DateTime.UtcNow;
        var lights = _lamps.Values.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).Select(l => new LightInfo
        {
            Key = l.Key,
            Name = l.Name,
            Detail = "Windows Dynamic Lighting",
            Device = l.Name,
            Kind = "DynamicLighting",
        }).ToList();
        _ = App.Control.SetAppLightsAsync(lights);
    }

    private void OnColorsChanged()
    {
        if (App.Sensors.Colors is not { } state)
        {
            return;
        }
        var known = state.Lights.Where(l => l.Key.StartsWith(Prefix, StringComparison.Ordinal)).Select(l => l.Key).ToHashSet();
        if (_listed && !known.SetEquals(_lamps.Values.Select(l => l.Key)) && DateTime.UtcNow - _lastReport > TimeSpan.FromSeconds(5))
        {
            Report();
        }

        foreach (var lamp in _lamps.Values)
        {
            if (!known.Contains(lamp.Key) || LookFor(state, lamp.Key) is not { } look)
            {
                continue;   // not set in NotOnlyFans: left as it is
            }
            if (lamp.Shown != look)
            {
                _ = ShowAsync(lamp, look);
            }
        }
    }

    private static Look? LookFor(ColorState state, string key)
    {
        var setting = state.Sync && state.All is { } all ? all : state.Settings.GetValueOrDefault(key);
        if (setting is null)
        {
            return null;
        }
        string color = setting.Color;
        if (setting.Effect == LightEffect.Temperature)
        {
            if (state.Live.GetValueOrDefault(key) is not { } live)
            {
                return null;   // no reading yet
            }
            color = live;
        }
        LightColor.TryParse(color, out byte r, out byte g, out byte b);
        float k = Math.Clamp(setting.Brightness, 0, 100) / 100f;
        var dimmed = setting.Effect == LightEffect.Off
            ? Color.FromArgb(255, 0, 0, 0)
            : Color.FromArgb(255, (byte)Math.Round(r * k), (byte)Math.Round(g * k), (byte)Math.Round(b * k));
        var effect = setting.Effect == LightEffect.Temperature ? LightEffect.Static : setting.Effect;
        // The rainbow's brightness is carried in the color's value.
        if (effect == LightEffect.Rainbow)
        {
            byte v = (byte)Math.Round(255 * k);
            dimmed = Color.FromArgb(255, v, v, v);
        }
        return new Look(effect, dimmed, effect is LightEffect.Breathing or LightEffect.Rainbow ? setting.Speed : LightSpeed.Medium);
    }

    private async Task ShowAsync(Lamp lamp, Look look)
    {
        if (lamp.Array is null)
        {
            if (lamp.Opening)
            {
                return;
            }
            lamp.Opening = true;
            try
            {
                lamp.Array = await LampArray.FromIdAsync(lamp.Id);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                lamp.Opening = false;
            }
            if (lamp.Array is null || !_lamps.ContainsKey(lamp.Id))
            {
                return;
            }
            // Windows gave control back (window in front again, or the user's background choice): show it again.
            // (Older Windows has no such event; it applies the state whenever it gives control.)
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                try
                {
                    lamp.Array.AvailabilityChanged += (array, _) => _ui.TryEnqueue(() =>
                    {
                        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && array.IsAvailable)
                        {
                            lamp.Shown = null;
                            OnColorsChanged();
                        }
                    });
                }
                catch (Exception)
                {
                }
            }
            // The look may have changed while the device was opening.
            if (App.Sensors.Colors is { } state && LookFor(state, lamp.Key) is { } latest)
            {
                look = latest;
            }
        }

        try
        {
            Apply(lamp, lamp.Array, look);
            lamp.Shown = look;
        }
        catch (Exception ex)
        {
            lamp.Shown = null;
            App.LogError(ex);
        }
    }

    private static void Apply(Lamp lamp, LampArray array, Look look)
    {
        bool sameEffect = lamp.Shown is { } shown && shown.Effect == look.Effect && shown.Speed == look.Speed;
        if (look.Effect == LightEffect.Static && sameEffect && lamp.Playlist is null)
        {
            array.SetColor(look.Color);   // temperature colors: every second, just the color
            return;
        }
        Stop(lamp);
        int[] all = [.. Enumerable.Range(0, array.LampCount)];
        double period = look.Speed switch { LightSpeed.Slow => 4.0, LightSpeed.Medium => 2.5, _ => 1.2 };   // seconds per breath / color turn
        switch (look.Effect)
        {
            case LightEffect.Breathing:
            {
                var blink = new LampArrayBlinkEffect(array, all)
                {
                    Color = look.Color,
                    AttackDuration = TimeSpan.FromSeconds(period * 0.45),
                    SustainDuration = TimeSpan.FromSeconds(period * 0.05),
                    DecayDuration = TimeSpan.FromSeconds(period * 0.45),
                    RepetitionDelay = TimeSpan.FromSeconds(period * 0.05),
                    RepetitionMode = LampArrayRepetitionMode.Forever,
                };
                lamp.Playlist = Play(blink);
                break;
            }
            case LightEffect.Rainbow:
            {
                // A wave across the device: each lamp's hue follows its position from left to right.
                var offsets = new double[all.Length];
                double width = Math.Max(array.BoundingBox.X, 0.001);
                for (int i = 0; i < all.Length; i++)
                {
                    offsets[i] = array.GetLampInfo(i).Position.X / width * 360;
                }
                double value = look.Color.R / 255.0;
                var colors = new Color[all.Length];
                var custom = new LampArrayCustomEffect(array, all)
                {
                    Duration = TimeSpan.FromDays(1),
                    UpdateInterval = TimeSpan.FromMilliseconds(Math.Max(33, array.MinUpdateInterval.TotalMilliseconds)),
                };
                custom.UpdateRequested += (_, e) =>
                {
                    double turn = e.SinceStarted.TotalSeconds / (period * 2) * 360;
                    for (int i = 0; i < colors.Length; i++)
                    {
                        colors[i] = Hue((turn + offsets[i]) % 360, value);
                    }
                    e.SetColorsForIndices(colors, all);
                };
                lamp.Playlist = Play(custom);
                break;
            }
            default:
                array.SetColor(look.Color);
                break;
        }
    }

    private static LampArrayEffectPlaylist Play(ILampArrayEffect effect)
    {
        var playlist = new LampArrayEffectPlaylist { RepetitionMode = LampArrayRepetitionMode.Forever };
        playlist.Append(effect);
        playlist.Start();
        return playlist;
    }

    private static void Stop(Lamp lamp)
    {
        try
        {
            lamp.Playlist?.Stop();
        }
        catch (Exception)
        {
        }
        lamp.Playlist = null;
    }

    private static Color Hue(double h, double v)
    {
        double x = v * (1 - Math.Abs(h / 60 % 2 - 1));
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (v, x, 0.0),
            1 => (x, v, 0.0),
            2 => (0.0, v, x),
            3 => (0.0, x, v),
            4 => (x, 0.0, v),
            _ => (v, 0.0, x),
        };
        static byte B(double f) => (byte)Math.Clamp(Math.Round(f * 255), 0, 255);
        return Color.FromArgb(255, B(r), B(g), B(b));
    }
}
