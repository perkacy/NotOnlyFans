using System.Text.Json.Serialization;

namespace NotOnlyFans.Contracts;

/// <summary>
/// Sensor stream: NotOnlyFans.Service → window app, over a named pipe.
/// Each message is one line of UTF-8 JSON.
/// On connect the service sends the catalog (what sensors exist) and the latest values;
/// after that a values message after every poll, and a new catalog only when hardware changes.
/// </summary>
public static class SensorPipe
{
    public const string Name = "NotOnlyFans.Sensors.v1";

    public const string CatalogType = "catalog";
    public const string ValuesType = "values";
}

public sealed class PipeMessage
{
    public string Type { get; set; } = "";
    public SensorCatalog? Catalog { get; set; }
    public SensorValues? Values { get; set; }

    /// <summary>Fan control settings and safety state (type "control"), sent on connect and on every change.</summary>
    public ControlState? Control { get; set; }

    /// <summary>Lights and their settings (type "colors"), sent on connect and on every change.</summary>
    public ColorState? Colors { get; set; }

    /// <summary>Graphics card overclocking (type "oc"), sent on connect and on every change.</summary>
    public OcState? Oc { get; set; }
}

/// <summary>Everything the service can see. Values messages refer to sensors by position in <see cref="Sensors"/>.</summary>
public sealed class SensorCatalog
{
    public int Version { get; set; }
    public List<HardwareInfo> Hardware { get; set; } = [];
    public List<SensorInfo> Sensors { get; set; } = [];
}

public sealed class HardwareInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>LibreHardwareMonitor hardware type, e.g. "Cpu", "GpuNvidia", "SuperIO", "Storage".</summary>
    public string Kind { get; set; } = "";

    public string? ParentId { get; set; }
}

public sealed class SensorInfo
{
    /// <summary>
    /// Unique, stable key (saved in settings, e.g. pins).
    /// Usually the LibreHardwareMonitor identifier, but those are not always unique,
    /// so duplicates get the sensor name appended.
    /// </summary>
    public string Key { get; set; } = "";

    public string HardwareId { get; set; } = "";

    /// <summary>LibreHardwareMonitor sensor type, e.g. "Temperature", "Fan", "Control", "Load".</summary>
    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";
    public int Index { get; set; }

    /// <summary>True when the sensor can be driven (fan speed).</summary>
    public bool HasControl { get; set; }

    /// <summary>Lowest / highest speed (%) the driver says it accepts for this control (e.g. a graphics card's 30%).</summary>
    public float? ControlMinimum { get; set; }

    public float? ControlMaximum { get; set; }
}

/// <summary>One poll. Arrays are parallel to <see cref="SensorCatalog.Sensors"/>; null = no reading.</summary>
public sealed class SensorValues
{
    public int CatalogVersion { get; set; }
    public DateTimeOffset Time { get; set; }
    public float?[] Now { get; set; } = [];
    public float?[] Min { get; set; } = [];
    public float?[] Max { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PipeMessage))]
[JsonSerializable(typeof(ControlState))]
[JsonSerializable(typeof(ColorState))]
[JsonSerializable(typeof(LightSetting))]
[JsonSerializable(typeof(OcState))]
[JsonSerializable(typeof(Dictionary<string, OcSetting>))]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlReply))]
public sealed partial class ContractsJson : JsonSerializerContext
{
}
