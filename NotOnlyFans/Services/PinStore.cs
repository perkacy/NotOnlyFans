using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotOnlyFans.Services;

public sealed class PinnedSensor
{
    public string Key { get; set; } = "";

    /// <summary>Saved with the pin so the sidebar shows it instantly at start-up, before the service answers.</summary>
    public string Label { get; set; } = "";
}

/// <summary>
/// Sensors pinned to the sidebar. Saved to %LOCALAPPDATA%\NotOnlyFans\pins.json.
/// </summary>
public sealed class PinStore
{
    private const string FileName = "pins.json";

    private readonly List<PinnedSensor> _pins;
    private bool _hasSavedState;

    private PinStore(List<PinnedSensor>? pins)
    {
        _pins = pins?.Where(p => p is not null && !string.IsNullOrEmpty(p.Key)).DistinctBy(p => p.Key).ToList() ?? [];
        foreach (var pin in _pins)
        {
            pin.Label ??= "";
        }
        _hasSavedState = pins is not null;
    }

    public IReadOnlyList<PinnedSensor> Pins => _pins;

    public event Action? Changed;

    public static PinStore Load() => new(AppFiles.Load(FileName, AppJson.Default.ListPinnedSensor));

    public bool IsPinned(string key) => _pins.Exists(p => p.Key == key);

    public void Toggle(string key, string label)
    {
        int i = _pins.FindIndex(p => p.Key == key);
        if (i >= 0)
        {
            _pins.RemoveAt(i);
        }
        else
        {
            _pins.Add(new PinnedSensor { Key = key, Label = label });
        }
        Save();
        Changed?.Invoke();
    }

    public void Unpin(string key)
    {
        if (_pins.RemoveAll(p => p.Key == key) > 0)
        {
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>Moves a pin one place up (-1) or down (+1) in the sidebar.</summary>
    public void Move(string key, int by)
    {
        int i = _pins.FindIndex(p => p.Key == key);
        int to = i + by;
        if (i < 0 || to < 0 || to >= _pins.Count)
        {
            return;
        }
        (_pins[i], _pins[to]) = (_pins[to], _pins[i]);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Puts a pin at <paramref name="index"/> (dragged in the sidebar); the others close up around it.</summary>
    public void MoveTo(string key, int index)
    {
        int i = _pins.FindIndex(p => p.Key == key);
        if (i < 0 || index < 0 || index >= _pins.Count || index == i)
        {
            return;
        }
        var pin = _pins[i];
        _pins.RemoveAt(i);
        _pins.Insert(index, pin);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Refreshes saved labels (after a rename). Saves only if something changed.</summary>
    public void UpdateLabels(Func<string, string?> labelForKey)
    {
        bool changed = false;
        foreach (var pin in _pins)
        {
            if (labelForKey(pin.Key) is { } label && label != pin.Label)
            {
                pin.Label = label;
                changed = true;
            }
        }
        if (changed)
        {
            Save();
        }
    }

    /// <summary>On the very first run (nothing saved yet) pin a sensible default set.</summary>
    public void EnsureDefaults(IEnumerable<PinnedSensor> defaults)
    {
        if (_hasSavedState)
        {
            return;
        }
        _pins.AddRange(defaults);
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        _hasSavedState = true;
        AppFiles.Save(FileName, _pins, AppJson.Default.ListPinnedSensor);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<PinnedSensor>))]
[JsonSerializable(typeof(UserSettingsData))]
internal sealed partial class AppJson : JsonSerializerContext
{
}
