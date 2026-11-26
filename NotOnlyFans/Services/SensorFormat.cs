using System.Globalization;

namespace NotOnlyFans.Services;

/// <summary>Turns a raw sensor value into text with its unit ("47 °C", "827 RPM", "4.98 GHz").</summary>
public static class SensorFormat
{
    public const string NoValue = "—";

    public static string Format(string kind, float? value)
    {
        if (value is not { } v)
        {
            return NoValue;
        }

        var c = CultureInfo.CurrentCulture;
        return kind switch
        {
            "Temperature" => string.Create(c, $"{v:0} °C"),
            "Fan" => string.Create(c, $"{v:0} RPM"),
            "Control" or "Load" or "Level" or "Humidity" => string.Create(c, $"{v:0}%"),
            "Voltage" => string.Create(c, $"{v:0.000} V"),
            "Current" => string.Create(c, $"{v:0.00} A"),
            "Clock" => v >= 1000 ? string.Create(c, $"{v / 1000:0.00} GHz") : string.Create(c, $"{v:0} MHz"),
            "Power" => string.Create(c, $"{v:0.0} W"),
            "Energy" => string.Create(c, $"{v:0} mWh"),
            "Data" => string.Create(c, $"{v:0.0} GB"),
            "SmallData" => string.Create(c, $"{v:0} MB"),
            "Throughput" => Rate(v, c),
            "Frequency" => string.Create(c, $"{v:0} Hz"),
            "Flow" => string.Create(c, $"{v:0} L/h"),
            "Noise" => string.Create(c, $"{v:0} dBA"),
            "Timing" => string.Create(c, $"{v:0.##} ns"),
            _ => v.ToString("0.##", c),
        };
    }

    private static string Rate(float bytesPerSecond, CultureInfo c) => bytesPerSecond switch
    {
        >= 1024 * 1024 => string.Create(c, $"{bytesPerSecond / (1024 * 1024):0.0} MB/s"),
        >= 1024 => string.Create(c, $"{bytesPerSecond / 1024:0} KB/s"),
        _ => string.Create(c, $"{bytesPerSecond:0} B/s"),
    };
}
