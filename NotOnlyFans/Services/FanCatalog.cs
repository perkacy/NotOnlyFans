using NotOnlyFans.Contracts;

namespace NotOnlyFans.Services;

/// <summary>
/// One fan: its RPM sensor and, if it can be driven, its speed (%) sensor.
/// <see cref="Key"/> (the RPM sensor) identifies the fan in the app's settings (name, order);
/// <see cref="ControlKey"/> identifies it in the service's fan settings.
/// </summary>
public sealed record FanInfo(string Key, string? ControlKey, int Rpm, int Control, string OriginalName, string Source, bool IsGpu);

/// <summary>The fans of this PC, worked out from what the service reports (motherboard headers, then graphics cards).</summary>
public static class FanCatalog
{
    // A header driven hard that has never turned has nothing connected (a fan stopped at 0% is still listed).
    private const float EmptyHeaderDuty = 30;

    public static List<FanInfo> All(SensorCatalog catalog)
    {
        var fans = new List<FanInfo>();
        foreach (var hw in catalog.Hardware.Where(h => h.Kind == "SuperIO"))
        {
            AddFans(catalog, hw, "Motherboard header", isGpu: false, fans);
        }
        foreach (var hw in catalog.Hardware.Where(h => h.Kind is "GpuNvidia" or "GpuAmd" or "GpuIntel"))
        {
            AddFans(catalog, hw, "Graphics card", isGpu: true, fans);
        }
        return fans;
    }

    /// <summary>The user's order first (Home › Fans), then fans not placed yet, in detected order.</summary>
    public static IEnumerable<FanInfo> Ordered(IEnumerable<FanInfo> fans)
    {
        var order = App.Settings.FanOrder;
        return fans
            .Select((fan, detected) => (fan, detected, saved: IndexOf(order, fan.Key)))
            .OrderBy(x => x.saved < 0 ? int.MaxValue : x.saved)
            .ThenBy(x => x.detected)
            .Select(x => x.fan);
    }

    /// <summary>
    /// Saves the order the user made in a list (Home or Cooling). Fans not on screen (empty headers)
    /// keep their place after the shown ones.
    /// </summary>
    public static void SaveOrder(IEnumerable<FanInfo> all, IEnumerable<string> shownKeys)
    {
        var shown = shownKeys.ToList();
        var hidden = Ordered(all).Select(f => f.Key).Where(k => !shown.Contains(k)).ToList();
        App.Settings.SetFanOrder(shown.Concat(hidden));
    }

    public static bool IsEmptyHeader(FanInfo fan) => IsEmptyHeader(fan.Rpm, fan.Control);

    /// <summary>By sensor index: the RPM sensor has never turned while its speed % was driven hard.</summary>
    public static bool IsEmptyHeader(int rpm, int control) =>
        Max(rpm) is 0 && Now(control) >= EmptyHeaderDuty;

    /// <summary>The fan's setting in the service (BIOS when it has none).</summary>
    public static FanSetting Setting(FanInfo fan) =>
        fan.ControlKey is { } key && App.Sensors.Control?.Fans.TryGetValue(key, out var s) == true
            ? s
            : new FanSetting { Mode = FanMode.Bios };

    /// <summary>Short mode label for lists: "BIOS" / "Auto" (graphics card), "Fixed", or "Safety" while the safety rule runs it at 100%.</summary>
    public static string ModeText(FanInfo fan)
    {
        var setting = Setting(fan);
        if (setting.Mode == FanMode.Bios)
        {
            return fan.IsGpu ? "Auto" : "BIOS";
        }
        if (App.Sensors.Control?.Emergency == true)
        {
            return "Safety";
        }
        if (App.Sensors.Control?.Calibration is { Status: CalibrationStatus.Running } c && c.Key == fan.ControlKey)
        {
            return "Calibrating";
        }
        return setting.Mode switch
        {
            FanMode.Fixed => "Fixed",
            _ => setting.Mode.ToString(),
        };
    }

    public static float? Now(int index) => index < 0 ? null : App.Sensors.GetNow(index);

    public static float? Max(int index) =>
        index >= 0 && App.Sensors.Values is { } v && index < v.Max.Length ? v.Max[index] : null;

    private static void AddFans(SensorCatalog catalog, HardwareInfo hw, string source, bool isGpu, List<FanInfo> fans)
    {
        for (int i = 0; i < catalog.Sensors.Count; i++)
        {
            var s = catalog.Sensors[i];
            if (s.HardwareId != hw.Id || s.Kind != "Fan")
            {
                continue;
            }
            int control = -1;
            for (int j = 0; j < catalog.Sensors.Count; j++)
            {
                var c = catalog.Sensors[j];
                if (c.HardwareId == hw.Id && c.Kind == "Control" && c.Name == s.Name)
                {
                    control = j;
                    break;
                }
            }
            string? controlKey = control >= 0 && catalog.Sensors[control].HasControl ? catalog.Sensors[control].Key : null;
            fans.Add(new FanInfo(s.Key, controlKey, i, control, s.Name, source, isGpu));
        }
    }

    private static int IndexOf(IReadOnlyList<string> list, string key)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == key)
            {
                return i;
            }
        }
        return -1;
    }
}
