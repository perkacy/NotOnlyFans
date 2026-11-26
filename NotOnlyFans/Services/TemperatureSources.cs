using NotOnlyFans.Contracts;

namespace NotOnlyFans.Services;

/// <summary>
/// The temperatures a fan curve or a light can follow, as one list for the "Temperature source" boxes:
/// "Highest of CPU and GPU" first (when both exist), then every real temperature sensor in Sensors-page order.
/// </summary>
public static class TemperatureSources
{
    public static List<(string Label, List<string> Keys)> Build(SensorCatalog catalog)
    {
        var hardware = catalog.Hardware.ToDictionary(h => h.Id);
        var sources = new List<(string Label, List<string> Keys)>();

        int cpu = Cpu(catalog);
        int gpu = Gpu(catalog);
        if (cpu >= 0 && gpu >= 0)
        {
            sources.Add(("Highest of CPU and GPU", [catalog.Sensors[cpu].Key, catalog.Sensors[gpu].Key]));
        }

        foreach (var g in SensorLayout.BuildGroups(catalog))
        {
            foreach (int i in g.SensorIndexes)
            {
                var s = catalog.Sensors[i];
                if (s.Kind != "Temperature" || !SafetyRule.Counts(s.Name))
                {
                    continue;
                }
                string label = hardware.TryGetValue(s.HardwareId, out var hw) && hw.Kind == "SuperIO"
                    ? $"Motherboard · {SensorLayout.DisplayName(catalog, i)}"
                    : SensorLayout.PinLabel(catalog, i);
                sources.Add((label, [s.Key]));
            }
        }
        return sources;
    }

    /// <summary>The CPU's main temperature (Intel package / AMD Tctl), or -1.</summary>
    public static int Cpu(SensorCatalog catalog) =>
        Find(catalog, h => h.Kind == "Cpu", "CPU Package", "Core (Tctl/Tdie)", "Core Max");

    /// <summary>
    /// The graphics card's core temperature, or -1. A card with its own fans first (integrated graphics has none,
    /// so a PC with both picks the real card), then any graphics.
    /// </summary>
    public static int Gpu(SensorCatalog catalog)
    {
        var withFans = catalog.Sensors.Where(s => s.Kind is "Fan" or "Control").Select(s => s.HardwareId).ToHashSet();
        int i = Find(catalog, h => IsGpu(h) && withFans.Contains(h.Id), "GPU Core");
        return i >= 0 ? i : Find(catalog, IsGpu, "GPU Core");
    }

    public static bool IsGpu(HardwareInfo h) => h.Kind is "GpuNvidia" or "GpuAmd" or "GpuIntel";

    /// <summary>Index of the first temperature sensor with one of the names (in order) on matching hardware, or -1.</summary>
    public static int Find(SensorCatalog catalog, Func<HardwareInfo, bool> hardware, params string[] names)
    {
        var ids = catalog.Hardware.Where(hardware).Select(h => h.Id).ToHashSet();
        foreach (var name in names)
        {
            int i = catalog.Sensors.FindIndex(s => s.Kind == "Temperature" && s.Name == name && ids.Contains(s.HardwareId));
            if (i >= 0)
            {
                return i;
            }
        }
        return -1;
    }
}
