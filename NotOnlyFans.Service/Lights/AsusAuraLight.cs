using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Lights;

/// <summary>
/// ASUS Aura USB lighting controllers on motherboards (protocol as documented by the OpenRGB project; this is
/// our own implementation, not yet tried on real hardware, so its lights are marked experimental).
/// 65-byte output reports with id 0xEC. The controller tells its firmware (0x82) and a configuration table (0xB0):
/// how many addressable (ARGB) headers there are, and how many LEDs the board itself has, its 12 V RGB headers included.
/// <list type="bullet">
/// <item>Mainboard controllers: effect per channel (0x35), its color through an LED mask (0x36), store (0x3F 0x55).</item>
/// <item>Addressable-only controllers: effect and color per header in one packet (0x3B); nothing to store.</item>
/// </list>
/// Aura effects have no speed or brightness: colors are dimmed by us, the rainbow runs at full brightness.
/// </summary>
internal sealed class AsusAuraLight : ILightDevice
{
    private const ushort Vendor = 0x0B05;
    private const byte ReportId = 0xEC;

    private static readonly ushort[] Mainboards = [0x18F3, 0x1939, 0x19AF, 0x1AA6, 0x1BED];
    private static readonly ushort[] AddressableOnly = [0x1867, 0x1872, 0x18A3, 0x18A5];

    private const byte ModeOff = 0;
    private const byte ModeStatic = 1;
    private const byte ModeBreathing = 2;
    private const byte ModeSpectrumCycle = 4;
    private const byte ModeRainbow = 5;

    /// <summary>One light: an effect channel and its LEDs in the color mask (mainboard controllers).</summary>
    private sealed record Zone(LightInfo Info, byte Channel, int Start, int Count, bool Addressable);

    private readonly HidPort _port;
    private readonly bool _mainboard;
    private readonly List<Zone> _zones = [];
    private readonly Dictionary<string, LightOutput> _shown = [];
    private bool _started;

    private AsusAuraLight(HidPort port, bool mainboard)
    {
        _port = port;
        _mainboard = mainboard;
    }

    public IReadOnlyList<LightInfo> Lights => [.. _zones.Select(z => z.Info)];

    public static AsusAuraLight? TryOpen()
    {
        foreach (var (path, pid) in Hid.Find(Vendor))
        {
            bool mainboard = Mainboards.Contains(pid);
            if (!mainboard && !AddressableOnly.Contains(pid))
            {
                continue;
            }
            // Vendor-defined interface only (0x19AF also has others; its lighting one is 0xFF72 / 0xA1).
            var port = HidPort.Open(path, 65, c => c.UsagePage >= 0xFF00 && (pid != 0x19AF || (c.UsagePage == 0xFF72 && c.Usage == 0xA1)));
            if (port is null)
            {
                continue;
            }
            try
            {
                if (Setup(port, pid, mainboard) is { } device)
                {
                    return device;
                }
            }
            catch (IOException)
            {
            }
            port.Dispose();
        }
        return null;
    }

    private static AsusAuraLight? Setup(HidPort port, ushort pid, bool mainboard)
    {
        var firmwareReply = port.Ask([ReportId, 0x82], r => r.Length > 18 && r[1] == 0x02);
        var configReply = port.Ask([ReportId, 0xB0], r => r.Length >= 64 && r[1] == 0x30);
        if (firmwareReply is null || configReply is null)
        {
            return null;
        }
        string firmware = System.Text.Encoding.ASCII.GetString(firmwareReply, 2, 16).TrimEnd('\0', ' ');
        var config = configReply.AsSpan(4);
        int argb = Math.Min((int)config[0x02], 8);
        int boardLeds = config[0x1B];
        int rgbHeaders = config[0x1D] > boardLeds ? 0 : config[0x1D];

        var device = new AsusAuraLight(port, mainboard);
        string board = Hid.BoardName() is { } name ? $"ASUS {name}" : "ASUS motherboard";
        string prefix = $"asus-{pid:x4}/";
        LightInfo Info(string id, string lightName, string detail, string kind, bool addressable) => new()
        {
            Key = prefix + id,
            Name = lightName,
            Detail = detail,
            Device = board,
            Kind = kind,
            Effects = [LightEffect.Static, LightEffect.Breathing, LightEffect.Rainbow, LightEffect.Temperature, LightEffect.Off],
            SpeedFixed = true,
            RainbowBrightnessFixed = true,
            Experimental = true,
        };

        if (mainboard)
        {
            // Firmware AULA3-AR32 numbers the ARGB headers first, the board after them.
            bool ar32 = firmware.StartsWith("AULA3-AR32", StringComparison.Ordinal);
            if (boardLeds > 0)
            {
                int onboard = boardLeds - rgbHeaders;
                string detail = rgbHeaders switch
                {
                    0 => "Lights on the board",
                    1 => onboard > 0 ? "Lights on the board and the RGB header (12 V)" : "RGB header (12 V)",
                    _ => onboard > 0 ? $"Lights on the board and {rgbHeaders} RGB headers (12 V)" : $"{rgbHeaders} RGB headers (12 V)",
                };
                device._zones.Add(new Zone(Info("board", onboard > 0 ? "Motherboard" : "RGB headers", detail, "Board", false),
                    (byte)(ar32 ? argb : 0), 0, Math.Min(boardLeds, 16), false));
            }
            for (int i = 0; i < argb; i++)
            {
                int start = (boardLeds > 0 ? boardLeds : 0) + i;   // each ARGB header takes one place in the color mask
                if (start >= 16)
                {
                    break;
                }
                byte channel = (byte)(ar32 ? i : boardLeds > 0 ? 1 + i : i);
                device._zones.Add(new Zone(Info($"argb{i + 1}", $"ARGB header {i + 1}", $"ARGB header {i + 1} · 5 V", "Header", true),
                    channel, start, 1, true));
            }
        }
        else
        {
            for (int i = 0; i < argb; i++)
            {
                device._zones.Add(new Zone(Info($"argb{i + 1}", $"ARGB header {i + 1}", $"ARGB header {i + 1} · 5 V", "Header", true),
                    (byte)i, 0, 0, true));
            }
        }
        return device._zones.Count > 0 ? device : null;
    }

    /// <summary>The controller can't tell what it shows.</summary>
    public void Refresh()
    {
    }

    public void Apply(IReadOnlyDictionary<string, LightOutput> outputs, bool save)
    {
        bool any = false;
        foreach (var zone in _zones)
        {
            if (!outputs.TryGetValue(zone.Info.Key, out var output))
            {
                continue;
            }
            if (_mainboard && !_started)
            {
                _port.Write([ReportId, 0x52, 0x53, 0x00, 0x01]);   // what OpenRGB sends first to mainboard controllers
                _started = true;
            }
            try
            {
                Show(zone, output);
            }
            catch
            {
                _shown.Remove(zone.Info.Key);   // unknown now: send everything next time
                throw;
            }
            _shown[zone.Info.Key] = output;
            any = true;
        }
        if (any && save && _mainboard)
        {
            _port.Write([ReportId, 0x3F, 0x55]);
        }
    }

    private void Show(Zone zone, LightOutput output)
    {
        byte mode = output.Effect switch
        {
            LightEffect.Off => ModeOff,
            LightEffect.Breathing => ModeBreathing,
            LightEffect.Rainbow => zone.Addressable ? ModeRainbow : ModeSpectrumCycle,
            _ => ModeStatic,
        };
        var (r, g, b) = output.Effect is LightEffect.Off or LightEffect.Rainbow ? ((byte)0, (byte)0, (byte)0) : output.Dimmed();

        if (!_mainboard)
        {
            _port.Write([ReportId, 0x3B, zone.Channel, 0x00, mode, r, g, b]);
            return;
        }

        // Temperature colors change every second: then only the color is sent.
        if (!_shown.TryGetValue(zone.Info.Key, out var shown) || shown.Effect != output.Effect)
        {
            _port.Write([ReportId, 0x35, zone.Channel, 0x00, 0x00, mode]);
        }
        int mask = ((1 << zone.Count) - 1) << zone.Start;
        var packet = new byte[65];
        packet[0] = ReportId;
        packet[1] = 0x36;
        packet[2] = (byte)(mask >> 8);
        packet[3] = (byte)mask;
        packet[4] = 0x00;
        for (int i = zone.Start; i < zone.Start + zone.Count; i++)
        {
            packet[5 + 3 * i] = r;
            packet[6 + 3 * i] = g;
            packet[7 + 3 * i] = b;
        }
        _port.Write(packet);
    }

    public void Dispose() => _port.Dispose();
}
