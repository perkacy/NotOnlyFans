using NotOnlyFans.Contracts;

namespace NotOnlyFans.Services;

/// <summary>
/// A group on the Sensors page (one card) and which sensors it shows, in order.
/// The first <see cref="EssentialCount"/> sensors are always visible; the rest sit behind "Show all".
/// </summary>
public sealed record SensorGroupInfo(string Id, string Title, string Subtitle, IReadOnlyList<int> SensorIndexes, int EssentialCount);

/// <summary>
/// Decides how the sensors from the service are grouped and named in the UI.
/// Works from what the service reports (hardware type, sensor type) - nothing is specific to one PC.
/// </summary>
public static class SensorLayout
{
    private static readonly string[] KindOrder =
    [
        "Temperature", "Fan", "Control", "Load", "Clock", "Power", "Voltage", "Current",
        "Energy", "Data", "SmallData", "Throughput", "Level", "Factor",
    ];

    /// <summary>Fixed facts (memory timings) - not sensors, left out of the list.</summary>
    private static bool IsHiddenKind(string kind) => kind == "Timing";

    public static IReadOnlyList<SensorGroupInfo> BuildGroups(SensorCatalog catalog)
    {
        var hardware = Index(catalog);
        string? boardName = catalog.Hardware.FirstOrDefault(h => h.Kind == "Motherboard")?.Name;
        int hwOrder(string id) => catalog.Hardware.FindIndex(h => h.Id == id);

        var groups = new Dictionary<string, (int Rank, string Title, string Subtitle, List<int> Sensors)>();

        for (int i = 0; i < catalog.Sensors.Count; i++)
        {
            var s = catalog.Sensors[i];
            if (IsHiddenKind(s.Kind) || !hardware.TryGetValue(s.HardwareId, out var hw))
            {
                continue;
            }

            var (id, rank, title, subtitle) = GroupFor(hw, s, boardName);
            if (!groups.TryGetValue(id, out var g))
            {
                g = (rank, title, subtitle, []);
                groups[id] = g;
            }
            g.Sensors.Add(i);
        }

        return groups
            .OrderBy(g => g.Value.Rank)
            .Select(g =>
            {
                var sensors = g.Value.Sensors;
                IEnumerable<int> ordered = g.Key == "fans"
                    // Fan headers: each fan's RPM right above its speed %.
                    ? sensors.OrderBy(i => catalog.Sensors[i].Index).ThenBy(i => KindRank(catalog.Sensors[i].Kind))
                    : sensors.OrderBy(i => hwOrder(catalog.Sensors[i].HardwareId))
                             .ThenBy(i => KindRank(catalog.Sensors[i].Kind))
                             .ThenBy(i => IsHeadline(catalog.Sensors[i]) ? 0 : 1)
                             .ThenBy(i => i);

                // Essential sensors first (keeping their order), everything else after them, behind "Show all".
                var list = ordered.ToList();
                var essential = list.Where(i => IsEssential(catalog, hardware, i)).ToList();
                var rest = list.Where(i => !IsEssential(catalog, hardware, i));
                return new SensorGroupInfo(g.Key, g.Value.Title, g.Value.Subtitle, (List<int>)[.. essential, .. rest], essential.Count);
            })
            .ToList();
    }

    /// <summary>
    /// What a person actually looks at: temperatures, fans (RPM and speed %) and the overall usage
    /// of each processor, graphics card and drive. Everything else stays available under "Show all".
    /// </summary>
    private static bool IsEssential(SensorCatalog catalog, Dictionary<string, HardwareInfo> hardware, int index)
    {
        var s = catalog.Sensors[index];
        hardware.TryGetValue(s.HardwareId, out var hw);

        switch (s.Kind)
        {
            case "Fan" or "Control":
                return true;

            case "Temperature":
                // Not real readings: distance-to-limit values and the drive's fixed warning thresholds.
                return SafetyRule.Counts(s.Name);

            case "Load":
                return hw?.Kind switch
                {
                    "Cpu" => s.Name == "CPU Total",
                    "GpuNvidia" or "GpuAmd" => s.Name == "GPU Core",
                    // Integrated Intel graphics has no "GPU Core" load; its 3D engine load is the equivalent.
                    "GpuIntel" => s.Name is "GPU Core" or "D3D 3D",
                    "Storage" => s.Name == "Total Activity",
                    "Memory" => s.Name == "Memory" && !hw.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };

            default:
                return false;
        }
    }

    /// <summary>The single most telling reading of a device goes to the top of its card (e.g. CPU Package).</summary>
    private static bool IsHeadline(SensorInfo s) =>
        s.Kind == "Temperature" && s.Name is "CPU Package" or "Core (Tctl/Tdie)" or "GPU Core" or "Composite Temperature";

    /// <summary>
    /// The name to show: the user's name if they gave one, else the original from the hardware.
    /// A fan's speed (%) sensor shares the name of its RPM sensor.
    /// </summary>
    public static string DisplayName(SensorCatalog catalog, int sensorIndex) =>
        CustomName(catalog, sensorIndex) ?? catalog.Sensors[sensorIndex].Name;

    /// <summary>The user's name for this sensor (or for the fan it belongs to), or null.</summary>
    public static string? CustomName(SensorCatalog catalog, int sensorIndex)
    {
        var s = catalog.Sensors[sensorIndex];
        if (s.Kind == "Control" && FanFor(catalog, sensorIndex) is int fan and >= 0)
        {
            return App.Settings.GetName(catalog.Sensors[fan].Key);
        }
        return App.Settings.GetName(s.Key);
    }

    /// <summary>The RPM sensor (same device, same name) of a fan speed (%) sensor; -1 if none.</summary>
    public static int FanFor(SensorCatalog catalog, int controlIndex)
    {
        var c = catalog.Sensors[controlIndex];
        for (int i = 0; i < catalog.Sensors.Count; i++)
        {
            var s = catalog.Sensors[i];
            if (s.Kind == "Fan" && s.HardwareId == c.HardwareId && s.Name == c.Name)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Row name inside its group.</summary>
    public static string RowName(SensorCatalog catalog, SensorGroupInfo group, int sensorIndex)
    {
        var s = catalog.Sensors[sensorIndex];
        string display = DisplayName(catalog, sensorIndex);
        string name = s.Kind == "Control" ? $"{display} · speed" : display;

        // Groups that mix several devices (e.g. Memory) say which device a row belongs to.
        bool mixed = group.SensorIndexes.Select(i => catalog.Sensors[i].HardwareId).Distinct().Skip(1).Any();
        if (mixed && group.Id != "fans" && Index(catalog).TryGetValue(s.HardwareId, out var hw))
        {
            name = $"{hw.Name} · {name}";
        }
        return name;
    }

    /// <summary>Short label for the sidebar ("CPU Package", "GPU Hot Spot", "System Fan #2").</summary>
    public static string PinLabel(SensorCatalog catalog, int sensorIndex)
    {
        var s = catalog.Sensors[sensorIndex];
        if (CustomName(catalog, sensorIndex) is { } custom)
        {
            // The user's own name is shown as given, without device prefixes.
            return s.Kind == "Control" ? $"{custom} speed" : custom;
        }

        string name = s.Kind == "Control" ? $"{s.Name} speed" : s.Name;
        Index(catalog).TryGetValue(s.HardwareId, out var hw);

        return hw?.Kind switch
        {
            "Cpu" when !name.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) => $"CPU {name}",
            "GpuNvidia" or "GpuAmd" or "GpuIntel" when !name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase) => $"GPU {name}",
            "Storage" when name.Contains("Composite", StringComparison.OrdinalIgnoreCase) => hw.Name,
            "Storage" => $"{hw.Name} {name}",
            _ => name,
        };
    }

    /// <summary>
    /// Pins for the very first start: the main CPU and GPU temperatures, if this PC has them.
    /// After that the user decides.
    /// </summary>
    public static IEnumerable<int> DefaultPins(SensorCatalog catalog)
    {
        int cpu = TemperatureSources.Cpu(catalog);
        int gpu = TemperatureSources.Gpu(catalog);
        int hotSpot = gpu < 0 ? -1
            : TemperatureSources.Find(catalog, h => h.Id == catalog.Sensors[gpu].HardwareId, "GPU Hot Spot");
        return new[] { cpu, gpu, hotSpot }.Where(i => i >= 0);
    }

    /// <summary>The Sensors card of a device's own readings (temperatures, load…), e.g. for a Home tile.</summary>
    public static string GroupId(HardwareInfo hw) => GroupFor(hw, new SensorInfo { Kind = "Temperature" }, null).Id;

    private static (string Id, int Rank, string Title, string Subtitle) GroupFor(HardwareInfo hw, SensorInfo s, string? boardName) =>
        hw.Kind switch
        {
            "Cpu" => ($"cpu:{hw.Id}", 0, "CPU", hw.Name),
            "GpuNvidia" or "GpuAmd" => ($"gpu:{hw.Id}", 1, "GPU", hw.Name),
            "GpuIntel" => ($"gpu:{hw.Id}", 2, "GPU", hw.Name),
            "SuperIO" when s.Kind is "Fan" or "Control" => ("fans", 4, "Fans", "Motherboard fan headers"),
            "SuperIO" or "Motherboard" => ("board", 3, "Motherboard", boardName is null || boardName == hw.Name ? hw.Name : $"{boardName} · {hw.Name}"),
            "Memory" => ("memory", 5, "Memory", "System memory"),
            "Storage" => ($"drive:{hw.Id}", 6, "Drive", hw.Name),
            _ => ($"hw:{hw.Id}", 7, hw.Name, hw.Kind),
        };

    private static int KindRank(string kind)
    {
        int i = Array.IndexOf(KindOrder, kind);
        return i < 0 ? KindOrder.Length : i;
    }

    private static Dictionary<string, HardwareInfo> Index(SensorCatalog catalog)
    {
        var map = new Dictionary<string, HardwareInfo>(StringComparer.Ordinal);
        foreach (var h in catalog.Hardware)
        {
            map.TryAdd(h.Id, h);
        }
        return map;
    }
}
