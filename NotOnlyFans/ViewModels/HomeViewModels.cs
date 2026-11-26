namespace NotOnlyFans.ViewModels;

/// <summary>A small labelled value on a Home tile ("VRM MOS 36 °C", "Fan 2 56% · 772 RPM").</summary>
public sealed class ValueChipViewModel(string label) : Observable
{
    private string _label = label;
    private string _value = Services.SensorFormat.NoValue;

    public string Label { get => _label; set => Set(ref _label, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
}

/// <summary>One drive on the Drives tile.</summary>
public sealed class DriveViewModel(string name) : Observable
{
    private string _detail = "";
    private string _temperature = Services.SensorFormat.NoValue;

    public string Name { get; } = name;
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Temperature { get => _temperature; set => Set(ref _temperature, value); }
}

/// <summary>One fan in the Fans list on Home.</summary>
public sealed class FanRowViewModel(string key, string originalName, string source, string mode) : Observable
{
    public const double BarLength = 140;

    private string _name = originalName;
    private string _rpm = Services.SensorFormat.NoValue;
    private string _percent = "";
    private double _barWidth;

    /// <summary>Key of the fan's RPM sensor: identifies the fan in settings (name, order).</summary>
    public string Key { get; } = key;

    /// <summary>Name from the hardware ("System Fan #2"), shown under a custom name so the header stays known.</summary>
    public string OriginalName { get; } = originalName;

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                Raise(nameof(Detail));
                Raise(nameof(IsRenamed));
            }
        }
    }

    public bool IsRenamed => _name != OriginalName;

    public string Source { get; } = source;

    /// <summary>Second line: where the fan is connected; after a rename also its original header name.</summary>
    public string Detail => IsRenamed ? $"{OriginalName} · {Source}" : Source;

    private string _mode = mode;

    /// <summary>Who drives the fan now: "BIOS" / "Auto" (graphics card), "Fixed", or "Safety" (100% by the safety rule).</summary>
    public string Mode { get => _mode; set => Set(ref _mode, value); }

    public string Rpm { get => _rpm; set => Set(ref _rpm, value); }
    public string Percent { get => _percent; set => Set(ref _percent, value); }
    public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }

    private bool _isPinned;

    /// <summary>The fan's speed is pinned to the sidebar (Cooling's pin button; Sensors pins the same reading).</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (Set(ref _isPinned, value))
            {
                Raise(nameof(IsUnpinned));
                Raise(nameof(PinText));
            }
        }
    }

    public bool IsUnpinned => !_isPinned;

    public string PinText => _isPinned ? "Unpin from sidebar" : "Pin to sidebar";
}
