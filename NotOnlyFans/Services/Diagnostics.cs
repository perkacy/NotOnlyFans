using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NotOnlyFans.Contracts;
using Windows.ApplicationModel;

namespace NotOnlyFans.Services;

/// <summary>
/// "Copy diagnostics" in Settings: a plain-text report for a bug report from another PC – what the service
/// found (hardware, sensors, fans, lights, graphics cards), what NotOnlyFans drives, what may get in the way
/// (missing driver, Smart App Control, other fan or RGB software) and recent errors.
/// No user or computer names: they are replaced in the whole text.
/// </summary>
internal static partial class Diagnostics
{
    // Programs that also drive fans or lights, or read the same sensor chips (process name → shown name).
    private static readonly (string Process, string Name)[] OtherSoftware =
    [
        ("FanControl", "Fan Control"),
        ("MSI.CentralServer", "MSI Center"),
        ("MSI_Central_Service", "MSI Center"),
        ("LEDKeeper2", "MSI Mystic Light"),
        ("MSIAfterburner", "MSI Afterburner"),
        ("ArmouryCrate.Service", "ASUS Armoury Crate"),
        ("LightingService", "ASUS Aura"),
        ("GCC", "Gigabyte Control Center"),
        ("RGBFusion", "Gigabyte RGB Fusion"),
        ("iCUE", "Corsair iCUE"),
        ("NZXT CAM", "NZXT CAM"),
        ("SignalRgb", "SignalRGB"),
        ("OpenRGB", "OpenRGB"),
        ("HWiNFO64", "HWiNFO"),
        ("HWiNFO32", "HWiNFO"),
        ("aida64", "AIDA64"),
        ("SpeedFan", "SpeedFan"),
        ("ArgusMonitor", "Argus Monitor"),
        ("LibreHardwareMonitor", "LibreHardwareMonitor"),
        ("OpenHardwareMonitor", "OpenHardwareMonitor"),
        ("AMDRSServ", "AMD Software (Adrenalin)"),
        ("IntelGraphicsSoftware", "Intel Graphics Software"),
        ("ArcControl", "Intel Arc Control"),
    ];

    private static CultureInfo Inv => CultureInfo.InvariantCulture;

    /// <summary>The report. Call on the UI thread (it reads what the app holds); the slow parts run in the background.</summary>
    public static async Task<string> BuildAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Inv, $"NotOnlyFans diagnostics · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        AppPart(sb);
        string system = await Task.Run(SystemPart);   // registry, processes, event log: off the UI thread
        sb.Append(system);
        HardwarePart(sb);
        FansPart(sb);
        ColorsPart(sb);
        OcPart(sb);
        sb.Append(await Task.Run(ErrorsPart));
        return Scrub(sb.ToString());
    }

    // ----- What the app knows -----

    private static void AppPart(StringBuilder sb)
    {
        sb.AppendLine("[App]");
        try
        {
            var v = Package.Current.Id.Version;
            sb.AppendLine(Inv, $"Version: {v.Major}.{v.Minor}.{v.Build}.{v.Revision} ({Package.Current.DisplayName})");
        }
        catch (InvalidOperationException)
        {
            sb.AppendLine("Version: not packaged");
        }
        sb.AppendLine(Inv, $".NET: {Environment.Version}");
        var sensors = App.Sensors;
        sb.AppendLine("Service: " + (sensors.IsConnected ? "connected" : sensors.IsServiceMissing ? "not running" : "connecting"));
        sb.AppendLine();
    }

    private static void HardwarePart(StringBuilder sb)
    {
        sb.AppendLine("[Hardware and sensors]");
        if (App.Sensors.Catalog is not { } catalog)
        {
            sb.AppendLine("No data from the service.");
            sb.AppendLine();
            return;
        }
        var values = App.Sensors.Values;
        foreach (var hw in catalog.Hardware)
        {
            string parent = hw.ParentId is null ? "" : $" (in {hw.ParentId})";
            sb.AppendLine(Inv, $"{hw.Kind}: {hw.Name}  id={hw.Id}{parent}");
            for (int i = 0; i < catalog.Sensors.Count; i++)
            {
                var s = catalog.Sensors[i];
                if (s.HardwareId != hw.Id)
                {
                    continue;
                }
                float? now = values is not null && i < values.Now.Length ? values.Now[i] : null;
                float? max = values is not null && i < values.Max.Length ? values.Max[i] : null;
                string control = s.HasControl ? string.Create(Inv, $"  control {s.ControlMinimum:0}–{s.ControlMaximum:0}") : "";
                sb.AppendLine(Inv, $"    {s.Kind,-12} {s.Name,-32} {SensorFormat.Format(s.Kind, now),-12} max {SensorFormat.Format(s.Kind, max),-12} {s.Key}{control}");
            }
        }
        sb.AppendLine();
    }

    private static void FansPart(StringBuilder sb)
    {
        sb.AppendLine("[Fan control]");
        var control = App.Sensors.Control;
        if (control is null || App.Sensors.Catalog is not { } catalog)
        {
            sb.AppendLine("No data from the service.");
            sb.AppendLine();
            return;
        }
        sb.AppendLine(Inv, $"Emergency temperature: {control.SafetyTemperature:0} °C{(control.Emergency ? $" – ACTIVE ({control.EmergencySensor})" : "")}");
        foreach (var fan in FanCatalog.All(catalog))
        {
            var s = FanCatalog.Setting(fan);
            string rpm = SensorFormat.Format("Fan", FanCatalog.Now(fan.Rpm));
            string duty = fan.Control < 0 ? "no control" : SensorFormat.Format("Control", FanCatalog.Now(fan.Control));
            string empty = FanCatalog.IsEmptyHeader(fan) ? ", nothing connected?" : "";
            sb.AppendLine(Inv, $"{fan.OriginalName} ({fan.Source}): {rpm}, {duty}{empty} · {FanCatalog.ModeText(fan)}");
            if (s.Mode != FanMode.Bios)
            {
                sb.AppendLine(Inv, $"    sources {string.Join(", ", s.Sources)}; start {s.StartSpeed:0}%, min {s.MinSpeed:0}%, stop at 0% {(s.AllowStop ? "yes" : "no")}, hysteresis {s.Hysteresis:0.#} °C, delay {s.DelayDown:0.#} s, smooth {(s.Smooth ? string.Create(Inv, $"{s.MaxChangePerSecond:0.#}%/s") : "off")}");
            }
            if (fan.ControlKey is { } key)
            {
                if (control.ZeroStop.TryGetValue(key, out var zero))
                {
                    sb.AppendLine(Inv, $"    0%: {zero}");
                }
                if (control.Calibrations.TryGetValue(key, out var cal))
                {
                    sb.AppendLine(Inv, $"    calibrated {cal.Time:yyyy-MM-dd}: max {cal.MaxRpm:0} RPM, {(cal.Stops ? "stops" : "doesn't stop")}, starts at {cal.MinSpeed:0}% ({cal.MinRpm:0} RPM)");
                }
            }
        }
        if (control.Calibration is { } run)
        {
            sb.AppendLine(Inv, $"Last calibration: {run.Key} {run.Status}");
        }
        sb.AppendLine();
    }

    private static void ColorsPart(StringBuilder sb)
    {
        sb.AppendLine("[Colors]");
        if (App.Sensors.Colors is not { } colors)
        {
            sb.AppendLine("No data from the service.");
            sb.AppendLine();
            return;
        }
        if (colors.Lights.Count == 0)
        {
            sb.AppendLine("No supported lights found.");
        }
        foreach (var light in colors.Lights)
        {
            string setting = colors.Settings.TryGetValue(light.Key, out var ls) ? $"set: {ls.Effect}" : "not set";
            sb.AppendLine(Inv, $"{light.Name} · {light.Detail} · {light.Device} ({light.Kind}, {light.Key}): {setting}; effects {string.Join(", ", light.Effects)}");
        }
        if (colors.Sync)
        {
            sb.AppendLine(Inv, $"Sync all: {colors.All?.Effect}");
        }
        foreach (string warning in colors.Warnings)
        {
            sb.AppendLine("Warning: " + warning);
        }
        sb.AppendLine();
    }

    private static void OcPart(StringBuilder sb)
    {
        sb.AppendLine("[OC]");
        if (App.Sensors.Oc is not { } oc)
        {
            sb.AppendLine("No data from the service.");
            sb.AppendLine();
            return;
        }
        if (oc.Gpus.Count == 0)
        {
            sb.AppendLine("No graphics card found.");
        }
        foreach (var gpu in oc.Gpus)
        {
            sb.AppendLine(Inv, $"{gpu.Name} ({gpu.Key})");
            if (gpu.Unsupported is not null)
            {
                sb.AppendLine("    unsupported: " + gpu.Unsupported);
                continue;
            }
            sb.AppendLine(Inv, $"    core {gpu.CoreMin}…{gpu.CoreMax} MHz, memory {gpu.MemoryMin}…{gpu.MemoryMax} MHz, power {gpu.PowerMin:0}…{gpu.PowerMax:0} {gpu.PowerUnit} (default {gpu.PowerDefault:0})");
            sb.AppendLine(Inv, $"    now: core {gpu.CoreOffset:+0;-0;0}, memory {gpu.MemoryOffset:+0;-0;0}, power {gpu.PowerLimit:0} {gpu.PowerUnit}; boost {gpu.BoostClock} MHz, memory {gpu.MemoryClock} MHz");
            if (oc.Settings.TryGetValue(gpu.Key, out var saved))
            {
                sb.AppendLine(Inv, $"    saved: core {saved.CoreOffset:+0;-0;0}, memory {saved.MemoryOffset:+0;-0;0}, power {(saved.PowerLimit is { } p ? p.ToString("0", Inv) + " " + gpu.PowerUnit : "default")}, at startup {(saved.ApplyAtStartup ? "yes" : "no")}");
            }
            if (oc.Notices.TryGetValue(gpu.Key, out var notice))
            {
                sb.AppendLine("    notice: " + notice);
            }
        }
        sb.AppendLine();
    }

    // ----- The PC (background thread) -----

    private static string SystemPart()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Windows]");
        using (var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            string build = nt?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(Inv);
            string name = int.TryParse(build, out int b) && b >= 22000 ? "Windows 11" : "Windows 10";
            string edition = nt?.GetValue("EditionID") as string ?? "";
            string display = nt?.GetValue("DisplayVersion") as string ?? "";
            sb.AppendLine(Inv, $"{name} {edition} {display}, build {build}.{nt?.GetValue("UBR")}");
        }
        sb.AppendLine("Smart App Control: " + (DwordAt(@"SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState") switch
        {
            0 => "off",
            1 => "ON (blocks NotOnlyFans)",
            2 => "evaluation (may turn on and block NotOnlyFans)",
            _ => "unknown",
        }));
        sb.AppendLine("PawnIO driver: " + (ServiceExists("PawnIO") ? "installed" : "MISSING (no CPU and motherboard sensors)"));
        sb.AppendLine("NotOnlyFans service: " + ServiceFile());
        var running = OtherSoftware
            .Where(p => IsRunning(p.Process))
            .Select(p => p.Name)
            .Distinct()
            .ToList();
        sb.AppendLine("Other fan, RGB or sensor software running: " + (running.Count == 0 ? "none found" : string.Join(", ", running)));
        sb.AppendLine();
        return sb.ToString();
    }

    private static int? DwordAt(string key, string value)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(value) as int?;
    }

    private static bool ServiceExists(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return k is not null;
    }

    /// <summary>Installed or not, and the version and date of the service's file.</summary>
    private static string ServiceFile()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NotOnlyFans");
        if (k?.GetValue("ImagePath") is not string image)
        {
            return "not installed";
        }
        string path = image.Trim().Trim('"');
        int exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            path = path[..(exe + 4)];
        }
        string start = k.GetValue("Start") is 2 ? "automatic" : k.GetValue("Start") is 4 ? "DISABLED" : "manual";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Create(Inv, $"installed ({start}), {info.FileVersion}, built {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"installed ({start}), file not readable";
        }
    }

    private static bool IsRunning(string process)
    {
        var found = Process.GetProcessesByName(process);
        foreach (var p in found)
        {
            p.Dispose();
        }
        return found.Length > 0;
    }

    // ----- Recent errors (background thread) -----

    private static string ErrorsPart()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Recent errors]");
        int count = 0;
        try
        {
            // Last 14 days: warnings and errors of the service, and crashes of the app or the service.
            const string query =
                "*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= 1209600000] and " +
                "(Provider[@Name='NotOnlyFans.Service'] or Provider[@Name='NotOnlyFans'] or " +
                "Provider[@Name='.NET Runtime'] or Provider[@Name='Application Error'])]]";
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query) { ReverseDirection = true });
            for (var e = reader.ReadEvent(); e is not null && count < 15; e = reader.ReadEvent())
            {
                using (e)
                {
                    string message = e.FormatDescription() ?? "";
                    // Crash reports name the program first ("Application: X.exe", "Faulting application name: X.exe").
                    bool ours = e.ProviderName.StartsWith("NotOnlyFans", StringComparison.Ordinal)
                                || OurCrash().IsMatch(message);
                    if (!ours)
                    {
                        continue;
                    }
                    count++;
                    message = message.Replace("Category: NotOnlyFans.Service.SensorWorker", "").Replace("EventId: 0", "");
                    int max = e.ProviderName == "Application Error" ? 300 : 1500;   // the .NET Runtime entry next to it has the reason
                    sb.AppendLine(Inv, $"{e.TimeCreated:yyyy-MM-dd HH:mm:ss} {e.ProviderName} {e.LevelDisplayName}: {Shorten(message, max)}");
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            sb.AppendLine("Event log not readable: " + ex.Message);
        }
        if (count == 0)
        {
            sb.AppendLine("None in the event log (14 days).");
        }

        string log = Path.Combine(AppFiles.Folder, "crash.log");
        try
        {
            if (File.Exists(log))
            {
                string text = File.ReadAllText(log);
                sb.AppendLine();
                sb.AppendLine("App crash.log (end):");
                sb.AppendLine(text.Length > 6000 ? "…" + text[^6000..] : text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sb.AppendLine("crash.log not readable: " + ex.Message);
        }
        return sb.ToString();
    }

    private static string Shorten(string text, int max)
    {
        text = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0));
        return text.Length > max ? text[..max] + "…" : text;
    }

    [GeneratedRegex(@"^(Application|Faulting application name): NotOnlyFans(\.Service)?\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex OurCrash();

    /// <summary>Takes out the user's folder, user name and computer name.</summary>
    private static string Scrub(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3)
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        foreach (string name in new[] { Environment.UserName, Environment.MachineName })
        {
            if (name.Length >= 2)
            {
                text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])", "<name>", RegexOptions.IgnoreCase);
            }
        }
        return text;
    }
}
