using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service.Lights;

/// <summary>
/// ASRock Polychrome USB lighting controllers on motherboards (protocol as documented by the OpenRGB project; this is
/// our own implementation, not yet tried on real hardware, so its lights are marked experimental).
/// 65-byte output reports without a report id; the controller answers every packet with one input report.
/// It tells which of its eight zones exist (0x14), and each zone's effect can be set (0x10) and read back (0x11);
/// 0x12 stores everything. Colors go green-red-blue unless the zone is marked as swapped.
/// </summary>
internal sealed class AsrockPolychromeLight : ILightDevice
{
    private const ushort Vendor = 0x26CE;
    private const ushort Product = 0x01A2;
    private const byte NotPresent = 0x1E;

    private const byte ModeOff = 0x00;
    private const byte ModeStatic = 0x01;
    private const byte ModeBreathing = 0x02;
    private const byte ModeSpectrumCycle = 0x04;
    private const byte ModeRainbow = 0x0E;

    private static readonly Dictionary<byte, string> OtherModes = new()
    {
        [0x03] = "Strobe", [0x05] = "Random", [0x06] = "Music", [0x07] = "Wave", [0x08] = "Spring", [0x09] = "Stack",
        [0x0A] = "Cram", [0x0B] = "Scan", [0x0C] = "Neon", [0x0D] = "Water", [0x0F] = "Per-LED",
    };

    /// <summary>The controller's zones by number: name, detail, kind, addressable.</summary>
    private static readonly (string Id, string Name, string Detail, string Kind, bool Addressable)[] ZoneTable =
    [
        ("rgb1", "RGB header 1", "RGB LED header 1 · 12 V", "Header", false),
        ("rgb2", "RGB header 2", "RGB LED header 2 · 12 V", "Header", false),
        ("argb1", "ARGB header 1", "Addressable header 1 · 5 V", "Header", true),
        ("argb2", "ARGB header 2", "Addressable header 2 · 5 V", "Header", true),
        ("pch", "Chipset", "Light on the chipset cover", "Board", false),
        ("io", "I/O cover", "Light on the I/O cover", "Board", false),
        ("pcb", "Board edge", "Lights on the board", "Board", false),
        ("argb3", "ARGB header 3", "Addressable header 3 · 5 V", "Header", true),
    ];

    private sealed record Zone(byte Number, LightInfo Info, bool Swapped, bool Addressable);

    private readonly HidPort _port;
    private readonly List<Zone> _zones = [];

    private AsrockPolychromeLight(HidPort port) => _port = port;

    public IReadOnlyList<LightInfo> Lights => [.. _zones.Select(z => z.Info)];

    public static AsrockPolychromeLight? TryOpen()
    {
        foreach (var (path, pid) in Hid.Find(Vendor))
        {
            if (pid != Product || HidPort.Open(path, 65) is not { } port)
            {
                continue;
            }
            try
            {
                if (Setup(port) is { } device)
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

    private static AsrockPolychromeLight? Setup(HidPort port)
    {
        // Replies carry a leading report id 0, so field n of OpenRGB's description is byte n + 1 here.
        var counts = ReadHeader(port, 0x02);
        var swapped = ReadHeader(port, 0x03);
        var present = ReadHeader(port, 0x01);
        if (counts is null || swapped is null || present is null)
        {
            return null;
        }
        var device = new AsrockPolychromeLight(port);
        string board = Hid.BoardName() is { } name ? $"ASRock {name}" : "ASRock motherboard";
        for (byte i = 0; i < ZoneTable.Length; i++)
        {
            byte count = counts[5 + i];
            if (count == NotPresent || (present[5] & (1 << i)) == 0)
            {
                continue;
            }
            if (count > 100)
            {
                return null;   // not the answer we expect: leave this controller alone
            }
            var z = ZoneTable[i];
            device._zones.Add(new Zone(i, new LightInfo
            {
                Key = $"asrock-{Product:x4}/{z.Id}",
                Name = z.Name,
                Detail = z.Detail,
                Device = board,
                Kind = z.Kind,
                Effects = [LightEffect.Static, LightEffect.Breathing, LightEffect.Rainbow, LightEffect.Temperature, LightEffect.Off],
                RainbowBrightnessFixed = true,
                Experimental = true,
            }, (swapped[5] & (1 << i)) != 0, z.Addressable));
        }
        return device._zones.Count > 0 ? device : null;
    }

    private static byte[]? ReadHeader(HidPort port, byte which)
    {
        var request = new byte[65];
        request[1] = 0x14;
        request[3] = which;
        port.Flush();
        port.Write(request);
        return port.Read() is { Length: >= 13 } reply ? reply : null;
    }

    public void Refresh()
    {
        foreach (var zone in _zones)
        {
            var request = new byte[65];
            request[1] = 0x11;
            request[3] = zone.Number;
            _port.Flush();
            _port.Write(request);
            if (_port.Read() is not { Length: >= 10 } reply)
            {
                continue;
            }
            byte mode = reply[5];
            var (r, g, b) = zone.Swapped ? (reply[6], reply[7], reply[8]) : (reply[7], reply[6], reply[8]);
            LightEffect? known = mode switch
            {
                ModeOff => LightEffect.Off,
                ModeStatic => LightEffect.Static,
                ModeBreathing => LightEffect.Breathing,
                ModeSpectrumCycle or ModeRainbow => LightEffect.Rainbow,
                _ => null,
            };
            if (known is { } k)
            {
                zone.Info.Found = new LightSetting { Effect = k, Color = LightColor.ToHex(r, g, b), Speed = SpeedOf(reply[9]) };
                zone.Info.FoundOther = null;
            }
            else
            {
                zone.Info.Found = null;
                zone.Info.FoundOther = OtherModes.GetValueOrDefault(mode, "Per-LED");
            }
        }
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
            byte mode = output.Effect switch
            {
                LightEffect.Off => ModeOff,
                LightEffect.Breathing => ModeBreathing,
                LightEffect.Rainbow => zone.Addressable ? ModeRainbow : ModeSpectrumCycle,
                _ => ModeStatic,
            };
            var (r, g, b) = output.Effect is LightEffect.Off or LightEffect.Rainbow ? ((byte)0, (byte)0, (byte)0) : output.Dimmed();
            var p = new byte[65];
            p[1] = 0x10;
            p[3] = zone.Number;
            p[4] = mode;
            (p[5], p[6], p[7]) = zone.Swapped ? (r, g, b) : (g, r, b);
            p[8] = output.Speed switch { LightSpeed.Slow => 0xFF, LightSpeed.Medium => 0xE0, _ => 0xA0 };   // lower is faster
            p[9] = 0xFF;
            Send(p);
            any = true;
        }
        if (any && save)
        {
            var commit = new byte[65];
            commit[1] = 0x12;
            Send(commit);
        }
    }

    private static LightSpeed SpeedOf(byte value) => value switch { >= 0xF0 => LightSpeed.Slow, >= 0xC0 => LightSpeed.Medium, _ => LightSpeed.Fast };

    /// <summary>Every packet is answered; waiting for it keeps the controller from being flooded.</summary>
    private void Send(byte[] packet)
    {
        _port.Flush();
        _port.Write(packet);
        _port.Read();
    }

    public void Dispose() => _port.Dispose();
}
