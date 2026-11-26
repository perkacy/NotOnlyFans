using System.Text.Json;

namespace NotOnlyFans.Services;

public sealed class UserSettingsData
{
    /// <summary>Names the user gave, by sensor key. For a fan the key is its RPM sensor; its speed (%) follows the same name.</summary>
    public Dictionary<string, string> Names { get; set; } = [];

    /// <summary>Order of fans on Home, by RPM sensor key. Fans not listed go after these, in detected order.</summary>
    public List<string> FanOrder { get; set; } = [];

    /// <summary>Sensors the user hid on the Sensors page, by sensor key.</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>The close button hides the window to the tray (the app keeps running) instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>The "still running in the tray" notification was shown once already.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>App theme: "System" (follows Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = Services.Theme.System;

    /// <summary>The game overlay (pinned readings over games) is on.</summary>
    public bool OverlayShown { get; set; }

    /// <summary>Where the user put the overlay (screen pixels, top-left corner); null = the default corner.</summary>
    public int? OverlayX { get; set; }

    public int? OverlayY { get; set; }
}

/// <summary>
/// What the user changed in the app (names, order). Saved to %LOCALAPPDATA%\NotOnlyFans\settings.json.
/// The original names from the hardware stay the default; a name the user clears goes back to the original.
/// </summary>
public sealed class UserSettings
{
    private const string FileName = "settings.json";

    private readonly UserSettingsData _data;

    private UserSettings(UserSettingsData data)
    {
        // A hand-edited or damaged file may have nulls: never let that break the app.
        data.Names = data.Names?.Where(n => n.Value is not null).ToDictionary() ?? [];
        data.FanOrder = data.FanOrder?.Where(k => k is not null).ToList() ?? [];
        data.Hidden = data.Hidden?.Where(k => k is not null).ToList() ?? [];
        data.Theme ??= Services.Theme.System;
        _data = data;
    }

    public event Action? NamesChanged;

    public IReadOnlyList<string> FanOrder => _data.FanOrder;

    public static UserSettings Load() => new(AppFiles.Load(FileName, AppJson.Default.UserSettingsData) ?? new UserSettingsData());

    public string? GetName(string key) => _data.Names.TryGetValue(key, out var name) ? name : null;

    /// <summary>Sets a custom name; null, empty or the original name removes it.</summary>
    public void SetName(string key, string? name, string originalName)
    {
        name = name?.Trim();
        bool changed;
        if (string.IsNullOrEmpty(name) || name == originalName)
        {
            changed = _data.Names.Remove(key);
        }
        else
        {
            changed = GetName(key) != name;
            _data.Names[key] = name;
        }

        if (changed)
        {
            Save();
            NamesChanged?.Invoke();
        }
    }

    public event Action? HiddenChanged;

    public bool IsHidden(string key) => _data.Hidden.Contains(key);

    public int HiddenCount => _data.Hidden.Count;

    public void SetHidden(string key, bool hidden)
    {
        bool changed = hidden ? !_data.Hidden.Contains(key) : _data.Hidden.Remove(key);
        if (hidden && changed)
        {
            _data.Hidden.Add(key);
        }
        if (changed)
        {
            Save();
            HiddenChanged?.Invoke();
        }
    }

    public bool CloseToTray
    {
        get => _data.CloseToTray;
        set
        {
            if (_data.CloseToTray != value)
            {
                _data.CloseToTray = value;
                Save();
            }
        }
    }

    public bool OverlayShown
    {
        get => _data.OverlayShown;
        set
        {
            if (_data.OverlayShown != value)
            {
                _data.OverlayShown = value;
                Save();
            }
        }
    }

    public (int X, int Y)? OverlayPosition
    {
        get => _data.OverlayX is { } x && _data.OverlayY is { } y ? (x, y) : null;
        set
        {
            _data.OverlayX = value?.X;
            _data.OverlayY = value?.Y;
            Save();
        }
    }

    public bool TrayHintShown
    {
        get => _data.TrayHintShown;
        set
        {
            if (_data.TrayHintShown != value)
            {
                _data.TrayHintShown = value;
                Save();
            }
        }
    }

    public event Action? ThemeChanged;

    public string Theme
    {
        get => _data.Theme;
        set
        {
            if (_data.Theme != value)
            {
                _data.Theme = value;
                Save();
                ThemeChanged?.Invoke();
            }
        }
    }

    public void SetFanOrder(IEnumerable<string> keys)
    {
        _data.FanOrder = keys.Distinct().ToList();
        Save();
    }

    private void Save() => AppFiles.Save(FileName, _data, AppJson.Default.UserSettingsData);
}
