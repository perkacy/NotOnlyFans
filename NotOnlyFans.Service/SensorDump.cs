using System.Globalization;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace NotOnlyFans.Service;

/// <summary>
/// Stage 1a probe: lists every piece of hardware and every sensor LibreHardwareMonitor can see
/// and saves the list to docs\sensors-dump.txt. Read-only.
/// </summary>
internal static class SensorDump
{
    public static void Run()
    {
        var computer = Hardware.CreateComputer();

        var sb = new StringBuilder();
        sb.AppendLine($"NotOnlyFans sensor dump · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Running as administrator: {Hardware.IsElevated()}");
        sb.AppendLine($"Windows: {Environment.OSVersion.VersionString}");
        sb.AppendLine();

        try
        {
            computer.Open();

            // Two passes: some sensors only have a value after the second read.
            computer.Accept(new UpdateVisitor());
            Thread.Sleep(1000);
            computer.Accept(new UpdateVisitor());

            foreach (var hardware in computer.Hardware)
            {
                Dump(sb, hardware, 0);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("ERROR: " + ex);
        }
        finally
        {
            computer.Close();
        }

        string report = sb.ToString();
        Console.WriteLine(report);

        string? root = FindProjectRoot();
        if (root is not null)
        {
            string path = Path.Combine(root, "docs", "sensors-dump.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report, Encoding.UTF8);
            Console.WriteLine($"Saved to {path}");
        }
        else
        {
            Console.WriteLine("Could not find the project folder (the one with NotOnlyFans.Contracts) - nothing saved.");
        }

        Console.WriteLine();
        Console.WriteLine("Done. Press Enter to close.");
        Console.ReadLine();
    }

    private static void Dump(StringBuilder sb, IHardware hardware, int depth)
    {
        string indent = new string(' ', depth * 4);
        sb.AppendLine($"{indent}[{hardware.HardwareType}] {hardware.Name}   id={hardware.Identifier}");

        foreach (var sensor in hardware.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
        {
            string control = sensor.Control is null ? "" : "   CONTROL";
            sb.AppendLine(
                $"{indent}    {sensor.SensorType,-12} {sensor.Name,-36} " +
                $"now={Format(sensor.Value),-10} min={Format(sensor.Min),-10} max={Format(sensor.Max),-10} " +
                $"id={sensor.Identifier}{control}");
        }

        foreach (var sub in hardware.SubHardware)
        {
            Dump(sb, sub, depth + 1);
        }

        sb.AppendLine();
    }

    private static string Format(float? value) =>
        value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-";

    private static string? FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "NotOnlyFans.Contracts")))   // the repository root
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
